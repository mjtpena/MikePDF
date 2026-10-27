import sys
from PIL import Image
src, w, h, dst = sys.argv[1], int(sys.argv[2]), int(sys.argv[3]), sys.argv[4]
data = open(src, "rb").read()
Image.frombuffer("RGBA", (w, h), data, "raw", "BGRA", 0, 1).convert("RGB").save(dst)
