"""S1.4: API checklist checker.

Parses the public PDFium headers of the pinned prebuilt, then for every function in the
plan's checklist (docs/spikes/M0-spike-plan.md, S1) reports: present/missing, header,
Experimental flag, and whether the x64, arm64 and v8-x64 DLLs actually export it.

Outputs (spikes/S1/out/):
  api.csv        per-header totals (functions, experimental)
  checklist.csv  one row per checklist function
  functions.csv  every function declared in the headers (usable as an API-diff baseline)
Run:  .venv\\Scripts\\python apicheck.py
"""
import csv
import difflib
import re
import sys
from pathlib import Path

from peutil import OUT, dll_path, exported_names, include_dir

# --- checklist, expanded from the plan's table (Area, function) -------------------------
CHECKLIST = {
    "Load": ["FPDF_InitLibraryWithConfig", "FPDF_LoadCustomDocument", "FPDF_GetLastError",
             "FPDF_GetPageCount", "FPDF_GetPageSizeByIndexF"],
    "Render": ["FPDF_RenderPageBitmapWithMatrix", "FPDF_RenderPageBitmap_Start",
               "FPDF_RenderPage_Continue", "FPDF_RenderPage_Close", "FPDFBitmap_CreateEx",
               "FPDF_FFLDraw"],
    "Text": ["FPDFText_LoadPage", "FPDFText_CountChars", "FPDFText_GetUnicode",
             "FPDFText_GetCharBox", "FPDFText_GetLooseCharBox", "FPDFText_GetCharIndexAtPos",
             "FPDFText_FindStart", "FPDFText_FindNext", "FPDFText_GetRect"],
    "Doc": ["FPDFBookmark_GetFirstChild", "FPDFBookmark_GetNextSibling", "FPDFBookmark_GetDest",
            "FPDFLink_GetLinkAtPoint", "FPDFLink_Enumerate", "FPDF_GetNamedDestByName",
            "FPDFDest_GetLocationInPage", "FPDF_GetMetaText"],
    "Annot": ["FPDFPage_CreateAnnot", "FPDFAnnot_SetAP", "FPDFAnnot_AppendAttachmentPoints",
              "FPDFAnnot_AddInkStroke", "FPDFAnnot_SetColor", "FPDFAnnot_SetStringValue",
              "FPDFAnnot_GetSubtype"],
    "Forms": ["FPDFDOC_InitFormFillEnvironment", "FORM_OnAfterLoadPage", "FORM_OnLButtonDown",
              "FORM_OnLButtonUp", "FORM_OnChar", "FORM_OnKeyDown", "FORM_ForceToKillFocus",
              "FPDFAnnot_GetFormFieldType"],
    "Edit/Save": ["FPDF_SaveAsCopy", "FPDF_SaveWithVersion", "FPDFPage_SetRotation",
                  "FPDFPage_Delete", "FPDF_MovePages"],
    "PPO": ["FPDF_ImportPagesByIndex", "FPDF_ImportPages", "FPDF_ImportNPagesToOne"],
    "Flatten": ["FPDFPage_Flatten"],
    "Struct": ["FPDF_StructTree_GetForPage", "FPDF_StructElement_GetType",
               "FPDF_StructElement_GetMarkedContentID"],
    "Signature": ["FPDF_GetSignatureCount", "FPDF_GetSignatureObject",
                  "FPDFSignatureObj_GetContents", "FPDFSignatureObj_GetByteRange",
                  "FPDFSignatureObj_GetSubFilter", "FPDFSignatureObj_GetTime",
                  "FPDFSignatureObj_GetDocMDPPermission"],
    "Misc": ["FPDFPage_GetThumbnailAsBitmap", "FPDFDoc_GetAttachmentCount",
             "FPDFAttachment_GetFile"],
}
# Macros the plan relies on (FPDF_SaveAsCopy flags).
CHECKLIST_MACROS = {"Edit/Save": ["FPDF_INCREMENTAL", "FPDF_NO_INCREMENTAL", "FPDF_REMOVE_SECURITY"]}

KNOWN_GUARDS = ("PDF_ENABLE_XFA", "PDF_ENABLE_V8", "PDF_USE_SKIA", "_WIN32", "WIN32")


class Func:
    def __init__(self, name, header, line, experimental, guards):
        self.name, self.header, self.line = name, header, line
        self.experimental, self.guards = experimental, guards


def parse_header(path: Path):
    """Return (functions, experimental_comment_count, text)."""
    lines = path.read_text(encoding="utf-8", errors="replace").splitlines()
    funcs: list[Func] = []
    comment: list[str] = []          # contiguous // block directly above the current line
    cond_stack: list[str] = []       # preprocessor conditions currently open
    i = 0
    while i < len(lines):
        raw = lines[i]
        s = raw.strip()
        if s.startswith("//"):
            comment.append(s)
            i += 1
            continue
        if s.startswith("#"):
            m = re.match(r"#\s*(ifdef|ifndef|if|elif|else|endif)\b\s*(.*)", s)
            if m:
                kw, rest = m.group(1), re.sub(r"//.*", "", m.group(2)).strip()
                if kw in ("if", "ifdef", "ifndef"):
                    cond_stack.append(("!" if kw == "ifndef" else "") + rest)
                elif kw in ("else", "elif") and cond_stack:
                    prev = cond_stack.pop()
                    cond_stack.append("!(" + prev + ")" if kw == "else" else rest)
                elif kw == "endif" and cond_stack:
                    cond_stack.pop()
            comment = []
            i += 1
            continue
        if "FPDF_EXPORT" in s and not s.startswith("*") and "FPDF_EXPORT" in s.split("//")[0]:
            start = i
            decl = s
            while ";" not in decl and i + 1 < len(lines):
                i += 1
                decl += " " + lines[i].strip()
            decl = re.sub(r"\s+", " ", re.sub(r"//.*", "", decl))
            m = re.search(r"FPDF_CALLCONV\s+(\w+)\s*\(", decl) or re.search(r"(\w+)\s*\(", decl)
            if m:
                exp = any("Experimental API" in c for c in comment)
                guards = [g for g in cond_stack if any(k in g for k in KNOWN_GUARDS)]
                funcs.append(Func(m.group(1), path.name, start + 1, exp, guards))
            comment = []
            i += 1
            continue
        if s != "":
            comment = []     # code line that is not a declaration; a blank line keeps the block
        i += 1
    text = "\n".join(lines)
    exp_comments = len(re.findall(r"//\s*Experimental API", text))
    return funcs, exp_comments, text


