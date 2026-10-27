"""S1.3: export and PE analysis of the pinned prebuilt PDFium DLLs (x64, arm64, v8-x64).

Reports machine type, export count, export diffs between variants, file size and
deflate-compressed size (proxy for MSIX), security flags (DllCharacteristics, load config
GuardFlags, CETCOMPAT in the extended-DLL-characteristics debug entry), imported DLLs and the
section table.

Outputs (spikes/S1/out/): pe_summary.json, pe_summary.csv, exports-<variant>.txt, export-diff.txt
Run:  .venv\\Scripts\\python symdiff.py
(Replaces the dumpbin-based S1.3 from the plan: dumpbin is not installed on this machine.)
"""
import csv
import io
import json
import struct
import sys
import zipfile
from pathlib import Path

import pefile

from peutil import EXTRA, OUT, REPO, VARIANTS, dll_path, exported_names

MACHINES = {0x8664: "AMD64 (x64)", 0xAA64: "ARM64 (AA64)", 0x14C: "I386", 0x1C4: "ARMNT"}

DLL_CHARS = {  # IMAGE_DLLCHARACTERISTICS_*
    0x0020: "HIGH_ENTROPY_VA", 0x0040: "DYNAMIC_BASE", 0x0080: "FORCE_INTEGRITY",
    0x0100: "NX_COMPAT", 0x0200: "NO_ISOLATION", 0x0400: "NO_SEH", 0x0800: "NO_BIND",
    0x1000: "APPCONTAINER", 0x2000: "WDM_DRIVER", 0x4000: "GUARD_CF",
    0x8000: "TERMINAL_SERVER_AWARE",
}
GUARD_FLAGS = {  # IMAGE_GUARD_* (winnt.h)
    0x00000100: "CF_INSTRUMENTED", 0x00000200: "CFW_INSTRUMENTED",
    0x00000400: "CF_FUNCTION_TABLE_PRESENT", 0x00000800: "SECURITY_COOKIE_UNUSED",
    0x00001000: "PROTECT_DELAYLOAD_IAT", 0x00004000: "CF_EXPORT_SUPPRESSION_INFO_PRESENT",
    0x00008000: "CF_ENABLE_EXPORT_SUPPRESSION", 0x00010000: "CF_LONGJUMP_TABLE_PRESENT",
    0x00020000: "RF_INSTRUMENTED", 0x00040000: "RF_ENABLE", 0x00080000: "RF_STRICT",
    0x00100000: "RETPOLINE_PRESENT", 0x00400000: "EH_CONTINUATION_TABLE_PRESENT",
    0x00800000: "XFG_ENABLED", 0x01000000: "CASTGUARD_PRESENT", 0x02000000: "MEMCPY_PRESENT",
}
IMAGE_DEBUG_TYPE_EX_DLLCHARACTERISTICS = 20
EX_DLLCHAR_CET_COMPAT = 0x1
EX_DLLCHAR_CET_COMPAT_STRICT_MODE = 0x2


def decode(value, table):
    return [name for bit, name in table.items() if value & bit]


def deflate_size(path: Path, level=9) -> int:
    buf = io.BytesIO()
    with zipfile.ZipFile(buf, "w", zipfile.ZIP_DEFLATED, compresslevel=level) as z:
        z.write(path, "pdfium.dll")
    return buf.tell()


