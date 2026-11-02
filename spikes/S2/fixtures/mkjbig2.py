# Builds out/fixtures/jbig2-scan.pdf: N pages of 300-dpi 1-bit scans (rendered from text-book.pdf by bench.exe dump), JBIG2 generic region, MMR (T.6) coded.
# The G4 bitstream comes from libtiff via Pillow (TIFF Group4 strip), wrapped into JBIG2 embedded segments (page info + immediate generic region).
# usage: python -I mkjbig2.py <bench.exe> <text-book.pdf> <workdir> <out.pdf> <pages>
import sys, os, struct, subprocess, io, zlib
from PIL import Image
bench, src, work, outpdf, npages = os.path.abspath(sys.argv[1]), os.path.abspath(sys.argv[2]), os.path.abspath(sys.argv[3]), os.path.abspath(sys.argv[4]), int(sys.argv[5])
os.makedirs(work, exist_ok=True)
T = 3400; zoom = 208   # 2.78 px/pt = 200 dpi. NOTE: 300 dpi (2552 px wide) desyncs in the prebuilt PDFium (see S2.md surprises)
streams = []
for p in range(npages):
    raw = os.path.join(work, f"p{p}.bgra")
    subprocess.run([bench, "dump", src, str(p + 1), str(zoom), str(T), "0", "0", raw], check=True, stdout=subprocess.DEVNULL)
    im = Image.frombuffer("RGBA", (T, T), open(raw, "rb").read(), "raw", "BGRA", 0, 1).convert("L")
    w, h = round(612 * 2.78), round(792 * 2.78)
    im = im.crop((0, 0, w, h))
    bw = im.point(lambda v: 0 if v > 150 else 255, mode="1")      # libtiff G4 treats bit 0 as white run: ink (dark) pixels must be 1
    buf = io.BytesIO(); bw.save(buf, "TIFF", compression="group4"); tiff = Image.open(io.BytesIO(buf.getvalue()))
    off = tiff.tag_v2[273]; cnt = tiff.tag_v2[279]; off = off if isinstance(off, int) else off[0]; cnt = cnt if isinstance(cnt, int) else sum(cnt)
    g4 = buf.getvalue()[off:off + cnt]
    # JBIG2 embedded: segment 0 page info, segment 1 immediate generic region (MMR)
    pageinfo_data = struct.pack(">IIIIBH", w, h, 0, 0, 0, 0)
    seg0 = struct.pack(">IBBBI", 0, 48, 0, 1, len(pageinfo_data)) + pageinfo_data
    region = struct.pack(">IIIIB", w, h, 0, 0, 0) + bytes([0x01]) + g4
    seg1 = struct.pack(">IBBBI", 1, 38, 0, 1, len(region)) + region
    streams.append((w, h, seg0 + seg1))
    print("page", p, w, h, "g4 bytes", len(g4))
objs = []
def add(b): objs.append(b); return len(objs)
cat = add(None); pages = add(None)
kids = []
font = add(b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>")
for (w, h, data) in streams:
    img = add(f"<< /Type /XObject /Subtype /Image /Width {w} /Height {h} /ColorSpace /DeviceGray /BitsPerComponent 1 /Filter /JBIG2Decode /Length {len(data)} >>\nstream\n".encode() + data + b"\nendstream")
    c = b"q 612 0 0 792 0 0 cm /Im Do Q"
    cs = add(f"<< /Length {len(c)} >>\nstream\n".encode() + c + b"\nendstream")
    kids.append(add(f"<< /Type /Page /Parent {pages} 0 R /MediaBox [0 0 612 792] /Resources << /XObject << /Im {img} 0 R >> >> /Contents {cs} 0 R >>".encode()))
objs[cat-1] = f"<< /Type /Catalog /Pages {pages} 0 R >>".encode()
objs[pages-1] = f"<< /Type /Pages /Kids [{' '.join(f'{k} 0 R' for k in kids)}] /Count {len(kids)} >>".encode()
out = io.BytesIO(); out.write(b"%PDF-1.7\n"); offs = []
for i, o in enumerate(objs):
    offs.append(out.tell()); out.write(f"{i+1} 0 obj\n".encode() + o + b"\nendobj\n")
x = out.tell(); out.write(f"xref\n0 {len(objs)+1}\n0000000000 65535 f \n".encode())
for o in offs: out.write(f"{o:010d} 00000 n \n".encode())
out.write(f"trailer\n<< /Size {len(objs)+1} /Root {cat} 0 R >>\nstartxref\n{x}\n%%EOF\n".encode())
open(outpdf, "wb").write(out.getvalue()); print("wrote", outpdf, len(out.getvalue()))