def main():
    OUT.mkdir(exist_ok=True)
    inc = include_dir("x64")
    all_funcs: dict[str, Func] = {}
    per_header = []
    macros_text = ""
    for h in sorted(inc.glob("*.h")):
        funcs, exp_comments, text = parse_header(h)
        macros_text += text + "\n"
        for f in funcs:
            if f.name in all_funcs:
                print(f"note: duplicate declaration {f.name} in {f.header} and {all_funcs[f.name].header}")
            all_funcs.setdefault(f.name, f)
        n_exp = sum(f.experimental for f in funcs)
        per_header.append((h.name, len(funcs), n_exp, exp_comments))

    exports = {k: exported_names(dll_path(k)) for k in ("x64", "arm64", "v8-x64")}

    # ---- api.csv: per header totals
    with open(OUT / "api.csv", "w", newline="") as fh:
        w = csv.writer(fh)
        w.writerow(["header", "functions", "experimental", "experimental_comment_markers",
                    "exported_x64", "exported_arm64", "exported_v8_x64"])
        tot = [0, 0, 0, 0, 0, 0]
        for name, n, ne, nc in per_header:
            fs = [f for f in all_funcs.values() if f.header == name]
            ex = [sum(f.name in exports[k] for f in fs) for k in ("x64", "arm64", "v8-x64")]
            w.writerow([name, n, ne, nc, *ex])
            for j, v in enumerate([n, ne, nc, *ex]):
                tot[j] += v
        w.writerow(["TOTAL", *tot])

    # ---- functions.csv: every declared function
    with open(OUT / "functions.csv", "w", newline="") as fh:
        w = csv.writer(fh)
        w.writerow(["function", "header", "line", "experimental", "ifdef_guards",
                    "exported_x64", "exported_arm64", "exported_v8_x64"])
        for f in sorted(all_funcs.values(), key=lambda f: (f.header, f.line)):
            w.writerow([f.name, f.header, f.line, int(f.experimental), ";".join(f.guards),
                        *[int(f.name in exports[k]) for k in ("x64", "arm64", "v8-x64")]])

    # ---- checklist.csv
    rows, missing = [], []
    for area, names in CHECKLIST.items():
        for n in names:
            f = all_funcs.get(n)
            alt = ""
            if not f:
                missing.append(n)
                pool = list(all_funcs)
                close = difflib.get_close_matches(n, pool, n=3, cutoff=0.6)
                alt = " | ".join(close)
            rows.append([area, n, "present" if f else "MISSING", f.header if f else "",
                         ("yes" if f.experimental else "no") if f else "",
                         ";".join(f.guards) if f else "",
                         *[("yes" if n in exports[k] else "NO") for k in ("x64", "arm64", "v8-x64")],
                         alt])
    for area, macros in CHECKLIST_MACROS.items():
        for mname in macros:
            m = re.search(rf"#define\s+{mname}\s+(\S+)", macros_text)
            rows.append([area, mname + " (macro)", "present" if m else "MISSING",
                         "fpdf_save.h" if m else "", "", "", "n/a", "n/a", "n/a",
                         f"= {m.group(1)}" if m else ""])
    with open(OUT / "checklist.csv", "w", newline="") as fh:
        w = csv.writer(fh)
        w.writerow(["area", "function", "header_status", "header", "experimental",
                    "ifdef_guards", "exported_x64", "exported_arm64", "exported_v8_x64",
                    "nearest_alternatives_if_missing"])
        w.writerows(rows)

    # ---- console summary
    print(f"{'header':24} {'funcs':>5} {'exp':>4} {'exp-markers':>11}")
    for name, n, ne, nc in per_header:
        flag = "" if ne == nc else "   <-- marker/decl mismatch"
        print(f"{name:24} {n:5} {ne:4} {nc:11}{flag}")
    print(f"TOTAL functions parsed: {len(all_funcs)}  experimental: "
          f"{sum(f.experimental for f in all_funcs.values())}")
    for k, v in exports.items():
        decl = set(all_funcs)
        print(f"{k:8}: {len(v)} exports; declared-not-exported={len(decl - v)}; "
              f"exported-not-declared={len(v - decl)}")
    print()
    print(f"{'area':10} {'function':42} {'status':8} {'header':20} exp  x64 arm64 v8   guards")
    for r in rows:
        print(f"{r[0]:10} {r[1]:42} {r[2]:8} {r[3]:20} {r[4]:4} {r[6]:3} {r[7]:5} {r[8]:3} {r[5]}")
    print()
    exp_rows = [r for r in rows if r[4] == "yes"]
    print(f"checklist: {len(rows)} entries, missing={len(missing)}, experimental={len(exp_rows)}")
    if missing:
        print("MISSING:", missing)
    bad = [r for r in rows if r[6] == "NO" or r[7] == "NO"]
    print("not exported by x64 or arm64:", [r[1] for r in bad] or "none")
    return 0


if __name__ == "__main__":
    sys.exit(main())
