"""Create non-committed demo wallpapers used for local UI verification."""
from __future__ import annotations

import json
import math
import random
import subprocess
from datetime import datetime, timedelta, timezone
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw, ImageFilter

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / "artifacts" / "demo"
DATA = ROOT / "artifacts" / "test-data"
W, H = 1600, 900


def gradient(top: tuple[int, int, int], bottom: tuple[int, int, int]) -> Image.Image:
    t = np.linspace(0.0, 1.0, H)[:, None]
    pixels = np.zeros((H, W, 3), dtype=np.float64)
    for channel in range(3):
        pixels[:, :, channel] = top[channel] * (1 - t) + bottom[channel] * t
    return Image.fromarray(pixels.clip(0, 255).astype(np.uint8), "RGB")


def add_stars(image: Image.Image, count: int = 260, seed: int = 4, max_y: int = 640) -> None:
    rng = random.Random(seed)
    draw = ImageDraw.Draw(image, "RGBA")
    for _ in range(count):
        x = rng.randrange(W)
        y = rng.randrange(max_y)
        radius = 1 if rng.random() < 0.85 else 2
        alpha = rng.randint(45, 210)
        draw.ellipse((x - radius, y - radius, x + radius, y + radius), fill=(230, 244, 255, alpha))


def add_glow(image: Image.Image, layer: Image.Image, blur: int, alpha: float = 1.0) -> None:
    glow = layer.filter(ImageFilter.GaussianBlur(blur))
    if alpha < 1.0:
        alpha_channel = glow.getchannel("A").point(lambda a: int(a * alpha))
        glow.putalpha(alpha_channel)
    image.alpha_composite(glow)


def aurora() -> None:
    base = gradient((3, 6, 15), (13, 24, 43)).convert("RGBA")
    add_stars(base, 300, 12, 560)
    ribbons = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    draw = ImageDraw.Draw(ribbons)
    colors = [(34, 211, 238), (74, 222, 128), (139, 92, 246), (45, 212, 191)]
    rng = random.Random(9)
    for band in range(5):
        points = []
        for x in range(-50, W + 60, 20):
            y = 260 + band * 58 + math.sin(x / 210 + band * 1.7) * 95 + math.sin(x / 70 + band) * 18
            points.append((x, y))
        for thickness, alpha in [(105, 18), (58, 25), (26, 62), (7, 145)]:
            draw.line(points, fill=(*colors[band % len(colors)], alpha), width=thickness, joint="curve")
    add_glow(base, ribbons, 28, 0.85)
    base.alpha_composite(ribbons)

    mountains = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    draw = ImageDraw.Draw(mountains)
    for layer, (peak, color) in enumerate([(560, (8, 17, 31, 255)), (650, (5, 11, 21, 255))]):
        points = [(0, H)]
        previous = peak
        for x in range(0, W + 60, 60):
            previous += rng.randint(-26, 26)
            previous = max(430, min(690, previous))
            points.append((x, previous + layer * 55))
        points.extend([(W, H), (0, H)])
        draw.polygon(points, fill=color)
    base.alpha_composite(mountains)

    overlay = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    ImageDraw.Draw(overlay).ellipse((-300, 380, 700, 1100), fill=(34, 211, 238, 16))
    add_glow(base, overlay, 120)
    base.convert("RGB").save(OUT / "aurora-drift.jpg", quality=93)


def dusk() -> None:
    base = gradient((22, 14, 48), (246, 128, 90)).convert("RGBA")
    sun = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    draw = ImageDraw.Draw(sun)
    draw.ellipse((980, 350, 1260, 630), fill=(255, 214, 153, 95))
    add_glow(base, sun, 70)
    draw.ellipse((1060, 430, 1180, 550), fill=(255, 232, 183, 235))
    base.alpha_composite(sun.filter(ImageFilter.GaussianBlur(1)))

    layers = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    draw = ImageDraw.Draw(layers)
    rng = random.Random(30)
    colors = [(54, 30, 71, 255), (34, 20, 49, 255), (18, 11, 27, 255)]
    for index, color in enumerate(colors):
        points = [(0, H)]
        y = 610 + index * 70
        for x in range(0, W + 80, 80):
            y += rng.randint(-20, 20)
            y = max(500 + index * 70, min(780, y))
            points.append((x, y))
        points.extend([(W, H), (0, H)])
        draw.polygon(points, fill=color)
    base.alpha_composite(layers)

    water = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    draw = ImageDraw.Draw(water)
    for i in range(34):
        y = 690 + i * 5
        alpha = max(8, 52 - i)
        draw.line((760 - i * 10, y, 1400 + i * 6, y), fill=(255, 195, 140, alpha), width=2)
    add_glow(base, water, 3)
    base.convert("RGB").save(OUT / "dusk-ridge.jpg", quality=93)


