"""Checks a thumbnail of PDFium's rectangles.pdf (200 x 300 pt, black/blue/green/red corner squares) from the test host's --save output.
usage: verify_rect.py thumb.bin   -> prints PASS/FAIL per check, exit code 1 on any failure."""
import sys

data = open(sys.argv[1], "rb").read()
nl = data.index(b"\n")
_, w, h, alpha = data[:nl].split()
w, h = int(w), int(h)
px = data[nl + 1:]


def at(x, y):
    i = (y * w + x) * 4
    return tuple(px[i:i + 4])  # B, G, R, A


s = w / 200.0  # device pixels per point (the page is fitted, aspect preserved)
checks = {
    "top-left black": (at(int(5 * s), int(5 * s)), (0, 0, 0, 255)),
    "top-right blue": (at(w - 1 - int(5 * s), int(5 * s)), (255, 0, 0, 255)),
    "bottom-right green": (at(w - 1 - int(5 * s), h - 1 - int(5 * s)), (0, 255, 0, 255)),
    "bottom-left red": (at(int(5 * s), h - 1 - int(5 * s)), (0, 0, 255, 255)),
    "paper": (at(w // 2, int(5 * s)), (255, 255, 255, 255)),
}
bad = 0
print(f"size {w}x{h} (aspect {w / h:.4f}, page aspect {200 / 300:.4f}), wts_alphatype={alpha.decode()}")
for name, (got, want) in checks.items():
    ok = got == want
    bad += not ok
    print(("PASS " if ok else "FAIL ") + name, got, "expected", want)
sys.exit(1 if bad else 0)
