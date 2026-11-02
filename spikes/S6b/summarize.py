"""Summarises test-host CSV output (the lines between '# label' headers): n, ok count, p50/p95/p99/max for the timing columns.
usage: summarize.py file.csv [file.csv ...]   (writes a markdown table to stdout)"""
import csv
import io
import statistics
import sys

COLS = ["total_ms", "copy_ms", "connect_ms", "open_rt_ms", "render_rt_ms", "close_ms", "convert_ms", "srv_spawn_us", "srv_open_us", "srv_render_us", "srv_out_us", "srv_stage_us", "first_pixel_ms", "do_preview_call_ms"]


def pct(v, p):
    v = sorted(v)
    if not v:
        return float("nan")
    k = (len(v) - 1) * p / 100.0
    lo, hi = int(k), min(int(k) + 1, len(v) - 1)
    return v[lo] + (v[hi] - v[lo]) * (k - lo)


for path in sys.argv[1:]:
    text = open(path, encoding="utf-8", errors="replace").read()
    header = [l for l in text.splitlines() if l.startswith("# label")]
    lines = [l for l in text.splitlines() if l and not l.startswith("#")]
    rows = list(csv.DictReader(io.StringIO("\n".join(lines))))
    ok = [r for r in rows if r.get("ok") == "1"]
    print(f"\n### {path}\n{header[0] if header else ''}")
    print(f"n={len(rows)} ok={len(ok)}")
    print("| column | p50 | p95 | p99 | max | min |\n|---|---|---|---|---|---|")
    for c in COLS:
        if not rows or c not in rows[0]:
            continue
        try:
            v = [float(r[c]) for r in ok if r[c] not in ("", "-1.00")]
        except ValueError:
            continue
        if not v:
            continue
        print(f"| {c} | {pct(v, 50):.2f} | {pct(v, 95):.2f} | {pct(v, 99):.2f} | {max(v):.2f} | {min(v):.2f} |")
