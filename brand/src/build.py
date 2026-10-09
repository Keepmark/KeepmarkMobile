"""Keepmark brand asset builder (canvas icon 5D).

Builds the brand kit in brand/svg, brand/png and the 01..10 sheet crops. The
icon is canvas design 5D: a silver ribbon hanging from a dark slot on a
radial navy tile, with a recessed keyhole. The flat mark is the same ribbon
as a one-color silhouette. The "Keepmark" wordmark is vector outlines in
wordmark.svg. Everything is rendered with rsvg-convert.

    python3 brand/src/build.py

The shipped app assets (Android adaptive mipmaps, splash, extension icons)
were exported from the same 5D geometry and are committed in their repos;
this script no longer overwrites them.

Colors
    tile       #3a56b4 > #1f3075 > #0f1838  radial gradient, light from top left
    slot       #070b1e  the slot the ribbon hangs from
    silver     #dfe3f2 > #ffffff > #f3f5fb > #c6cde6  ribbon, lit from the left
    navy       #243878  brand primary (wordmark, flat marks)
    navy-top   #2B4288  flat mark gradient start
    navy-deep  #1C2D6A  flat mark gradient end
    ink        #111111  monochrome black
"""
import os
import re
import subprocess
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
BRAND = os.path.dirname(HERE)

NAVY, NAVY_TOP, NAVY_DEEP, INK, WHITE = "#243878", "#2B4288", "#1C2D6A", "#111111", "#FFFFFF"

# ---- 5D geometry, 108 x 108 canvas (Android adaptive icon grid).
RIB = "M37 1h34v81l-17-11-17 11z"
KH = "M54 42a6 6 0 0 0-3 11.2L49.4 63h9.2L57 53.2A6 6 0 0 0 54 42z"
# Launchers show only the centre 72 (18..90): the design is mapped there.
G = '<g transform="translate(18 18) scale(0.6667)">'
# Flat mark bounds inside the design: ribbon 34 x 81 from (37, 1).
MX, MY, MW, MH = 37.0, 1.0, 34.0, 81.0

TILE_DEFS = (
    '<radialGradient id="r" cx="0.25" cy="0.15" r="1.1"><stop offset="0" stop-color="#3a56b4"/>'
    '<stop offset="0.55" stop-color="#1f3075"/><stop offset="1" stop-color="#0f1838"/></radialGradient>')
RIBBON_DEFS = (
    '<filter id="s" x="-50%" y="-20%" width="200%" height="150%">'
    '<feDropShadow dx="3" dy="4" stdDeviation="3.5" flood-color="#000" flood-opacity="0.55"/></filter>'
    '<linearGradient id="c" x1="0" y1="0" x2="1" y2="0"><stop offset="0" stop-color="#dfe3f2"/>'
    '<stop offset="0.35" stop-color="#fff"/><stop offset="0.7" stop-color="#f3f5fb"/><stop offset="1" stop-color="#c6cde6"/></linearGradient>'
    '<linearGradient id="t" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#000" stop-opacity="0.45"/>'
    '<stop offset="1" stop-color="#000" stop-opacity="0"/></linearGradient>'
    f'<clipPath id="k"><path d="{KH}"/></clipPath><clipPath id="b"><path d="{RIB}"/></clipPath>')

TILE_BG = '<rect width="108" height="108" fill="url(#r)"/>'
SLOT = ('<rect x="33" y="0" width="42" height="4" rx="2" fill="#070b1e"/>'
        '<rect x="33" y="3.6" width="42" height="1" fill="rgba(255,255,255,0.18)"/>')
RIBBON = (f'<path d="{RIB}" fill="url(#c)" filter="url(#s)"/>'
          '<rect x="37" y="0" width="34" height="16" fill="url(#t)" clip-path="url(#b)"/>'
          '<path d="M37 82l17-11 17 11" fill="none" stroke="rgba(20,30,70,0.18)" stroke-width="1"/>'
          f'<path d="{KH}" fill="#fff" fill-opacity="0.9" transform="translate(0 1)"/>'
          f'<g clip-path="url(#k)"><rect x="40" y="38" width="28" height="30" fill="#0b1336"/>'
          f'<path d="{KH}" fill="#2a3f8a" transform="translate(0 1.8)"/></g>')


