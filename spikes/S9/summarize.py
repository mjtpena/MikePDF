"""Turns out/s9-overlay.csv into the markdown pivots used in docs/spikes/S9-annotation-display.md. Throwaway."""
import csv, sys, collections

rows = list(csv.DictReader(open(sys.argv[1] if len(sys.argv) > 1 else "out/s9-overlay.csv")))
devs = ["dgpu", "igpu", "warp"]
names = {"dgpu": "RTX 5080 Laptop (dGPU)", "igpu": "Intel Graphics (iGPU)", "warp": "WARP (software)"}
for kind, strats in (("ink", None), ("highlight", None), ("mixed", None)):
    for dev in devs:
        sub = [r for r in rows if r["device"] == dev and r["kind"] == kind]
        if not sub:
            continue
        strategies = sorted({r["strategy"] for r in sub})
        print(f"\n#### {kind}, {names[dev]}: frame p50 / p95 ms (whole frame: tiles + overlay, GPU flushed)\n")
        zooms = sorted({float(r["zoom"]) for r in sub})
        ns = sorted({int(r["n"]) for r in sub})
        print("| strategy | N | " + " | ".join(f"{z:g}x" for z in zooms) + " |")
        print("|---|---|" + "---|" * len(zooms))
        for s in strategies:
            for n in ns:
                cells = []
                for z in zooms:
                    m = [r for r in sub if r["strategy"] == s and int(r["n"]) == n and float(r["zoom"]) == z]
                    cells.append(f"{float(m[0]['p50_ms']):.2f} / {float(m[0]['p95_ms']):.2f}" if m else "-")
                print(f"| {s} | {n} | " + " | ".join(cells) + " |")
print("\n#### floors (tiles only, p50 ms) and empty frame + flush\n")
for dev in devs:
    for z in (1.0, 4.0, 16.0):
        f = [r for r in rows if r["device"] == dev and r["strategy"] == "tiles only" and float(r["zoom"]) == z]
        e = [r for r in rows if r["device"] == dev and r["strategy"] == "empty frame + flush" and float(r["zoom"]) == z]
        print(f"- {dev} {z:g}x: tiles only {float(f[0]['p50_ms']):.2f} ms; empty frame + flush {float(e[0]['p50_ms']):.3f} ms")
