#!/usr/bin/env python3
"""The HUD's knife icon, hud/images/knife.png: an original drawing, made here from straight lines and curves - no game art, so the
public repository alone can rebuild the HUD (the owner's 9 a, 2026-11-02; until content v4.1.0 the icon came from CS2's own HUD art).
White on transparent, tip to the right: the stylesheet washes it gold or dark, and turns it for the emblem. Needs Pillow.

    python hud/knife.py
"""
import os

from PIL import Image, ImageChops, ImageDraw

SIZE = (512, 256)  # what hud/images/knife.vtex compiles; the stylesheet keeps its 2:1
SCALE = 8          # drawn this much larger, then scaled down: smooth edges


def curve(p0, p1, p2, p3, steps=40):
    """A cubic Bezier curve as points, without its first point."""
    points = []
    for i in range(1, steps + 1):
        t = i / steps
        u = 1 - t
        points.append((u**3 * p0[0] + 3 * u**2 * t * p1[0] + 3 * u * t**2 * p2[0] + t**3 * p3[0],
                       u**3 * p0[1] + 3 * u**2 * t * p1[1] + 3 * u * t**2 * p2[1] + t**3 * p3[1]))
    return points


def outline():
    """Clockwise from the butt's top corner, on a 512 x 256 sheet: a hunting knife with a clipped point and one finger guard."""
    p = [(22, 104)]
    p += curve((22, 104), (70, 94), (130, 92), (190, 96))     # the back of the handle
    p += [(206, 96)]                                          # into the bolster
    p += [(214, 88), (232, 88), (236, 96)]                    # the guard's small top lug
    p += [(372, 96)]                                          # the blade's straight spine
    p += curve((372, 96), (405, 104), (440, 118), (492, 132)) # the clip, down to the point
    p += curve((492, 132), (450, 160), (400, 170), (340, 170))  # the belly, back from the point
    p += [(238, 170)]                                         # the straight edge, to the ricasso
    p += [(236, 186), (226, 204), (212, 206), (208, 190), (208, 172)]  # the finger guard, below
    p += curve((208, 172), (188, 178), (176, 168), (160, 168))  # the handle's belly: a finger's dip
    p += curve((160, 168), (144, 168), (136, 178), (118, 176))
    p += curve((118, 176), (102, 174), (94, 166), (78, 168))
    p += curve((78, 168), (60, 170), (42, 176), (30, 170))    # to the butt
    p += [(16, 140)]                                          # the butt, cut at an angle
    return p


def holes():
    """What is cut out of it: the blade's fuller, two rivets and the lanyard hole."""
    fuller = [(250, 112)] + curve((250, 112), (290, 110), (340, 110), (384, 118), 16) + curve((384, 118), (340, 124), (290, 126), (250, 124), 16)
    return [fuller], [(146, 128, 7), (88, 128, 7), (42, 132, 9)]


def main():
    w, h = SIZE
    big = Image.new("L", (w * SCALE, h * SCALE), 0)
    d = ImageDraw.Draw(big)
    d.polygon([(x * SCALE, y * SCALE) for x, y in outline()], fill=255)
    polygons, circles = holes()
    for polygon in polygons:
        d.polygon([(x * SCALE, y * SCALE) for x, y in polygon], fill=0)
    for x, y, r in circles:
        d.ellipse([(x - r) * SCALE, (y - r) * SCALE, (x + r) * SCALE, (y + r) * SCALE], fill=0)
    alpha = big.resize(SIZE, Image.LANCZOS)
    icon = Image.merge("RGBA", (*[Image.new("L", SIZE, 255)] * 3, ImageChops.lighter(alpha, Image.new("L", SIZE, 0))))
    out = os.path.join(os.path.dirname(os.path.abspath(__file__)), "images", "knife.png")
    icon.save(out, optimize=True)
    print(f"{out}: {SIZE[0]}x{SIZE[1]}, {os.path.getsize(out)} bytes")


if __name__ == "__main__":
    main()
