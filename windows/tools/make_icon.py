"""Draws the Wallfly app icon (windows/Assets/Wallfly.ico).

The mark is the one in SidebarView.xaml and the tray: a red (#C32B3D) rounded square with white vertical waveform
bars (the "Waveform" geometry of Wallfly.App/Controls/Icon.cs, 24 grid, bars at x = 2..22). Every size is drawn on its
own 4x supersampled canvas and reduced with LANCZOS; 16 and 20 px use four bars snapped to whole pixels. The ICO is
written by hand (BMP entries below 256 px for maximum compatibility, PNG for 256 px) so each size stays exactly as drawn.

Usage:  python -X utf8 -I windows/tools/make_icon.py [output.ico] [preview-dir]
Needs Pillow.
"""
import io
import struct
import sys
from pathlib import Path

from PIL import Image, ImageDraw

RED = (0xC3, 0x2B, 0x3D, 255)
WHITE = (255, 255, 255, 255)
SIZES = (16, 20, 24, 32, 40, 48, 64, 256)
SS = 4  # supersampling factor

# Icon.cs Waveform: x = 2, 6, 10, 14, 18, 22 on a 24 grid; heights 4, 12, 20, 10, 16, 4.
GRID_X = (2, 6, 10, 14, 18, 22)
GRID_H = (4, 12, 20, 10, 16, 4)

# Hand-snapped bars for the smallest sizes: (bar width, gap, heights) in whole pixels, all even so they centre exactly.
PIXEL_BARS = {
    16: (2, 1, (6, 10, 4, 8)),
    20: (2, 2, (8, 12, 6, 10)),
}


def draw_icon(size: int) -> Image.Image:
    canvas = size * SS
    image = Image.new("RGBA", (canvas, canvas), (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)
    margin = 0 if size <= 20 else (1 if size <= 32 else round(size * 0.06))
    box = size - 2 * margin
    draw.rounded_rectangle((margin * SS, margin * SS, (margin + box) * SS - 1, (margin + box) * SS - 1),
                           radius=box * 6 / 22 * SS, fill=RED)
    centre = size / 2
    if size in PIXEL_BARS:
        width, gap, heights = PIXEL_BARS[size]
        total = len(heights) * width + (len(heights) - 1) * gap
        left = (size - total) // 2  # whole pixels, so every bar edge is crisp
        for index, height in enumerate(heights):
            x0 = left + index * (width + gap)
            draw.rectangle((x0 * SS, (centre - height / 2) * SS, (x0 + width) * SS - 1, (centre + height / 2) * SS - 1), fill=WHITE)
    else:
        # The sidebar mark: waveform icon of 14/22 of the square, stroke 2.2/22 of the square, round caps.
        icon = box * 14 / 22
        scale = icon / 24
        stroke = box * 2.2 / 22
        origin = centre - icon / 2
        for x, height in zip(GRID_X, GRID_H):
            cx = origin + x * scale
            half = height * scale / 2 + stroke / 2  # round caps extend the line by half the stroke
            draw.rounded_rectangle(((cx - stroke / 2) * SS, (centre - half) * SS, (cx + stroke / 2) * SS - 1, (centre + half) * SS - 1),
                                   radius=stroke / 2 * SS, fill=WHITE)
    return image.resize((size, size), Image.LANCZOS)


def bmp_entry(image: Image.Image) -> bytes:
    size = image.width
    pixels = image.convert("RGBA").tobytes()
    rows = []
    for y in range(size - 1, -1, -1):  # bottom-up BGRA
        row = pixels[y * size * 4:(y + 1) * size * 4]
        rows.append(b"".join(bytes((row[i + 2], row[i + 1], row[i], row[i + 3])) for i in range(0, len(row), 4)))
    mask_row = b"\x00" * (((size + 31) // 32) * 4)  # AND mask unused with 32-bit alpha
    header = struct.pack("<IiiHHIIiiII", 40, size, size * 2, 1, 32, 0, 0, 0, 0, 0, 0)
    return header + b"".join(rows) + mask_row * size


def png_entry(image: Image.Image) -> bytes:
    buffer = io.BytesIO()
    image.save(buffer, format="PNG", optimize=True)
    return buffer.getvalue()


def write_ico(path: Path, images: dict) -> None:
    entries = [(size, png_entry(img) if size >= 256 else bmp_entry(img)) for size, img in sorted(images.items())]
    out = bytearray(struct.pack("<HHH", 0, 1, len(entries)))
    offset = 6 + 16 * len(entries)
    for size, data in entries:
        out += struct.pack("<BBBBHHII", size % 256, size % 256, 0, 0, 1, 32, len(data), offset)
        offset += len(data)
    for _, data in entries:
        out += data
    path.write_bytes(bytes(out))


def main() -> None:
    root = Path(__file__).resolve().parent.parent
    target = Path(sys.argv[1]) if len(sys.argv) > 1 else root / "Assets" / "Wallfly.ico"
    preview = Path(sys.argv[2]) if len(sys.argv) > 2 else None
    images = {size: draw_icon(size) for size in SIZES}
    target.parent.mkdir(parents=True, exist_ok=True)
    write_ico(target, images)

    reopened = Image.open(target)
    found = sorted(reopened.info.get("sizes", set()))
    assert found == sorted((s, s) for s in SIZES), f"unexpected sizes {found}"
    for size in SIZES:  # each entry must decode to exactly what was drawn
        reopened.size = (size, size)
        assert list(reopened.convert("RGBA").getdata()) == list(images[size].getdata()), f"entry {size} differs"
    print(f"{target}: {target.stat().st_size} bytes, sizes {[s for s, _ in found]}")

    if preview:
        preview.mkdir(parents=True, exist_ok=True)
        images[256].save(preview / "icon-preview.png")
        strip = Image.new("RGBA", (16 + 32 + 48 + 40 * 2 + 8 * 2 * 3, 64), (255, 255, 255, 255))
        dark = Image.new("RGBA", strip.size, (32, 32, 32, 255))
        x = 8
        for size in (16, 32, 48):
            for sheet in (strip, dark):
                sheet.alpha_composite(images[size], (x, 8))
            x += size + 16
        strip.save(preview / "icon-strip-light.png")
        dark.save(preview / "icon-strip-dark.png")
        # Magnified (nearest) copies of the small sizes for judging pixel crispness.
        zoom = Image.new("RGBA", (16 * 12 + 20 * 12 + 24 * 12 + 48, 24 * 12), (255, 255, 255, 255))
        x = 8
        for size in (16, 20, 24):
            zoom.alpha_composite(images[size].resize((size * 12, size * 12), Image.NEAREST), (x, 0))
            x += size * 12 + 8
        zoom.save(preview / "icon-zoom-small.png")


if __name__ == "__main__":
    main()