def analyse(label: str) -> dict:
    path = dll_path(label)
    pe = pefile.PE(str(path))
    oh, fh = pe.OPTIONAL_HEADER, pe.FILE_HEADER
    r = {
        "variant": label,
        "file": str(path.relative_to(REPO) if path.is_relative_to(REPO) else path).replace("\\", "/"),
        "machine_hex": hex(fh.Machine),
        "machine": MACHINES.get(fh.Machine, "?"),
        "pe_magic": hex(oh.Magic),
        "size_bytes": path.stat().st_size,
        "deflate9_bytes": deflate_size(path, 9),
        "deflate6_bytes": deflate_size(path, 6),
        "linker_version": f"{oh.MajorLinkerVersion}.{oh.MinorLinkerVersion}",
        "os_version_min": f"{oh.MajorOperatingSystemVersion}.{oh.MinorOperatingSystemVersion}",
        "subsystem_version": f"{oh.MajorSubsystemVersion}.{oh.MinorSubsystemVersion}",
        "timestamp_hex": hex(fh.TimeDateStamp),
        "image_base": hex(oh.ImageBase),
        "size_of_image": oh.SizeOfImage,
        "checksum_in_header": hex(oh.CheckSum),
        "authenticode_present": oh.DATA_DIRECTORY[pefile.DIRECTORY_ENTRY["IMAGE_DIRECTORY_ENTRY_SECURITY"]].Size > 0,
        "dll_characteristics_hex": hex(oh.DllCharacteristics),
        "dll_characteristics": decode(oh.DllCharacteristics, DLL_CHARS),
        "sections": [
            {"name": s.Name.rstrip(b"\0").decode(), "virtual_size": s.Misc_VirtualSize,
             "raw_size": s.SizeOfRawData, "characteristics": hex(s.Characteristics)}
            for s in pe.sections
        ],
    }

    # ---- exports
    pe.parse_data_directories([pefile.DIRECTORY_ENTRY["IMAGE_DIRECTORY_ENTRY_EXPORT"]])
    syms = pe.DIRECTORY_ENTRY_EXPORT.symbols
    r["export_total_entries"] = len(syms)
    r["export_named"] = sum(1 for s in syms if s.name)
    r["export_ordinal_only"] = sum(1 for s in syms if not s.name)
    r["export_forwarders"] = sum(1 for s in syms if s.forwarder)

    # ---- load config (GuardFlags, cookie, XFG, EH continuation ...)
    lc = getattr(pe, "DIRECTORY_ENTRY_LOAD_CONFIG", None)
    if lc is not None:
        s = lc.struct
        g = lambda n: getattr(s, n, None)  # noqa: E731
        r["load_config"] = {
            "size": g("Size"),
            "security_cookie_va": hex(g("SecurityCookie") or 0),   # /GS cookie
            "guard_flags_hex": hex(g("GuardFlags") or 0),
            "guard_flags": decode(g("GuardFlags") or 0, GUARD_FLAGS),
            "guard_cf_function_count": g("GuardCFFunctionCount"),
            "guard_cf_check_fn_ptr": hex(g("GuardCFCheckFunctionPointer") or 0),
            "guard_cf_dispatch_fn_ptr": hex(g("GuardCFDispatchFunctionPointer") or 0),
            "guard_longjump_target_count": g("GuardLongJumpTargetCount"),
            "guard_eh_continuation_count": g("GuardEHContinuationCount"),
            "guard_xfg_check_fn_ptr": hex(g("GuardXFGCheckFunctionPointer") or 0),
            "guard_rf_failure_routine": hex(g("GuardRFFailureRoutine") or 0),
            "se_handler_count": g("SEHandlerCount"),
        }
    else:
        r["load_config"] = None

    # ---- CET: IMAGE_DEBUG_TYPE_EX_DLLCHARACTERISTICS (type 20) holds a DWORD of EX flags
    cet = {"debug_entry_present": False, "ex_dll_characteristics_hex": None,
           "cet_compat": False, "cet_strict": False}
    pe.parse_data_directories([pefile.DIRECTORY_ENTRY["IMAGE_DIRECTORY_ENTRY_DEBUG"]])
    r["debug_types"] = []
    for d in getattr(pe, "DIRECTORY_ENTRY_DEBUG", []):
        r["debug_types"].append(d.struct.Type)
        if d.struct.Type == IMAGE_DEBUG_TYPE_EX_DLLCHARACTERISTICS:
            raw = pe.get_data(d.struct.AddressOfRawData, 4)
            v = struct.unpack("<I", raw)[0]
            cet.update(debug_entry_present=True, ex_dll_characteristics_hex=hex(v),
                       cet_compat=bool(v & EX_DLLCHAR_CET_COMPAT),
                       cet_strict=bool(v & EX_DLLCHAR_CET_COMPAT_STRICT_MODE))
    r["cet"] = cet

    # ---- PDB name (CodeView) and version resource: provenance of the binary
    for d in getattr(pe, "DIRECTORY_ENTRY_DEBUG", []):
        if d.struct.Type == 2:   # IMAGE_DEBUG_TYPE_CODEVIEW (RSDS: 4 sig + 16 guid + 4 age + path)
            r["pdb_name"] = pe.get_data(d.struct.AddressOfRawData, d.struct.SizeOfData)[24:].split(b"\x00")[0].decode()
    pe.parse_data_directories([pefile.DIRECTORY_ENTRY["IMAGE_DIRECTORY_ENTRY_RESOURCE"]])
    r["version_info"] = {}
    for fi in getattr(pe, "FileInfo", [[]])[0]:
        if getattr(fi, "Key", b"") == b"StringFileInfo":
            for st in fi.StringTable:
                r["version_info"] = {k.decode(): v.decode() for k, v in st.entries.items()}

    # ---- imports (S3 cares: user32/gdi32 imply win32k dependence)
    pe.parse_data_directories([pefile.DIRECTORY_ENTRY["IMAGE_DIRECTORY_ENTRY_IMPORT"],
                               pefile.DIRECTORY_ENTRY["IMAGE_DIRECTORY_ENTRY_DELAY_IMPORT"]])
    imps = {}
    for e in getattr(pe, "DIRECTORY_ENTRY_IMPORT", []):
        imps[e.dll.decode().lower()] = sorted(i.name.decode() for i in e.imports if i.name)
    r["imports"] = {k: len(v) for k, v in sorted(imps.items())}
    r["imports_user32_gdi32"] = {k: v for k, v in imps.items() if k in ("user32.dll", "gdi32.dll")}
    r["delay_imports"] = [e.dll.decode().lower() for e in getattr(pe, "DIRECTORY_ENTRY_DELAY_IMPORT", [])]
    pe.close()
    return r


