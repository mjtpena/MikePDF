# usage: python -I mkjpx.py <in.jpg> <outdir> <size>   -> writes photo.jp2 and photo.dim (WxH)
import sys, os
from PIL import Image, features
Image.MAX_IMAGE_PIXELS = 400_000_000
src, outdir, size = sys.argv[1], sys.argv[2], int(sys.argv[3])
assert features.check("jpg_2000"), "Pillow lacks OpenJPEG"
im = Image.open(src).convert("RGB")
print("source", im.size)
im = im.resize((size, size), Image.LANCZOS)
os.makedirs(outdir, exist_ok=True)
im.save(os.path.join(outdir, "photo.jp2"), "JPEG2000", quality_mode="rates", quality_layers=[20], irreversible=True)
open(os.path.join(outdir, "photo.dim"), "w").write(f"{size}x{size}")
print("wrote", os.path.getsize(os.path.join(outdir, "photo.jp2")))
