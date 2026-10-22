"""Derive application icons from the approved artwork without changing its design.

Requires Pillow. Run from any directory with Python; all paths are repository-relative.
"""

from pathlib import Path

from PIL import Image, ImageChops, ImageDraw, ImageOps


ROOT = Path(__file__).resolve().parent.parent
SOURCE = ROOT / "assets/branding/app-icon-source.png"
WINDOWS = ROOT / "src/windows/Fy1Switch.Host/Assets"
ANDROID = ROOT / "src/android/app/src/main/res"


def fitted(art: Image.Image, canvas_size: int, extent: int) -> Image.Image:
    scaled = art.copy()
    scaled.thumbnail((extent, extent), Image.Resampling.LANCZOS)
    canvas = Image.new("RGBA", (canvas_size, canvas_size))
    canvas.alpha_composite(scaled, ((canvas_size - scaled.width) // 2, (canvas_size - scaled.height) // 2))
    return canvas


def main() -> None:
    source = Image.open(SOURCE).convert("RGBA")
    # Ignore almost invisible alpha noise when fitting, retaining the original file verbatim.
    bounds = source.getchannel("A").point(lambda alpha: 255 if alpha >= 8 else 0).getbbox()
    if bounds is None:
        raise ValueError("Approved artwork is empty")
    art = source.crop(bounds)
    WINDOWS.mkdir(parents=True, exist_ok=True)
    icon = fitted(art, 256, 240)
    icon.save(WINDOWS / "App.png")
    icon.save(WINDOWS / "App.ico", sizes=[(n, n) for n in (16, 20, 24, 32, 40, 48, 64, 128, 256)])

    # Keep the entire monitor inside Android's 66dp adaptive-icon safe circle.
    # Foreground canvas is 108dp at xxxhdpi. This accounts for the actual silhouette,
    # rather than assuming a square bounding box fits a circular mask.
    alpha = art.getchannel("A")
    cx, cy = art.width / 2, art.height / 2
    radius = max(((x - cx) ** 2 + (y - cy) ** 2) ** 0.5
                 for y in range(art.height) for x in range(art.width) if alpha.getpixel((x, y)) >= 8)
    scale = 31 * 4 / radius  # 2dp clearance within the 33dp safe radius.
    scaled = art.resize((round(art.width * scale), round(art.height * scale)), Image.Resampling.LANCZOS)
    foreground = Image.new("RGBA", (432, 432))
    foreground.alpha_composite(scaled, ((432 - scaled.width) // 2, (432 - scaled.height) // 2))
    # Derive the themed silhouette from the same artwork. The light toggle becomes
    # negative space, preserving the monitor, Fy mark and switch rather than using a stock logo.
    silhouette = ImageOps.grayscale(foreground).point(lambda value: max(0, min(255, (230 - value) * 3)))
    silhouette = ImageChops.multiply(silhouette, foreground.getchannel("A"))
    monochrome = Image.new("RGBA", foreground.size, "white")
    monochrome.putalpha(silhouette)
    for density, dpi in (("mdpi", 1), ("hdpi", 1.5), ("xhdpi", 2), ("xxhdpi", 3), ("xxxhdpi", 4)):
        folder = ANDROID / f"mipmap-{density}"
        folder.mkdir(parents=True, exist_ok=True)
        foreground.resize((round(108 * dpi), round(108 * dpi)), Image.Resampling.LANCZOS).save(folder / "ic_launcher_foreground.png")
        monochrome.resize((round(108 * dpi), round(108 * dpi)), Image.Resampling.LANCZOS).save(folder / "ic_launcher_monochrome.png")
        # Legacy launchers use the same artwork at a larger size, without an adaptive mask.
        fitted(art, round(48 * dpi), round(44 * dpi)).save(folder / "ic_launcher.png")

    preview = Image.new("RGB", (720, 330), "#EEF2F8")
    draw = ImageDraw.Draw(preview)
    draw.text((24, 14), "Windows / Android adaptive mask", fill="#243044")
    windows = fitted(art, 256, 240)
    preview.paste(windows, (30, 54), windows)
    background = Image.new("RGBA", (432, 432), "#EAF3FF")
    background.alpha_composite(foreground)
    circle = Image.new("L", (432, 432))
    ImageDraw.Draw(circle).ellipse((72, 72, 360, 360), fill=255)  # visible 72dp mask
    masked = background.crop((72, 72, 360, 360)).resize((256, 256), Image.Resampling.LANCZOS)
    mask = circle.crop((72, 72, 360, 360)).resize((256, 256), Image.Resampling.LANCZOS)
    preview.paste(masked.convert("RGB"), (420, 54), mask)
    output = ROOT / "artifacts/fluent-ui-20261021"
    output.mkdir(parents=True, exist_ok=True)
    preview.save(output / "application-icons.png")
    print("Generated Windows ICO/PNG and Android density/adaptive foreground resources.")


if __name__ == "__main__":
    main()
