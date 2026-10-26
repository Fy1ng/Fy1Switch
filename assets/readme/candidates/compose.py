"""Render six local README hero candidates using licensed upstream artwork.

Requires Pillow and the Windows Segoe UI / Microsoft YaHei fonts.
Run from any working directory: python assets/readme/candidates/compose.py
Upstream originals and license texts are kept unchanged in sources/.
"""

from pathlib import Path
import hashlib
import json

from PIL import Image, ImageDraw, ImageFilter, ImageFont


ROOT = Path(__file__).resolve().parent
SCALE = 2
SIZE = (1200, 440)
FONTS = Path("C:/Windows/Fonts")


def font(size, bold=False, chinese=False):
    filename = ("msyhbd.ttc" if bold else "msyh.ttc") if chinese else (
        "segoeuib.ttf" if bold else "segoeui.ttf"
    )
    return ImageFont.truetype(str(FONTS / filename), round(size * SCALE))


def rect(draw, box, fill, radius=0, outline=None):
    draw.rounded_rectangle(
        tuple(round(value * SCALE) for value in box),
        radius=round(radius * SCALE), fill=fill, outline=outline, width=SCALE,
    )


def text(draw, xy, value, size, color, bold=False, chinese=False):
    draw.text(tuple(round(value * SCALE) for value in xy), value,
              font=font(size, bold, chinese), fill=color, anchor="lt")


def artwork(canvas, filename, xy, size, crop=None):
    with Image.open(ROOT / "sources" / filename) as original:
        asset = original.convert("RGBA")
    if crop:
        asset = asset.crop(crop)
    else:
        asset = asset.crop(asset.getchannel("A").getbbox())
    ratio = min(size[0] * SCALE / asset.width, size[1] * SCALE / asset.height)
    asset = asset.resize((round(asset.width * ratio), round(asset.height * ratio)),
                         Image.Resampling.LANCZOS)
    canvas.alpha_composite(asset, tuple(round(value * SCALE) for value in xy))


def glow(canvas, box, color, blur=60):
    mask = Image.new("L", canvas.size)
    ImageDraw.Draw(mask).ellipse(tuple(round(value * SCALE) for value in box),
                                 fill=color[3])
    layer = Image.new("RGBA", canvas.size, (*color[:3], 0))
    layer.putalpha(mask.filter(ImageFilter.GaussianBlur(blur * SCALE)))
    canvas.alpha_composite(layer)


def headline(canvas, language, palette):
    chinese = language == "zh-CN"
    draw = ImageDraw.Draw(canvas)
    fg, accent, muted, chip = palette
    text(draw, (60, 46), "FY1SWITCH  /  WINDOWS + ANDROID", 17, muted, bold=True)
    lines = ("显示器开关,", "就在手边.") if chinese else ("Your display.", "One tap away.")
    for index, line in enumerate(lines):
        text(draw, (57, 116 + index * 70), line, 53, accent if index else fg,
             bold=True, chinese=chinese)
    support = "电脑绑定目标, 手机轻点操作." if chinese else "Bind on Windows. Control from Android."
    text(draw, (60, 281), support, 21 if chinese else 20, muted, chinese=chinese)
    for label, x, width in (("LAN", 60, 75), ("Tailscale", 147, 123), ("Relay", 282, 90)):
        rect(draw, (x, 352, x + width, 389), chip, 18)
        label_width = draw.textlength(label, font=font(16, True)) / SCALE
        text(draw, (x + (width - label_width) / 2, 363), label, 16, fg, bold=True)


def render(variant, language):
    if variant == "a":
        canvas = Image.new("RGBA", (SIZE[0] * SCALE, SIZE[1] * SCALE), "#171924")
        glow(canvas, (725, 45, 1110, 370), (93, 60, 173, 75), 60)
        headline(canvas, language, ("#F5F4FC", "#C6ADFF", "#B9B8CD", "#2C2B3E"))
        artwork(canvas, "3dicons-computer.png", (660, 59), (300, 320))
        artwork(canvas, "3dicons-mobile.png", (943, 170), (182, 228))
    elif variant == "b":
        canvas = Image.new("RGBA", (SIZE[0] * SCALE, SIZE[1] * SCALE), "#EDF4FB")
        glow(canvas, (670, 44, 1100, 390), (185, 218, 255, 150), 54)
        headline(canvas, language, ("#182C47", "#315DB3", "#5C7089", "#DBE7F5"))
        artwork(canvas, "fluent-computer.png", (637, 82), (304, 304))
        artwork(canvas, "fluent-mobile.png", (957, 150), (155, 217))
    else:
        canvas = Image.new("RGBA", (SIZE[0] * SCALE, SIZE[1] * SCALE), "#435175")
        headline(canvas, language, ("#FFF7EE", "#FFBB6A", "#D4DEF0", "#354362"))
        # Crop the empty margins of the original square scene. Do not redraw it.
        artwork(canvas, "illlustrations-workstation.png", (594, 140), (568, 258),
                crop=(55, 270, 950, 670))
    # Transparent corners are safe on both light and dark README backgrounds.
    mask = Image.new("L", canvas.size)
    ImageDraw.Draw(mask).rounded_rectangle((0, 0, canvas.width, canvas.height),
                                           24 * SCALE, fill=255)
    canvas.putalpha(mask)
    canvas = canvas.resize(SIZE, Image.Resampling.LANCZOS)
    output = ROOT / f"hero-{variant}.{language}.png"
    canvas.save(output, optimize=True)
    return {"file": output.name, "sha256": hashlib.sha256(output.read_bytes()).hexdigest()}


if __name__ == "__main__":
    manifest = []
    for variant in "abc":
        for language in ("zh-CN", "en"):
            manifest.append(render(variant, language))
    (ROOT / "renders.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    print("Rendered 6 README candidates at 1200x440.")
