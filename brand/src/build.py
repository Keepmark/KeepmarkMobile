"""Keepmark brand asset builder.

Single source of truth for every icon/logo raster in the Android app and the
browser extension. The bookmark+keyhole mark is pure geometry (below); the
"Keepmark" wordmark is traced once from the brand sheet (trace_wordmark.py ->
wordmark.svg). Everything is rendered with rsvg-convert.

    python3 brand/src/build.py            # brand/svg + brand/png + 01..10 sheet crops
    python3 brand/src/build.py --install  # also refresh the app/extension assets

Colors
    navy       #172552  brand primary (flat fills, small sizes, splash/bars)
    navy-top   #1E2D63  gradient start (large icons only)
    navy-deep  #111A40  gradient end
    ink        #111111  monochrome black
"""
import os
import re
import shutil
import subprocess
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
BRAND = os.path.dirname(HERE)
KEEPMARK = os.path.dirname(BRAND)
ROOT = os.path.dirname(KEEPMARK)
EXT = os.path.join(ROOT, "KeepmarkApp")

NAVY, NAVY_TOP, NAVY_DEEP, INK, WHITE = "#172552", "#1E2D63", "#111A40", "#111111", "#FFFFFF"
GREY = "#5F667A"  # extension "not synced" toolbar state
# Icon tile (extension icons + Android launcher background): a touch
# lighter than brand navy so the icon still reads on dark browser toolbars
# and launchers, without leaving the navy family.
EXT_TILE = "#243878"

# ---- Mark geometry: 100 x 174 units, measured from the sheet's standalone icon.
MW, MH = 100.0, 174.0


def keyhole(scale=1.0):
    """Keyhole path; scale > 1 opens it up for tiny sizes (centered on the bow)."""
    cx, cy, r = 50.0, 64.0, 16.0
    top_hw, bot_hw, bot_y, rad = 8.5, 12.0, 107.5, 3.0
    s = scale
    def P(x, y):  # scale around the keyhole's visual center
        ox, oy = 50.0, 82.0
        return f"{ox + (x - ox) * s:.3f},{oy + (y - oy) * s:.3f}"
    jy = cy + (r * r - top_hw * top_hw) ** 0.5
    return (f"M{P(cx - top_hw, jy)} A{r * s:.3f},{r * s:.3f} 0 1 1 {P(cx + top_hw, jy)} "
            f"L{P(cx + bot_hw - 0.4, bot_y - rad)} Q{P(cx + bot_hw, bot_y)} {P(cx + bot_hw - rad, bot_y)} "
            f"L{P(cx - bot_hw + rad, bot_y)} Q{P(cx - bot_hw, bot_y)} {P(cx - bot_hw + 0.4, bot_y - rad)} Z")


BOOKMARK = "M0,13.7 A13.7,13.7 0 0 1 13.7,0 L86.3,0 A13.7,13.7 0 0 1 100,13.7 L100,174 L50,141 L0,174 Z"


def mark_path(key_scale=1.0):
    return BOOKMARK + " " + keyhole(key_scale)


def grad(gid, a=NAVY_TOP, b=NAVY_DEEP):
    return (f'<linearGradient id="{gid}" x1="0" y1="0" x2="0" y2="1">'
            f'<stop offset="0" stop-color="{a}"/><stop offset="1" stop-color="{b}"/></linearGradient>')


def svg(w, h, body, defs=""):
    d = f"<defs>{defs}</defs>" if defs else ""
    return f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {w:g} {h:g}" width="{w:g}" height="{h:g}">{d}{body}</svg>\n'


def place_mark(x, y, height, fill, key_scale=1.0):
    k = height / MH
    return (f'<path transform="translate({x:.3f},{y:.3f}) scale({k:.5f})" '
            f'fill-rule="evenodd" fill="{fill}" d="{mark_path(key_scale)}"/>')


def wordmark():
    s = open(os.path.join(HERE, "wordmark.svg")).read()
    vb = [float(v) for v in re.search(r'viewBox="([^"]+)"', s).group(1).split()]
    d = re.search(r' d="([^"]+)"', s).group(1)
    return vb[2], vb[3], d