def svg(w, h, body, defs=""):
    d = f"<defs>{defs}</defs>" if defs else ""
    return f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {w:g} {h:g}" width="{w:g}" height="{h:g}">{d}{body}</svg>\n'


def tile_body(shape="rounded"):
    """The full 5D icon on the 108 grid. shape: rounded | circle | square."""
    clip = {"rounded": '<rect width="108" height="108" rx="24"/>',
            "circle": '<circle cx="54" cy="54" r="54"/>',
            "square": '<rect width="108" height="108"/>'}[shape]
    defs = TILE_DEFS + RIBBON_DEFS + f'<clipPath id="q">{clip}</clipPath>'
    return defs, f'<g clip-path="url(#q)">{TILE_BG}{G}{SLOT}{RIBBON}</g></g>'


def place_tile(x, y, size, shape="rounded"):
    defs, body = tile_body(shape)
    return defs, f'<g transform="translate({x:.3f},{y:.3f}) scale({size / 108:.5f})">{body}</g>'


def tile_svg(size, shape):
    defs, body = place_tile(0, 0, size, shape)
    return svg(size, size, body, defs)


def mark_svg(fill, gradient=False):
    """Flat ribbon silhouette with the keyhole cut out, cropped to the ribbon."""
    defs = (f'<linearGradient id="g" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="{NAVY_TOP}"/>'
            f'<stop offset="1" stop-color="{NAVY_DEEP}"/></linearGradient>') if gradient else ""
    body = (f'<path transform="translate({-MX:g},{-MY:g})" fill-rule="evenodd" '
            f'fill="{"url(#g)" if gradient else fill}" d="{RIB} {KH}"/>')
    return svg(MW, MH, body, defs)


def wordmark():
    s = open(os.path.join(HERE, "wordmark.svg")).read()
    vb = [float(v) for v in re.search(r'viewBox="([^"]+)"', s).group(1).split()]
    d = re.search(r' d="([^"]+)"', s).group(1)
    return vb[2], vb[3], d


def place_word(x, y, height, fill):
    ww, wh, d = wordmark()
    k = height / wh
    return f'<path transform="translate({x:.3f},{y:.3f}) scale({k:.5f})" fill-rule="evenodd" fill="{fill}" d="{d}"/>'


def horizontal_svg(word_fill):
    # Tile 150 tall, 37 gap, wordmark at its native 80.77 height, centred.
    ts = 150.0
    ww, wh, _ = wordmark()
    defs, tile = place_tile(0, 0, ts)
    return svg(ts + 37 + ww, ts, tile + place_word(ts + 37, (ts - wh) / 2, wh, word_fill), defs)


def stacked_svg(word_fill):
    # Tile 190 wide over a 235 wide wordmark, 24 gap.
    ts, tw = 190.0, 235.0
    ww, wh, _ = wordmark()
    th = wh * tw / ww
    defs, tile = place_tile((tw - ts) / 2, 0, ts)
    return svg(tw, ts + 24 + th, tile + place_word(0, ts + 24, th, word_fill), defs)


def adaptive_bg_svg():
    return svg(108, 108, f'{TILE_BG}{G}{SLOT}</g>', TILE_DEFS)


def adaptive_fg_svg():
    return svg(108, 108, f'{G}{RIBBON}</g>', RIBBON_DEFS)


def adaptive_mono_svg():
    # Themed icon (Android 13+): alpha only, ribbon with the keyhole cut out.
    return svg(108, 108, f'{G}<path d="{RIB} {KH}" fill="#fff" fill-rule="evenodd"/></g>')


def adaptive_preview_svg():
    # Background + foreground as a launcher composes them (square mask).
    return svg(108, 108, f'{TILE_BG}{G}{SLOT}{RIBBON}</g>', TILE_DEFS + RIBBON_DEFS)


