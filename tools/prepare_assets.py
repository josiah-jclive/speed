"""Fetch and generate the binary files JSPEED builds with, so the repository holds only text.

  python tools/prepare_assets.py

Needs Python 3.10+, Pillow and CairoSVG (pip install pillow cairosvg). Downloads are pinned to
exact commits, so every build gets identical files:
  - Blackmagic DeckLink SDK 12.0 headers (Boost licence), from OBS Studio's copy
  - Geist, Geist Mono and Poppins fonts (SIL Open Font License)
It then draws the record-dot icons and renders the JSPEED by JC LIVE logo for the title bar.
"""
import pathlib
import urllib.request

from PIL import Image, ImageDraw

ROOT = pathlib.Path(__file__).resolve().parent.parent

OBS = "https://raw.githubusercontent.com/obsproject/obs-studio/fe1ed7955f300efd17fc59f65938388e43d8bb06/plugins/decklink"
GEIST = "https://raw.githubusercontent.com/vercel/geist-font/ef2597cfc42f4797676df105517ccc0a1cfa6569"
GFONTS = "https://raw.githubusercontent.com/google/fonts/cc39c4c2c304bc368c9b7c311ea2bcc6013299fe/ofl/poppins"

SDK_WIN = [
    "DeckLinkAPI.idl", "DeckLinkAPIConfiguration.idl", "DeckLinkAPIDeckControl.idl", "DeckLinkAPIDiscovery.idl",
    "DeckLinkAPIModes.idl", "DeckLinkAPIStreaming.idl", "DeckLinkAPIStreaming_v10_8.idl", "DeckLinkAPITypes.idl",
    "DeckLinkAPIVersion.h", "DeckLinkAPI_v10_11.idl", "DeckLinkAPI_v10_2.idl", "DeckLinkAPI_v10_4.idl",
    "DeckLinkAPI_v10_5.idl", "DeckLinkAPI_v10_6.idl", "DeckLinkAPI_v10_8.idl", "DeckLinkAPI_v10_9.idl",
    "DeckLinkAPI_v11_4.idl", "DeckLinkAPI_v11_5.idl", "DeckLinkAPI_v11_5_1.idl", "DeckLinkAPI_v7_1.idl",
    "DeckLinkAPI_v7_3.idl", "DeckLinkAPI_v7_6.idl", "DeckLinkAPI_v7_9.idl", "DeckLinkAPI_v8_0.idl",
    "DeckLinkAPI_v8_1.idl", "DeckLinkAPI_v9_2.idl", "DeckLinkAPI_v9_9.idl",
]
SDK_MAC = [
    "DeckLinkAPI.h", "DeckLinkAPIConfiguration.h", "DeckLinkAPIDeckControl.h", "DeckLinkAPIDiscovery.h",
    "DeckLinkAPIDispatch.cpp", "DeckLinkAPIModes.h", "DeckLinkAPIStreaming.h", "DeckLinkAPITypes.h",
    "DeckLinkAPIVersion.h",
]
FONTS = {
    f"{GEIST}/fonts/Geist/ttf/Geist-Regular.ttf": "Geist-Regular.ttf",
    f"{GEIST}/fonts/Geist/ttf/Geist-Medium.ttf": "Geist-Medium.ttf",
    f"{GEIST}/fonts/Geist/ttf/Geist-SemiBold.ttf": "Geist-SemiBold.ttf",
    f"{GEIST}/fonts/Geist/ttf/Geist-Bold.ttf": "Geist-Bold.ttf",
    f"{GEIST}/fonts/GeistMono/ttf/GeistMono-Regular.ttf": "GeistMono-Regular.ttf",
    f"{GEIST}/fonts/GeistMono/ttf/GeistMono-Medium.ttf": "GeistMono-Medium.ttf",
    f"{GEIST}/OFL.txt": "Geist-OFL.txt",
    f"{GFONTS}/Poppins-SemiBold.ttf": "Poppins-SemiBold.ttf",
    f"{GFONTS}/OFL.txt": "Poppins-OFL.txt",
}


