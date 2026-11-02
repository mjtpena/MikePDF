"""Generates the S6b test PDFs (throwaway): text pages, a rotated page, and files padded with an unreferenced random stream to a chosen size.
usage: python gen_pdfs.py <outdir>
"""
import os
import random
import sys


def build(pages, pad_bytes=0, rotate=0, seed=1):
    objs = []  # list of bytes bodies, object number = index + 1

    def add(body):
        objs.append(body)
        return len(objs)

    cat = add(b"")  # placeholder (1)
    pages_obj = add(b"")  # placeholder (2)
    font = add(b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>")
    kids = []
    for p in range(pages):
        lines = []
        y = 740
        lines.append(b"BT /F1 24 Tf 72 %d Td (Page %d of %d - S6b thumbnail test) Tj ET" % (y, p + 1, pages))
        for i in range(30):
            y -= 20
            lines.append(b"BT /F1 11 Tf 72 %d Td (Line %d: The quick brown fox jumps over the lazy dog 0123456789.) Tj ET" % (y, i + 1))
        lines.append(b"0.8 0.1 0.1 rg 72 60 200 40 re f 0.1 0.1 0.8 rg 320 60 200 40 re f")
        content = b"\n".join(lines)
        c = add(b"<< /Length %d >>\nstream\n" % len(content) + content + b"\nendstream")
        pg = add(b"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Rotate %d /Resources << /Font << /F1 %d 0 R >> >> /Contents %d 0 R >>" % (rotate, font, c))
        kids.append(pg)
    if pad_bytes:
        rnd = random.Random(seed)
        blob = rnd.randbytes(pad_bytes)
        add(b"<< /Length %d >>\nstream\n" % len(blob) + blob + b"\nendstream")
    objs[cat - 1] = b"<< /Type /Catalog /Pages 2 0 R >>"
    objs[pages_obj - 1] = b"<< /Type /Pages /Count %d /Kids [%s] >>" % (len(kids), b" ".join(b"%d 0 R" % k for k in kids))
    out = bytearray(b"%PDF-1.7\n%\xe2\xe3\xcf\xd3\n")
    offsets = []
    for i, body in enumerate(objs):
        offsets.append(len(out))
        out += b"%d 0 obj\n" % (i + 1) + body + b"\nendobj\n"
    xref = len(out)
    out += b"xref\n0 %d\n0000000000 65535 f \n" % (len(objs) + 1)
    for off in offsets:
        out += b"%010d 00000 n \n" % off
    out += b"trailer\n<< /Size %d /Root 1 0 R >>\nstartxref\n%d\n%%%%EOF\n" % (len(objs) + 1, xref)
    return bytes(out)


def build_heavy(side):
    """One page with a side x side RGB image, Flate-compressed (a few hundred KB): PDFium has to decode side*side*3 bytes to draw it."""
    import zlib
    row = bytes((x * 7 + 3) & 255 for x in range(side * 3))
    comp = zlib.compress(row * side, 9)
    objs = [
        b"<< /Type /Catalog /Pages 2 0 R >>",
        b"<< /Type /Pages /Count 1 /Kids [3 0 R] >>",
        b"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /XObject << /Im0 5 0 R >> >> /Contents 4 0 R >>",
        b"<< /Length 31 >>\nstream\nq 612 0 0 792 0 0 cm /Im0 Do Q\nendstream",
        b"<< /Type /XObject /Subtype /Image /Width %d /Height %d /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /FlateDecode /Length %d >>\nstream\n" % (side, side, len(comp)) + comp + b"\nendstream",
    ]
    out = bytearray(b"%PDF-1.7\n")
    offs = []
    for i, body in enumerate(objs):
        offs.append(len(out))
        out += b"%d 0 obj\n" % (i + 1) + body + b"\nendobj\n"
    x = len(out)
    out += b"xref\n0 %d\n0000000000 65535 f \n" % (len(objs) + 1)
    for o in offs:
        out += b"%010d 00000 n \n" % o
    out += b"trailer\n<< /Size %d /Root 1 0 R >>\nstartxref\n%d\n%%%%EOF\n" % (len(objs) + 1, x)
    return bytes(out)



def main():
    d = sys.argv[1]
    os.makedirs(d, exist_ok=True)
    files = {
        "text1.pdf": build(1),
        "text20.pdf": build(20),
        "rot90.pdf": build(1, rotate=90),
        "pad5mb.pdf": build(5, pad_bytes=5 * 1024 * 1024),
        "pad30mb.pdf": build(5, pad_bytes=30 * 1024 * 1024),
        "pad40mb.pdf": build(5, pad_bytes=40 * 1024 * 1024),
        "heavy8k.pdf": build_heavy(8000),
        "heavy14k.pdf": build_heavy(14000),
    }
    for name, data in files.items():
        with open(os.path.join(d, name), "wb") as f:
            f.write(data)
        print(name, len(data))


main()