def notification_svg():
    # Status-bar icon: white silhouette on transparent, 24dp grid, 2dp padding.
    k = 20 / MH
    x = (24 - MW * k) / 2
    return svg(24, 24, f'<path transform="translate({x:.3f},2) scale({k:.5f}) translate({-MX:g},{-MY:g})" '
                       f'fill-rule="evenodd" fill="{WHITE}" d="{RIB} {KH}"/>')


# ---- Rendering ------------------------------------------------------------------

def write(path, text):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w") as f:
        f.write(text)


def render(svg_text, out, w=None, h=None):
    os.makedirs(os.path.dirname(out), exist_ok=True)
    with tempfile.NamedTemporaryFile("w", suffix=".svg", delete=False) as f:
        f.write(svg_text)
        tmp = f.name
    args = ["rsvg-convert", tmp, "-o", out]
    if w:
        args += ["-w", str(w)]
    if h:
        args += ["-h", str(h)]
    subprocess.run(args, check=True)
    os.unlink(tmp)


def build():
    S = os.path.join(BRAND, "svg")
    P = os.path.join(BRAND, "png")
    sources = {
        "mark.svg": mark_svg(NAVY, gradient=True),
        "mark-navy.svg": mark_svg(NAVY),
        "mark-white.svg": mark_svg(WHITE),
        "mark-black.svg": mark_svg(INK),
        "logo-horizontal.svg": horizontal_svg(NAVY),
        "logo-horizontal-white.svg": horizontal_svg(WHITE),
        "logo-stacked.svg": stacked_svg(NAVY),
        "logo-stacked-white.svg": stacked_svg(WHITE),
        "icon-rounded.svg": tile_svg(512, "rounded"),
        "icon-circle.svg": tile_svg(512, "circle"),
        "icon-square.svg": tile_svg(512, "square"),
        "android-adaptive-foreground.svg": adaptive_fg_svg(),
        "android-adaptive-background.svg": adaptive_bg_svg(),
        "android-monochrome.svg": adaptive_mono_svg(),
        "android-notification.svg": notification_svg(),
    }
    for name, text in sources.items():
        write(os.path.join(S, name), text)

    # High-res PNGs for docs/stores.
    render(sources["mark.svg"], f"{P}/mark-1024.png", h=1024)
    render(sources["mark-white.svg"], f"{P}/mark-white-1024.png", h=1024)
    render(sources["logo-horizontal.svg"], f"{P}/logo-horizontal-2048.png", w=2048)
    render(sources["logo-horizontal-white.svg"], f"{P}/logo-horizontal-white-2048.png", w=2048)
    render(sources["logo-stacked.svg"], f"{P}/logo-stacked-1024.png", w=1024)
    render(sources["icon-rounded.svg"], f"{P}/icon-rounded-1024.png", w=1024)
    render(sources["icon-circle.svg"], f"{P}/icon-circle-1024.png", w=1024)
    render(sources["icon-square.svg"], f"{P}/play-store-512.png", w=512)  # Play requires full-bleed square

    # Clean replacements for the sheet crops (same numbering as the sheet).
    crops = [
        ("01-full-horizontal.png", sources["logo-horizontal.svg"], 1600, None),
        ("02-stacked.png", sources["logo-stacked.svg"], 800, None),
        ("03-standalone.png", sources["icon-rounded.svg"], 800, None),
        ("04-adaptive-foreground.png", adaptive_preview_svg(), 432, None),
        ("05-circular.png", sources["icon-circle.svg"], 512, None),
        ("06-mono-black.png", sources["mark-black.svg"], None, 800),
        ("07-mono-white.png", sources["mark-white.svg"], None, 800),
        ("08-splash.png", sources["logo-stacked.svg"], 831, None),
        ("09-notification.png", sources["android-notification.svg"], 96, None),
        ("10-favicon.png", sources["icon-rounded.svg"], 256, None),
    ]
    for name, text, w, h in crops:
        render(text, os.path.join(BRAND, name), w=w, h=h)
    return sources


if __name__ == "__main__":
    build()
    print("ok")