def place_word(x, y, height, fill):
    ww, wh, d = wordmark()
    k = height / wh
    return f'<path transform="translate({x:.3f},{y:.3f}) scale({k:.5f})" fill-rule="evenodd" fill="{fill}" d="{d}"/>'


# ---- Compositions --------------------------------------------------------------

def mark_svg(fill, gradient=False, key_scale=1.0):
    defs = grad("g") if gradient else ""
    return svg(MW, MH, place_mark(0, 0, MH, "url(#g)" if gradient else fill, key_scale), defs)


def tile_svg(size, shape, bg, fg, mark_frac, key_scale=1.0, gradient=True, inverted=True):
    """Mark on a tile. shape: square | rounded | circle. inverted = white
    bookmark whose keyhole shows the tile color (sheet variants 5, 7, 9, 10)."""
    defs = grad("g") if gradient else ""
    fill_bg = "url(#g)" if gradient else bg
    if shape == "circle":
        tile = f'<circle cx="{size / 2}" cy="{size / 2}" r="{size / 2}" fill="{fill_bg}"/>'
    else:
        rx = {"square": 0, "rounded": size * 0.225}[shape]
        tile = f'<rect width="{size}" height="{size}" rx="{rx:g}" fill="{fill_bg}"/>'
    h = size * mark_frac
    w = h * MW / MH
    # optical centering: the notch makes the mark look top-heavy, nudge down 2%
    x, y = (size - w) / 2, (size - h) / 2 + size * 0.02
    return svg(size, size, tile + place_mark(x, y, h, fg, key_scale), defs)


def horizontal_svg(mark_fill, word_fill, gradient=True):
    # Sheet layout (px): mark 89x150 at (77,115); wordmark top at y=155, x=200.
    mh = 150.0
    ww, wh, _ = wordmark()
    W = (200 - 77) + ww
    H = mh
    defs = grad("g") if gradient else ""
    body = place_mark(0, 0, mh, "url(#g)" if gradient else mark_fill) + place_word(123, 40, wh, word_fill)
    return svg(W, H, body, defs)


def stacked_svg(mark_fill, word_fill, gradient=True):
    # Sheet layout: mark 111x190, 20px gap, wordmark 235 wide.
    mh = 190.0
    mw = mh * MW / MH
    ww, wh, _ = wordmark()
    tw = 235.0
    th = wh * tw / ww
    W, H = tw, mh + 20 + th
    defs = grad("g") if gradient else ""
    body = (place_mark((W - mw) / 2, 0, mh, "url(#g)" if gradient else mark_fill)
            + place_word(0, mh + 20, th, word_fill))
    return svg(W, H, body, defs)


def adaptive_fg_svg(fill=WHITE):
    # Android adaptive icon foreground, 108dp canvas: mark 56dp tall stays
    # inside the 66dp safe circle (half-diagonal 32.3dp).
    return svg(108, 108, place_mark((108 - 56 * MW / MH) / 2, (108 - 56) / 2 + 1.5, 56, fill))


def adaptive_preview_svg():
    # Foreground over the navy background, as a launcher composes them.
    fg = place_mark((108 - 56 * MW / MH) / 2, (108 - 56) / 2 + 1.5, 56, WHITE)
    return svg(108, 108, '<rect width="108" height="108" fill="url(#g)"/>' + fg, grad("g"))


