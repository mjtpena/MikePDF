"""Converts the test host's --save output ("BGRA w h alpha\\n" + raw pixels) to a PNG (stdlib only). usage: bin2png.py in.bin out.png"""
import struct
import sys
import zlib

data = open(sys.argv[1], "rb").read()
nl = data.index(b"\n")
_, w, h, alpha = data[:nl].split()
w, h = int(w), int(h)
px = data[nl + 1:]
assert len(px) == w * h * 4, (len(px), w, h)
rows = bytearray()
for y in range(h):
    row = px[y * w * 4:(y + 1) * w * 4]
    rows.append(0)
    for x in range(w):
        b, g, r, a = row[x * 4:x * 4 + 4]
        rows += bytes((r, g, b, a))


def chunk(t, d):
    c = struct.pack(">I", len(d)) + t + d
    return c + struct.pack(">I", zlib.crc32(t + d) & 0xFFFFFFFF)


png = b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 6, 0, 0, 0)) + chunk(b"IDAT", zlib.compress(bytes(rows), 6)) + chunk(b"IEND", b"")
open(sys.argv[2], "wb").write(png)
# simple statistics for the report: share of non-white pixels
non_white = sum(1 for i in range(0, len(px), 4) if px[i:i + 3] != b"\xff\xff\xff")
print(f"{w}x{h} alpha={alpha.decode()} non-white={non_white / (w * h):.3%}")
