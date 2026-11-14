"""Generate the WowWallPaper application icon (multi-resolution .ico)."""
from __future__ import annotations

from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / "src" / "WowWallPaper" / "Assets" / "app.ico"
SIZE = 1024


def lerp(a: int, b: int, t: float) -> int:
    return round(a + (b - a) * t)


def gradient(size: int) -> Image.Image:
    img = Image.new("RGBA", (size, size))
    px = img.load()
    left = (34, 211, 238)
    right = (139, 92, 246)
    for y in range(size):
        for x in range(size):
            t = (x / size * 0.62) + (y / size * 0.38)
            px[x, y] = (
                lerp(left[0], right[0], t),
                lerp(left[1], right[1], t),
                lerp(left[2], right[2], t),
                255,
            )
    return img


def main() -> None:
    base = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
    mask = Image.new("L", (SIZE, SIZE), 0)
    ImageDraw.Draw(mask).rounded_rectangle((54, 54, SIZE - 54, SIZE - 54), radius=230, fill=255)
    base.alpha_composite(Image.composite(gradient(SIZE), Image.new("RGBA", (SIZE, SIZE)), mask))

    draw = ImageDraw.Draw(base)

    # Soft top highlight and a crisp inner border.
    draw.rounded_rectangle(
        (54, 54, SIZE - 54, SIZE - 54),
        radius=230,
        outline=(255, 255, 255, 80),
        width=8,
    )
    highlight = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
    ImageDraw.Draw(highlight).ellipse((-260, -620, SIZE + 260, 360), fill=(255, 255, 255, 26))
    base.alpha_composite(Image.composite(highlight, Image.new("RGBA", (SIZE, SIZE)), mask))

    font_path = Path(r"C:\Windows\Fonts\segoeuib.ttf")
    if not font_path.exists():
        font_path = Path(r"C:\Windows\Fonts\arialbd.ttf")
    font = ImageFont.truetype(str(font_path), 500)
    text = "W"
    box = draw.textbbox((0, 0), text, font=font)
    tw, th = box[2] - box[0], box[3] - box[1]
    tx = (SIZE - tw) / 2 - box[0]
    ty = (SIZE - th) / 2 - box[1] - 44
    draw.text((tx, ty), text, font=font, fill=(255, 255, 255, 245))

    # Play badge: video wallpaper cue.
    cx, cy, r = 762, 748, 142
    draw.ellipse((cx - r, cy - r, cx + r, cy + r), fill=(8, 15, 30, 245), outline=(255, 255, 255, 105), width=9)
    draw.polygon(
        ((cx - 38, cy - 66), (cx - 38, cy + 66), (cx + 72, cy)),
        fill=(103, 232, 249, 255),
    )

    OUT.parent.mkdir(parents=True, exist_ok=True)
    base.save(OUT, format="ICO", sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
    print(f"wrote {OUT}")


if __name__ == "__main__":
    main()