def verdict(r: dict) -> dict:
    """Which section 13.2 hardening flags the binary has."""
    dc, lc = set(r["dll_characteristics"]), r["load_config"] or {}
    gf = set(lc.get("guard_flags", []))
    return {
        "ASLR (DYNAMIC_BASE)": "DYNAMIC_BASE" in dc,
        "High-entropy VA": "HIGH_ENTROPY_VA" in dc,
        "DEP (NX_COMPAT)": "NX_COMPAT" in dc,
        "CFG header bit (GUARD_CF)": "GUARD_CF" in dc,
        "CFG instrumented + function table": {"CF_INSTRUMENTED", "CF_FUNCTION_TABLE_PRESENT"} <= gf,
        "CFG export suppression": "CF_ENABLE_EXPORT_SUPPRESSION" in gf,
        "XFG (/guard:xfg)": "XFG_ENABLED" in gf,
        "EH continuation (/guard:ehcont)": "EH_CONTINUATION_TABLE_PRESENT" in gf,
        "CET shadow stack compat (/CETCOMPAT)": r["cet"]["cet_compat"],
        "/GS security cookie": int(lc.get("security_cookie_va", "0x0"), 16) != 0,
        "Retpoline": "RETPOLINE_PRESENT" in gf,
    }


def main():
    OUT.mkdir(exist_ok=True)
    args = sys.argv[1:]
    for a, b in zip(args, args[1:]):
        if a == "--extra":
            label, _, p = b.partition("=")
            EXTRA[label] = Path(p)
    labels = list(VARIANTS) + list(EXTRA)
    res = {k: analyse(k) for k in labels}
    names = {k: exported_names(dll_path(k)) for k in labels}
    for k, n in names.items():
        (OUT / f"exports-{k}.txt").write_text("\n".join(sorted(n)) + "\n")

    diffs = {
        "x64_vs_arm64": {"only_x64": sorted(names["x64"] - names["arm64"]),
                         "only_arm64": sorted(names["arm64"] - names["x64"])},
        "v8x64_vs_x64": {"added_by_v8": sorted(names["v8-x64"] - names["x64"]),
                         "removed_by_v8": sorted(names["x64"] - names["v8-x64"])},
    }
    for k in EXTRA:   # own build vs the prebuilt with the same machine type and V8 flavour
        base = next((v for v in VARIANTS if res[v]["machine"] == res[k]["machine"]
                     and ("v8" in v) == ("v8" in k)), None)
        if base:
            diffs[f"{k}_vs_{base}"] = {f"only_{k}": sorted(names[k] - names[base]),
                                       f"only_{base}": sorted(names[base] - names[k])}
    lines = []
    for title, d in diffs.items():
        lines.append(f"== {title}")
        for k, v in d.items():
            lines.append(f"  {k}: {len(v)}")
            lines += [f"    {x}" for x in v]
    (OUT / "export-diff.txt").write_text("\n".join(lines) + "\n")

    hardening = {k: verdict(r) for k, r in res.items()}
    (OUT / "pe_summary.json").write_text(json.dumps(
        {"variants": res, "export_diffs": diffs, "hardening": hardening}, indent=2))

    cols = ["variant", "machine", "size_bytes", "deflate6_bytes", "deflate9_bytes",
            "export_named", "export_total_entries", "linker_version", "dll_characteristics_hex",
            "guard_flags_hex", "ex_dll_characteristics_hex"]
    with open(OUT / "pe_summary.csv", "w", newline="") as fh:
        w = csv.writer(fh)
        w.writerow(cols)
        for r in res.values():
            w.writerow([r["variant"], r["machine"], r["size_bytes"], r["deflate6_bytes"],
                        r["deflate9_bytes"], r["export_named"], r["export_total_entries"],
                        r["linker_version"], r["dll_characteristics_hex"],
                        (r["load_config"] or {}).get("guard_flags_hex"),
                        r["cet"]["ex_dll_characteristics_hex"]])

    # ---- console
    mb = lambda b: f"{b / 1048576:.2f} MiB"  # noqa: E731
    for k, r in res.items():
        print(f"[{k}] {r['machine']} ({r['machine_hex']})  size={r['size_bytes']} ({mb(r['size_bytes'])})  "
              f"deflate9={r['deflate9_bytes']} ({mb(r['deflate9_bytes'])}) deflate6={r['deflate6_bytes']}")
        print(f"      exports named={r['export_named']} total={r['export_total_entries']} "
              f"ordinal-only={r['export_ordinal_only']} forwarders={r['export_forwarders']}")
        print(f"      DllCharacteristics={r['dll_characteristics_hex']} {r['dll_characteristics']}")
        lc = r["load_config"]
        print(f"      GuardFlags={lc['guard_flags_hex']} {lc['guard_flags']}  cfg_funcs={lc['guard_cf_function_count']}")
        print(f"      CET debug entry={r['cet']}  debug types={r['debug_types']}")
        print(f"      linker={r['linker_version']} imports={r['imports']}")
        print(f"      user32/gdi32 imports: {r['imports_user32_gdi32']}  delay={r['delay_imports']}")
    print()
    for t, d in diffs.items():
        print(t, {k: (len(v) if len(v) > 8 else v) for k, v in d.items()})
    print()
    print(f"{'hardening flag':44}" + "".join(f"{k:>10}" for k in res))
    for flag in next(iter(hardening.values())):
        print(f"{flag:44}" + "".join(f"{('yes' if hardening[k][flag] else 'NO'):>10}" for k in res))


if __name__ == "__main__":
    main()