def notification_svg():
    # Status-bar icon: white silhouette on transparent, 24dp grid, 2dp padding.
    return svg(24, 24, place_mark((24 - 20 * MW / MH) / 2, 2, 20, WHITE, key_scale=1.25))


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
        "logo-horizontal.svg": horizontal_svg(NAVY, NAVY),
        "logo-horizontal-white.svg": horizontal_svg(WHITE, WHITE, gradient=False),
        "logo-stacked.svg": stacked_svg(NAVY, NAVY),
        "logo-stacked-white.svg": stacked_svg(WHITE, WHITE, gradient=False),
        "icon-rounded.svg": tile_svg(512, "rounded", NAVY, WHITE, 0.62),
        "icon-circle.svg": tile_svg(512, "circle", NAVY, WHITE, 0.58),
        "icon-square.svg": tile_svg(512, "square", NAVY, WHITE, 0.62),
        "android-adaptive-foreground.svg": adaptive_fg_svg(),
        "android-adaptive-background.svg": svg(108, 108, f'<rect width="108" height="108" fill="{EXT_TILE}"/>'),
        "android-monochrome.svg": adaptive_fg_svg(WHITE),
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
        ("03-standalone.png", sources["mark.svg"], None, 800),
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


def install(sources):
    app = os.path.join(KEEPMARK, "src", "Keepmark")
    # MAUI: appicon.png is the adaptive FOREGROUND (Color= in csproj is the
    # background). Keep the tuned 432px canvas with the mark 300px tall.
    fg = svg(432, 432, place_mark((432 - 300 * MW / MH) / 2, 66, 300, WHITE))
    render(fg, os.path.join(app, "Resources", "AppIcon", "appicon.png"), w=432)
    # Splash: stacked logo on transparent (csproj Color is the backdrop), 3x of BaseSize 277x237.
    sp = stacked_svg(NAVY, NAVY)
    vb = [float(v) for v in re.search(r'viewBox="([^"]+)"', sp).group(1).split()]
    pad_w, pad_h = 277.0, 237.0
    k = min(pad_w * 0.92 / vb[2], pad_h * 0.92 / vb[3])
    inner = sp[sp.index("<defs>"):sp.rindex("</svg>")]
    splash = svg(pad_w, pad_h, f'<g transform="translate({(pad_w - vb[2] * k) / 2:.2f},{(pad_h - vb[3] * k) / 2:.2f}) scale({k:.5f})">{inner}</g>')
    render(splash, os.path.join(app, "Resources", "Splash", "splash.png"), w=831)
    # Top bar / drawer mark (white on navy, shown at 28px tall): 4x.
    render(sources["mark-white.svg"], os.path.join(app, "wwwroot", "img", "mark.png"), h=112)
    render(sources["icon-rounded.svg"], os.path.join(app, "wwwroot", "favicon.png"), w=64)

    ext = os.path.join(EXT, "res", "assets")
    if os.path.isdir(ext):
        # Small sizes are pixel-aligned by hand (mark width = whole pixels)
        # with an opened keyhole so it still reads at 16px.
        def small(px, bg):
            mw, x, y, ks, rx = {16: (8, 4, 1, 1.8, 3.5), 32: (16, 8, 2, 1.3, 7)}[px]
            tile = f'<rect width="{px}" height="{px}" rx="{rx}" fill="{bg}"/>'
            return svg(px, px, tile + place_mark(x, y, mw * MH / MW, WHITE, ks))
        render(small(16, EXT_TILE), os.path.join(ext, "icon16.png"), w=16)
        render(small(32, EXT_TILE), os.path.join(ext, "icon32.png"), w=32)
        render(small(16, EXT_TILE), os.path.join(ext, "synced16.png"), w=16)
        render(small(32, EXT_TILE), os.path.join(ext, "synced32.png"), w=32)
        render(small(16, GREY), os.path.join(ext, "notsynced16.png"), w=16)
        render(small(32, GREY), os.path.join(ext, "notsynced32.png"), w=32)
        render(tile_svg(48, "rounded", EXT_TILE, WHITE, 0.66, key_scale=1.1, gradient=False), os.path.join(ext, "icon48.png"), w=48)
        render(tile_svg(128, "rounded", EXT_TILE, WHITE, 0.62, gradient=False), os.path.join(ext, "icon128.png"), w=128)
        render(sources["mark-white.svg"], os.path.join(ext, "brand-mark.png"), h=72)  # 18px tall @4x


if __name__ == "__main__":
    src = build()
    if "--install" in sys.argv:
        install(src)
    print("ok")