def neon() -> None:
    base = gradient((4, 8, 20), (13, 22, 44)).convert("RGBA")
    add_stars(base, 100, 8, 460)
    city = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    draw = ImageDraw.Draw(city)
    rng = random.Random(77)
    x = -20
    while x < W:
        width = rng.randint(45, 115)
        height = rng.randint(150, 470)
        top = H - 150 - height
        draw.rectangle((x, top, x + width, H - 140), fill=(6, 12, 24, 245))
        for wy in range(top + 18, H - 165, 26):
            for wx in range(x + 10, x + width - 10, 18):
                if rng.random() < 0.28:
                    color = rng.choice([(34, 211, 238, 180), (167, 139, 250, 170), (251, 191, 36, 130)])
                    draw.rectangle((wx, wy, wx + 5, wy + 8), fill=color)
        x += width + rng.randint(5, 22)

    draw.line((0, 748, W, 748), fill=(34, 211, 238, 130), width=2)
    reflections = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    draw = ImageDraw.Draw(reflections)
    for i in range(44):
        y = 754 + i * 3
        alpha = max(6, 60 - i)
        width = int(3 + i * 1.4)
        draw.line((110, y, 540, y), fill=(34, 211, 238, alpha), width=width)
        draw.line((930, y, 1420, y), fill=(167, 139, 250, alpha), width=width)
    add_glow(base, city, 0)
    base.alpha_composite(city)
    add_glow(base, reflections, 5)
    base.convert("RGB").save(OUT / "neon-harbor.jpg", quality=93)


def dunes() -> None:
    t = np.linspace(0.0, 1.0, H)[:, None]
    base_pixels = np.zeros((H, W, 3), dtype=np.float64)
    for channel, (a, b) in enumerate([(250, 185), (211, 124), (142, 76)]):
        base_pixels[:, :, channel] = a * (1 - t) + b * t
    base = Image.fromarray(base_pixels.clip(0, 255).astype(np.uint8), "RGB").convert("RGBA")
    layers = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    draw = ImageDraw.Draw(layers)
    palette = [(198, 130, 86, 175), (140, 86, 67, 185), (77, 53, 58, 230), (34, 31, 39, 255)]
    for index, color in enumerate(palette):
        points = [(0, H)]
        for x in range(0, W + 40, 40):
            y = 420 + index * 108 + math.sin(x / 260 + index * 1.2) * 58 + math.sin(x / 83 + index) * 12
            points.append((x, y))
        points.extend([(W, H), (0, H)])
        draw.polygon(points, fill=color)
    base.alpha_composite(layers)
    glow = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    ImageDraw.Draw(glow).ellipse((1020, 70, 1380, 430), fill=(255, 246, 213, 160))
    add_glow(base, glow, 55)
    base.convert("RGB").save(OUT / "silk-dunes.jpg", quality=92)


def forest() -> None:
    base = gradient((7, 22, 26), (25, 51, 52)).convert("RGBA")
    fog = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    draw = ImageDraw.Draw(fog)
    for i in range(5):
        draw.ellipse((-300 + i * 240, 280 + i * 55, 1000 + i * 200, 850 + i * 20), fill=(183, 228, 214, 18))
    add_glow(base, fog, 90)
    trees = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    draw = ImageDraw.Draw(trees)
    rng = random.Random(3)
    for i in range(80):
        x = rng.randrange(-50, W + 50)
        width = rng.randint(4, 20)
        top = rng.randint(60, 520)
        alpha = rng.randint(70, 200)
        draw.polygon([(x, top), (x - width, H), (x + width, H)], fill=(4, 16, 18, alpha))
    add_glow(base, trees.filter(ImageFilter.GaussianBlur(2)), 2)
    base.alpha_composite(trees)
    light = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    draw = ImageDraw.Draw(light)
    draw.polygon([(320, 0), (520, 0), (980, H), (520, H)], fill=(173, 247, 221, 20))
    add_glow(base, light, 30)
    base.convert("RGB").save(OUT / "forest-mist.jpg", quality=92)


