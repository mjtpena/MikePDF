"""Generates MikePDF's app icon and Store artwork. Requires Pillow (build-time only, not shipped).

Usage: python build/store/generate-assets.py
Writes MSIX assets and Store listing art to build/store/assets/. The mark is a stack of
sheets with a folded corner and has no lettering, so it survives a rename.
"""
from pathlib import Path

from PIL import Image, ImageDraw

OUT = Path(__file__).parent / "assets"
SS = 8  # supersampling factor
TEAL_TOP, TEAL_BOTTOM = (15, 118, 110), (6, 78, 95)
PAGE, PAGE_BACK, PAGE_BACK2, FOLD = (255, 255, 255), (204, 236, 232), (150, 212, 206), (178, 222, 217)
LINE = (15, 118, 110)


def rounded_gradient(size, radius):
    grad = Image.new("RGBA", (1, size))
    for y in range(size):
        t = y / max(size - 1, 1)
        grad.putpixel((0, y), tuple(int(a + (b - a) * t) for a, b in zip(TEAL_TOP, TEAL_BOTTOM)) + (255,))
    grad = grad.resize((size, size))
    mask = Image.new("L", (size, size), 0)
    ImageDraw.Draw(mask).rounded_rectangle((0, 0, size - 1, size - 1), radius, fill=255)
    out = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    out.paste(grad, (0, 0), mask)
    return out


def sheet(d, x, y, w, h, r, color):
    d.rounded_rectangle((x, y, x + w, y + h), r, fill=color)


def draw_mark(img, cx, cy, s, detail):
    """Draws the sheets centred at (cx, cy), with overall height s, on img."""
    d = ImageDraw.Draw(img)
    w, h = s * 0.68, s * 0.86
    x, y = cx - w * 0.5, cy - h * 0.5
    r = s * 0.035
    step = s * 0.055
    sheet(d, x + step * 1.5, y - step * 1.0, w, h, r, PAGE_BACK2)
    sheet(d, x + step * 0.75, y - step * 0.5, w, h, r, PAGE_BACK)
    sheet(d, x, y, w, h, r, PAGE)
    fold = w * 0.3
    # Cut the corner, then draw the fold triangle over it.
    x1, y0 = x + w, y
    d.polygon([(x1 - fold, y0 - 1), (x1 + 1, y0 - 1), (x1 + 1, y0 + fold)], fill=PAGE_BACK)
    d.polygon([(x1 - fold, y0), (x1 - fold, y0 + fold), (x1, y0 + fold)], fill=FOLD)
    if detail:
        lh = s * 0.052
        lx0, lx1 = x + w * 0.16, x + w * 0.84
        for ty, frac in [(0.42, 1.0), (0.56, 1.0), (0.70, 1.0), (0.84, 0.62)]:
            ly = y + h * ty
            d.rounded_rectangle((lx0, ly, lx0 + (lx1 - lx0) * frac, ly + lh), lh / 2, fill=LINE)


def tile(w, h, mark_ratio):
    """Teal tile, w x h, with the mark centred."""
    W, H = w * SS, h * SS
    short = min(W, H)
    img = Image.new("RGBA", (W, H))
    d = ImageDraw.Draw(img)
    for yy in range(H):
        t = yy / max(H - 1, 1)
        d.line([(0, yy), (W, yy)], fill=tuple(int(a + (b - a) * t) for a, b in zip(TEAL_TOP, TEAL_BOTTOM)) + (255,))
    draw_mark(img, W / 2, H / 2 + short * 0.012, short * mark_ratio, True)
    return img.resize((w, h), Image.LANCZOS)


def glyph(size):
    """App icon: rounded teal plate with the mark. Lines are dropped below 32 px, where they would blur."""
    S = size * SS
    img = rounded_gradient(S, int(S * 0.2))
    draw_mark(img, S / 2, S / 2 + S * 0.012, S * 0.74, size >= 32)
    return img.resize((size, size), Image.LANCZOS)


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    for scale in (100, 125, 150, 200, 400):
        f = scale / 100
        tile(int(150 * f), int(150 * f), 0.5).save(OUT / f"Square150x150Logo.scale-{scale}.png")
        tile(int(310 * f), int(150 * f), 0.56).save(OUT / f"Wide310x150Logo.scale-{scale}.png")
        glyph(int(44 * f)).save(OUT / f"Square44x44Logo.scale-{scale}.png")
        glyph(int(50 * f)).save(OUT / f"StoreLogo.scale-{scale}.png")
    for size in (16, 20, 24, 30, 32, 36, 40, 48, 60, 64, 72, 80, 96, 256):
        icon = glyph(size)
        icon.save(OUT / f"Square44x44Logo.targetsize-{size}.png")
        icon.save(OUT / f"Square44x44Logo.targetsize-{size}_altform-unplated.png")
        icon.save(OUT / f"Square44x44Logo.targetsize-{size}_altform-lightunplated.png")
    store = OUT / "store-listing"
    store.mkdir(exist_ok=True)
    for size in (300, 1080):
        tile(size, size, 0.62).save(store / f"tile-{size}x{size}.png")
    glyph(256).save(OUT / "app.ico", sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (256, 256)])


if __name__ == "__main__":
    main()
