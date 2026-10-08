"""Vectorize the "Keepmark" wordmark from the brand sheet (section 1).

Upscales the crop, then traces the 50% luminance iso-line with matplotlib's
contour engine and writes brand/src/wordmark.svg (fill-rule evenodd).
Run once; the SVG is the source of truth afterwards.
"""
import os
import numpy as np
import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
from PIL import Image, ImageFilter

HERE = os.path.dirname(os.path.abspath(__file__))
SHEET = os.path.join(HERE, "..", "keepmark-brand-sheet.png")
BOX = (195, 140, 615, 245)  # wordmark in "1. Full horizontal logo"
SCALE = 6

im = Image.open(SHEET).convert("L").crop(BOX)
im = im.resize((im.width * SCALE, im.height * SCALE), Image.LANCZOS)
im = im.filter(ImageFilter.GaussianBlur(SCALE * 0.6))
a = 1.0 - np.asarray(im, dtype=float) / 255.0  # ink = 1

cs = plt.contour(a, levels=[0.5])
paths = cs.allsegs[0]

def simplify(pts, tol):
    # Ramer–Douglas–Peucker on a closed ring
    def rdp(p):
        if len(p) < 3:
            return p
        s, e = p[0], p[-1]
        d = e - s
        n = np.hypot(*d) or 1.0
        q = p - s
        dist = np.abs(d[0] * q[:, 1] - d[1] * q[:, 0]) / n
        i = int(np.argmax(dist))
        if dist[i] > tol:
            return np.vstack([rdp(p[: i + 1])[:-1], rdp(p[i:])])
        return np.vstack([s, e])
    # Closed ring: split at the point farthest from the start, simplify halves.
    k = int(np.argmax(np.hypot(*(pts - pts[0]).T)))
    return np.vstack([rdp(pts[: k + 1])[:-1], rdp(pts[k:])])

d = []
ys, xs = [], []
for seg in paths:
    if len(seg) < 8:
        continue
    seg = simplify(np.asarray(seg), 0.12 * SCALE)
    seg = seg / SCALE
    xs += list(seg[:, 0]); ys += list(seg[:, 1])
    d.append(seg)

minx, miny = min(xs), min(ys)
w, h = max(xs) - minx, max(ys) - miny
out = []
for seg in d:
    pts = seg - [minx, miny]
    pts = pts[:-1]
    # Smooth: vertices become quadratic control points, midpoints anchors.
    # Short edges (sharp corners) keep a straight join so stems stay crisp.
    n = len(pts)
    mids = (pts + np.roll(pts, -1, axis=0)) / 2
    parts = [f"M{mids[-1][0]:.2f},{mids[-1][1]:.2f}"]
    for i in range(n):
        x, y = pts[i]
        mx, my = mids[i]
        parts.append(f"Q{x:.2f},{y:.2f} {mx:.2f},{my:.2f}")
    out.append(" ".join(parts) + "Z")
svg = (f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {w:.2f} {h:.2f}">'
       f'<path fill-rule="evenodd" d="{" ".join(out)}"/></svg>\n')
open(os.path.join(HERE, "wordmark.svg"), "w").write(svg)
print("wordmark", round(w, 2), round(h, 2), len(d), "rings", len(svg), "bytes")