def fetch(url: str, dest: pathlib.Path) -> None:
    dest.parent.mkdir(parents=True, exist_ok=True)
    with urllib.request.urlopen(url, timeout=60) as r:
        dest.write_bytes(r.read())
    print(f"fetched {dest.relative_to(ROOT)}")


# ---------- Icons: record dot with speed lines ----------

LINES = (0x8B, 0x94, 0xA3, 255)
RING = (0x5A, 0x62, 0x70, 255)
DOT = {
    "ready": (0xEC, 0xEE, 0xF1, 255),
    "recording": (0xE5, 0x48, 0x4D, 255),
    "attention": (0xF5, 0xA5, 0x24, 255),
}


def draw_icon(size: int, state: str) -> Image.Image:
    s = size * 4  # supersample, then scale down for clean edges
    u = s / 64
    im = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    small = size <= 24
    if small:  # two thicker lines survive at 16-24 px
        bars, (cx, cy, r) = [(2, 22, 20, 29), (2, 36, 20, 43)], (41, 32, 19)
    else:
        bars, (cx, cy, r) = [(4, 22, 20, 27), (9, 30, 21, 35), (4, 38, 20, 43)], (41, 32, 18)
    for x0, y0, x1, y1 in bars:
        d.rounded_rectangle([x0 * u, y0 * u, x1 * u, y1 * u], radius=(y1 - y0) / 2 * u, fill=LINES)
    if state == "ready":  # white dot with a grey ring, visible on light and dark taskbars
        ring = 4 if small else 3
        d.ellipse([(cx - r) * u, (cy - r) * u, (cx + r) * u, (cy + r) * u], fill=RING)
        d.ellipse([(cx - r + ring) * u, (cy - r + ring) * u, (cx + r - ring) * u, (cy + r - ring) * u], fill=DOT["ready"])
    else:
        d.ellipse([(cx - r) * u, (cy - r) * u, (cx + r) * u, (cy + r) * u], fill=DOT[state])
    return im.resize((size, size), Image.LANCZOS)


def make_icons() -> None:
    icons = ROOT / "src/Speed/Assets/Icons"
    icons.mkdir(parents=True, exist_ok=True)
    for state in DOT:
        draw_icon(256, state).save(icons / f"state-{state}.png")
    sizes = [16, 24, 32, 48, 64, 128, 256]
    images = [draw_icon(sz, "recording") for sz in sizes]
    images[-1].save(icons / "app.ico", format="ICO", sizes=[(sz, sz) for sz in sizes], append_images=images[:-1])
    big = draw_icon(1024, "recording")
    big.save(ROOT / "packaging/icon-1024.png")
    big.save(ROOT / "packaging/mac/JSPEED.icns")
    print("drew icons")


def make_logo() -> None:
    import cairosvg

    src = ROOT / "packaging/JSPEED-by-JC-LIVE-white-knockout.svg"
    dest = ROOT / "src/Speed/Assets/jspeed-by-jc-live.png"
    cairosvg.svg2png(url=str(src), write_to=str(dest), output_height=160)
    print("rendered title-bar logo")


def main() -> None:
    sdk = ROOT / "native/speed_decklink/sdk"
    for name in SDK_WIN:
        fetch(f"{OBS}/win/decklink-sdk/{name}", sdk / "win" / name)
    for name in SDK_MAC:
        fetch(f"{OBS}/mac/decklink-sdk/{name}", sdk / "mac" / name)
    fetch(f"{OBS}/LICENSE.decklink-sdk", sdk / "LICENSE.txt")

    fonts = ROOT / "src/Speed/Assets/Fonts"
    for url, name in FONTS.items():
        fetch(url, fonts / name)

    make_icons()
    make_logo()


if __name__ == "__main__":
    main()