def chromatic() -> None:
    y, x = np.mgrid[0:H, 0:W]
    pixels = np.zeros((H, W, 3), dtype=np.float64)
    waves = (
        np.sin(x / 145 + y / 210)
        + np.sin(x / 61 - y / 187)
        + np.sin((x + y) / 290)
    )
    blend = (waves + 3) / 6
    pixels[:, :, 0] = 25 + blend * 90 + np.sin(y / 70) * 10
    pixels[:, :, 1] = 35 + (1 - blend) * 85
    pixels[:, :, 2] = 70 + blend * 150
    base = Image.fromarray(pixels.clip(0, 255).astype(np.uint8), "RGB").convert("RGBA")
    glow = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    draw = ImageDraw.Draw(glow)
    draw.ellipse((-160, 330, 680, 1120), fill=(34, 211, 238, 62))
    draw.ellipse((960, -160, 1840, 650), fill=(217, 70, 239, 54))
    add_glow(base, glow, 110)
    overlay = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    draw = ImageDraw.Draw(overlay)
    for i in range(9):
        points = []
        for xx in range(-50, W + 50, 25):
            yy = 300 + i * 55 + math.sin(xx / 240 + i * 0.6) * 120 + math.sin(xx / 80) * 14
            points.append((xx, yy))
        draw.line(points, fill=(255, 255, 255, 12 + i * 2), width=2)
    base.alpha_composite(overlay)
    base.convert("RGB").save(OUT / "chromatic-flow.jpg", quality=93)


def build_video(name: str, colors: str) -> None:
    command = [
        "ffmpeg", "-hide_banner", "-loglevel", "error", "-y",
        "-f", "lavfi",
        "-i", f"gradients=s=1280x720:r=30:d=10:{colors}",
        "-c:v", "libx264", "-preset", "medium", "-crf", "21",
        "-pix_fmt", "yuv420p", "-movflags", "+faststart",
        str(OUT / name),
    ]
    subprocess.run(command, check=True)


def write_settings(image_names: list[str], video_names: list[str]) -> None:
    now = datetime.now(timezone.utc)
    items = []
    for index, name in enumerate(image_names):
        items.append({
            "path": str(OUT / name),
            "kind": "image",
            "addedAt": (now - timedelta(days=index, hours=2)).isoformat().replace("+00:00", "Z"),
        })
    for index, name in enumerate(video_names):
        items.append({
            "path": str(OUT / name),
            "kind": "video",
            "addedAt": (now - timedelta(days=index, hours=1)).isoformat().replace("+00:00", "Z"),
        })

    settings = {
        "schemaVersion": 1,
        "library": items,
        "currentWallpaperPath": str(OUT / image_names[0]),
        "imageFit": "fill",
        "videoFit": "fill",
        "muteVideo": True,
        "pauseOnFullscreen": True,
        "autoRotate": False,
        "rotationIntervalMinutes": 30,
        "shuffle": False,
        "closeToTray": True,
    }
    DATA.mkdir(parents=True, exist_ok=True)
    (DATA / "settings.json").write_text(json.dumps(settings, ensure_ascii=False, indent=2), encoding="utf-8")


def main() -> None:
    OUT.mkdir(parents=True, exist_ok=True)
    aurora()
    dusk()
    neon()
    dunes()
    forest()
    chromatic()
    build_video(
        "flux-loop.mp4",
        "c0=0x030712:c1=0x0e7490:c2=0x6d28d9:c3=0x020617:c4=0x22d3ee:c5=0x111827",
    )
    build_video(
        "neon-pulse.mp4",
        "c0=0x050816:c1=0x7c3aed:c2=0xdb2777:c3=0x06b6d4:c4=0x0f172a",
    )
    write_settings(
        ["aurora-drift.jpg", "dusk-ridge.jpg", "neon-harbor.jpg", "silk-dunes.jpg", "forest-mist.jpg", "chromatic-flow.jpg"],
        ["flux-loop.mp4", "neon-pulse.mp4"],
    )
    print(f"demo assets written to {OUT}")


if __name__ == "__main__":
    main()
