from pathlib import Path
import sys

from PIL import Image


def save_square(source: Image.Image, path: Path, size: int) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    source.resize((size, size), Image.Resampling.LANCZOS).save(path, "PNG", optimize=True)


def main() -> None:
    if len(sys.argv) != 4:
        raise SystemExit("usage: build_icons.py <master.png> <project-root> <preview.png>")

    master_path = Path(sys.argv[1])
    project = Path(sys.argv[2])
    preview_path = Path(sys.argv[3])
    source = Image.open(master_path).convert("RGBA")

    pc_assets = project / "pc" / "BF66Host" / "Assets"
    pc_assets.mkdir(parents=True, exist_ok=True)
    source.save(pc_assets / "BF66Icon-master.png", "PNG", optimize=True)
    save_square(source, pc_assets / "BF66Icon.png", 512)
    save_square(source, preview_path, 1024)

    icon_256 = source.resize((256, 256), Image.Resampling.LANCZOS)
    icon_256.save(
        pc_assets / "BF66Icon.ico",
        format="ICO",
        sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)],
    )

    android_res = project / "android" / "app" / "src" / "main" / "res"
    for density, size in {
        "mipmap-mdpi": 48,
        "mipmap-hdpi": 72,
        "mipmap-xhdpi": 96,
        "mipmap-xxhdpi": 144,
        "mipmap-xxxhdpi": 192,
    }.items():
        save_square(source, android_res / density / "ic_launcher.png", size)


if __name__ == "__main__":
    main()
