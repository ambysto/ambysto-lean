#!/usr/bin/env python3
"""Generates Ambysto Lean's Windows art from the brand logo (ASP-13).

Source: ambysto-assets/brand/apps/ambysto-space-logo-v3.svg (a private repo; pass its path as the first argument, or set
AMBYSTO_ASSETS, default ~/work/ambysto/ambysto-assets). The SVG is read as plain <rect> elements (x, y, width, height,
rx, fill, stroke, stroke-width, with translate/scale on parent <g>) and drawn with Pillow, supersampled then downscaled.
Output (deterministic: running twice gives identical bytes):
  windows/src/Ambysto.Lean.App/Assets/AmbystoLean.ico   16 20 24 32 40 48 64 128 256 px
  windows/installer/Art/Banner.bmp                      493x58  (top band of the inner installer dialogs)
  windows/installer/Art/Dialog.bmp                      493x312 (welcome and finish dialogs)
Colours are the brand's (DESIGN.md): terminal black #0b0b0c, ink #e8e8e6, rule #222224, Space Coral #f99a8f.
WiX draws its dialog text in black over these bitmaps, so the text areas are ink (light) and the dark part is the
left panel and the logo only: a dark text area would make that text unreadable.
Needs Python 3 and Pillow, nothing else.
"""
import io
import os
import re
import struct
import sys
import xml.etree.ElementTree as ET

from PIL import Image, ImageDraw, ImageFont

ROOT = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
ICO_PATH = os.path.join(ROOT, "windows/src/Ambysto.Lean.App/Assets/AmbystoLean.ico")
BANNER_PATH = os.path.join(ROOT, "windows/installer/Art/Banner.bmp")
DIALOG_PATH = os.path.join(ROOT, "windows/installer/Art/Dialog.bmp")
ICO_SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]
SVG_NAME = "brand/apps/ambysto-space-logo-v3.svg"

BLACK = (0x0B, 0x0B, 0x0C)
INK = (0xE8, 0xE8, 0xE6)
RULE = (0x22, 0x22, 0x24)
CORAL = (0xF9, 0x9A, 0x8F)


def hexcolor(s):
    if not s or s == "none":
        return None
    s = s.lstrip("#")
    return tuple(int(s[i:i + 2], 16) for i in (0, 2, 4))


def parse_transform(t):
    """translate(a b) / scale(s) / scale(sx sy) in order, as (sx, sy, tx, ty): x' = sx*x + tx."""
    sx = sy = 1.0
    tx = ty = 0.0
    for name, args in re.findall(r"(\w+)\(([^)]*)\)", t or ""):
        v = [float(a) for a in re.split(r"[\s,]+", args.strip())]
        if name == "translate":
            tx += sx * v[0]
            ty += sy * (v[1] if len(v) > 1 else 0.0)
        elif name == "scale":
            sx *= v[0]
            sy *= v[1] if len(v) > 1 else v[0]
        else:
            raise SystemExit("unsupported transform: " + name)
    return sx, sy, tx, ty


def read_rects(svg_path):
    """All <rect>s in document order, with parent transforms applied."""
    rects = []

    def walk(el, m, paint):
        for ch in el:
            tag = ch.tag.split("}")[-1]
            cm = m
            if ch.get("transform"):
                a, b, c, d = parse_transform(ch.get("transform"))
                cm = (m[0] * a, m[1] * b, m[2] + m[0] * c, m[3] + m[1] * d)
            if tag in ("defs", "clipPath", "mask", "pattern", "symbol"):
                raise SystemExit("unsupported SVG element <%s>: the logo must be plain rects and groups" % tag)
            if ch.get("style") or ch.get("opacity") or ch.get("fill-opacity") or ch.get("stroke-opacity"):
                raise SystemExit("unsupported on <%s>: style or opacity attributes" % tag)
            # SVG paint inheritance: an element's own fill, stroke and stroke-width, else its group's.
            cp = dict(paint)
            for key in ("fill", "stroke", "stroke-width"):
                if ch.get(key) is not None:
                    cp[key] = ch.get(key)
            if tag not in ("rect", "g", "title", "desc", "metadata"):
                raise SystemExit("unsupported SVG element <%s>: the script draws rects only" % tag)
            if tag == "rect":
                s = cm[0]
                rects.append({
                    "x": cm[2] + cm[0] * float(ch.get("x", 0)),
                    "y": cm[3] + cm[1] * float(ch.get("y", 0)),
                    "w": cm[0] * float(ch.get("width")),
                    "h": cm[1] * float(ch.get("height")),
                    "rx": s * float(ch.get("rx", 0)),
                    "fill": hexcolor(cp.get("fill")),
                    "stroke": hexcolor(cp.get("stroke")),
                    "sw": s * float(cp.get("stroke-width", 1)),
                })
            else:
                walk(ch, cm, cp)

    root = ET.parse(svg_path).getroot()
    walk(root, (1.0, 1.0, 0.0, 0.0), {})
    vb = [float(v) for v in root.get("viewBox").split()]
    if vb[0] != 0 or vb[1] != 0 or vb[2] != vb[3]:
        raise SystemExit("unsupported viewBox %s: the logo must be a square starting at 0 0" % root.get("viewBox"))
    return rects, vb[2], vb[3]


def render_logo(rects, vw, vh, px):
    """The logo on a transparent square of px x px: drawn at a multiple of px, then downscaled."""
    ss = max(4, 1024 // px) if px < 256 else 4
    big = px * ss
    sc = big / vw
    img = Image.new("RGBA", (big, big), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)

    def box(x, y, w, h, r, color):
        x0, y0, x1, y1 = x * sc, y * sc, (x + w) * sc - 1, (y + h) * sc - 1
        if r > 0:
            d.rounded_rectangle([x0, y0, x1, y1], radius=r * sc, fill=color + (255,))
        else:
            d.rectangle([x0, y0, x1, y1], fill=color + (255,))

    for r in rects:
        if r["stroke"]:  # stroke is centred on the edge: outer shape in the stroke colour, inner shape in the fill
            h = r["sw"] / 2
            box(r["x"] - h, r["y"] - h, r["w"] + r["sw"], r["h"] + r["sw"], r["rx"] + h, r["stroke"])
            if r["fill"]:
                box(r["x"] + h, r["y"] + h, r["w"] - r["sw"], r["h"] - r["sw"], max(r["rx"] - h, 0), r["fill"])
        elif r["fill"]:
            box(r["x"], r["y"], r["w"], r["h"], r["rx"], r["fill"])
    return img.resize((px, px), Image.LANCZOS)


def ico_entry(img):
    """One .ico image: PNG for 256 px, a 32-bit DIB with an AND mask below that (accepted by every resource tool)."""
    w, h = img.size
    if w >= 256:
        b = io.BytesIO()
        img.save(b, "PNG", optimize=False, compress_level=9)
        return b.getvalue()
    px = img.load()
    xor = bytearray()
    for y in range(h - 1, -1, -1):
        for x in range(w):
            r, g, bl, a = px[x, y]
            xor += bytes((bl, g, r, a))
    rowbytes = ((w + 31) // 32) * 4
    andmask = bytes(rowbytes * h)  # all 0: the alpha channel decides
    hdr = struct.pack("<IiiHHIIiiII", 40, w, h * 2, 1, 32, 0, len(xor) + len(andmask), 0, 0, 0, 0)
    return hdr + bytes(xor) + andmask


def write_ico(rects, vw, vh):
    entries = [(s, ico_entry(render_logo(rects, vw, vh, s))) for s in ICO_SIZES]
    out = bytearray(struct.pack("<HHH", 0, 1, len(entries)))
    offset = 6 + 16 * len(entries)
    for s, data in entries:
        out += struct.pack("<BBBBHHII", s % 256, s % 256, 0, 0, 1, 32, len(data), offset)
        offset += len(data)
    for _, data in entries:
        out += data
    os.makedirs(os.path.dirname(ICO_PATH), exist_ok=True)
    with open(ICO_PATH, "wb") as f:
        f.write(out)


def paste_logo(img, rects, vw, vh, size, x, y):
    logo = render_logo(rects, vw, vh, size)
    img.paste(logo, (x, y), logo)


def write_bmp(img, path):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    img.convert("RGB").save(path, "BMP")


def write_banner(rects, vw, vh):
    """493x58. WiX puts the dialog title and description over the left, in black: ink ground there, the logo tile at the
    right, a one-pixel rule under the band (the brand's hairline, no shadow)."""
    w, h = 493, 58
    img = Image.new("RGB", (w, h), INK)
    d = ImageDraw.Draw(img)
    paste_logo(img, rects, vw, vh, 48, w - 48 - 8, 5)
    d.line([(0, h - 1), (w - 1, h - 1)], fill=(0xC8, 0xC8, 0xC6))
    write_bmp(img, BANNER_PATH)


def write_dialog(rects, vw, vh):
    """493x312. Left panel (164 px, WiX's own layout) in terminal black with the logo and a coral edge; the rest ink,
    because WiX draws the title and text from x = 180 px in black."""
    w, h = 493, 312
    img = Image.new("RGB", (w, h), INK)
    d = ImageDraw.Draw(img)
    d.rectangle([0, 0, 163, h - 1], fill=BLACK)
    d.rectangle([164, 0, 165, h - 1], fill=CORAL)
    paste_logo(img, rects, vw, vh, 128, 18, 28)
    # a small pixel row under the logo: three ink cells, one coral, one dim (lit versus dim, as in the brand's meters)
    cells = [INK, INK, INK, CORAL, (0x3A, 0x3A, 0x3C)]
    for i, c in enumerate(cells):
        x0 = 18 + 6 + i * 22
        d.rectangle([x0, 176, x0 + 15, 191], fill=c)

    font_path = os.path.join(ROOT, "windows/src/Ambysto.Lean.App/Assets/Fonts/Geist-Medium.ttf")
    try:
        body = ImageFont.truetype(font_path, 11)
    except OSError:
        body = ImageFont.load_default()

    # The left rail mirrors the Mac shell's shared stage order. WiX scales this
    # bitmap into its dialog, so the labels stay in the artwork instead of
    # depending on platform-specific control fonts.
    stages = [("Introduction", 207), ("Destination Select", 229), ("Installation", 251), ("Summary", 273)]
    for label, y in stages:
        d.ellipse([18, y, 26, y + 8], fill=CORAL if label == "Installation" else (0x66, 0x66, 0x68))
        d.text((34, y - 4), label, fill=INK if label == "Installation" else (0xB0, 0xB0, 0xAE), font=body)

    # The right side is deliberately quiet: WiX overlays the active dialog copy
    # on the left of the content area, while this rail gives the Installation
    # stage a stable home for sibling-app promotion.
    rail_x = 300
    d.rectangle([rail_x, 20, w - 12, h - 20], fill=(0xDE, 0xDE, 0xDC), outline=(0xC0, 0xC0, 0xBE))
    d.rectangle([rail_x + 7, 54, w - 20, 136], fill=(0xE8, 0xE8, 0xE6), outline=(0xC8, 0xC8, 0xC6))
    d.rectangle([rail_x + 7, 142, w - 20, 224], fill=(0xE8, 0xE8, 0xE6), outline=(0xC8, 0xC8, 0xC6))
    # WiX keeps the real opt-in controls as keyboard/click targets. These
    # labels are painted in the artwork because native button chrome sits
    # behind a Dialog bitmap on this classic UI surface.
    for y, label in ((96, "Install Optimer too"), (184, "Install DevBox too")):
        d.rectangle([rail_x + 7, y, w - 20, y + 20], fill=(0xF2, 0xF2, 0xF0), outline=(0xA8, 0xA8, 0xA6))
        d.text((rail_x + 14, y + 4), label, fill=BLACK, font=body)
    write_bmp(img, DIALOG_PATH)


W, H = 960, 660

# DESIGN.md tokens
BG = (0x0B, 0x0B, 0x0C)          # terminal-black #0b0b0c
PANEL = (0x10, 0x10, 0x11)       # panel #101011
PANEL_ALT = (0x16, 0x16, 0x18)   # elevated panel surface
INK = (0xE8, 0xE8, 0xE6)         # off-white #e8e8e6
MUTE = (0xB4, 0xB4, 0xB8)        # readable secondary text #b4b4b8
RULE = (0x22, 0x22, 0x24)        # rule #222224
DIM = (0x3A, 0x3A, 0x3C)         # dim #3a3a3c

APPS = {
    "lean": {
        "name": "Lean",
        "full": "Ambysto Lean",
        "tone": (0xF9, 0x9A, 0x8F),
        "tagline": "Disk Map, Duplicate Finder and Clean App Uninstaller",
        "oneliner": "find large files, duplicates and app leftovers safely",
        "bullets": [
            ("Disk Map", "See where gigabytes went in seconds."),
            ("Large & Old", "Spot forgotten archives and build outputs."),
            ("Exact Duplicates", "Reclaim wasted duplicate copies safely."),
            ("Clean Uninstall", "Remove app leftovers with a receipt."),
            ("Recycle First", "Put back anything from the journal."),
        ],
    },
    "optimer": {
        "name": "Optimer",
        "full": "Ambysto Optimer",
        "tone": (0xB0, 0xAD, 0xFF),
        "tagline": "Live System Monitor and Safe Cache Cleaner",
        "oneliner": "see which apps are using memory, CPU and disk",
        "bullets": [
            ("Live Readout", "See which apps use RAM, CPU & disk."),
            ("Safe Cache Clean", "Reclaim regenerable cache space."),
            ("Recycle First", "Put back anything you clean."),
            ("Quiet Tray Bar", "Zero background bloat or popups."),
            ("Local Only", "No account and no telemetry."),
        ],
    },
    "devbox": {
        "name": "DevBox",
        "full": "Ambysto DevBox",
        "tone": (0x4E, 0xCF, 0xBD),
        "tagline": "See What Your Dev Machine Is Running & Holding",
        "oneliner": "see active ports, containers and coding agents",
        "bullets": [
            ("Active Ports", "See every listening port and process."),
            ("Containers", "Inspect and stop local containers."),
            ("Coding Agents", "Spot runaway CLI and agent processes."),
            ("Safe Stop", "Graceful stop before force-quit."),
            ("Local Only", "Built for vibeworkers, zero telemetry."),
        ],
    },
    "power": {
        "name": "Power",
        "full": "Ambysto Power",
        "tone": (0x9F, 0xC6, 0x75),
        "tagline": "Battery Health, App Energy Readout and Keep-Awake",
        "oneliner": "track battery health, app wattage and thermal load",
        "bullets": [
            ("Battery Health", "Track charge cycles, wattage & wear."),
            ("Energy by App", "Spot which background app drains power."),
            ("Thermal Readout", "Monitor CPU temperature and throttling."),
            ("Keep Awake", "Prevent sleep during long builds or runs."),
            ("Local Only", "Lightweight tray monitor, zero telemetry."),
        ],
    },
    "tune": {
        "name": "Tune",
        "full": "Ambysto Tune",
        "tone": (0xDF, 0xAE, 0x5B),
        "tagline": "Reversible Windows Switches with a Full Undo Journal",
        "oneliner": "toggle Windows Explorer, Taskbar and Startup settings safely",
        "bullets": [
            ("Clean Switches", "Tweak Explorer, Taskbar & Start calmly."),
            ("Every Change Logged", "Full before/after journal on disk."),
            ("1-Click Restore", "Undo any single tweak or restore all."),
            ("Startup Control", "Inspect and disable slow login items."),
            ("Local Only", "No registry guesswork or hidden daemons."),
        ],
    },
    "gate": {
        "name": "Gate",
        "full": "Ambysto Gate",
        "tone": (0xE6, 0x9A, 0xD4),
        "tagline": "Outbound Connection Monitor and Network Guard",
        "oneliner": "see which apps connect where and block unwanted traffic",
        "bullets": [
            ("Live Connections", "See which app connects to which host."),
            ("Prompt on New", "Approve or block unknown outbound traffic."),
            ("Quiet Rules", "Keep trusted developer tools unblocked."),
            ("1-Click Lockdown", "Pause non-essential traffic instantly."),
            ("Local Only", "Inspects locally without cloud proxies."),
        ],
    },
}

SUITE_ORDER = ["lean", "optimer", "devbox", "power", "tune", "gate"]


def get_fonts(font_dir):
    return {
        "mono_reg": os.path.join(font_dir, "JetBrainsMono-Regular.ttf"),
        "mono_med": os.path.join(font_dir, "JetBrainsMono-Medium.ttf"),
        "mono_bold": os.path.join(font_dir, "JetBrainsMono-Bold.ttf"),
        "sans_reg": os.path.join(font_dir, "Geist-Regular.ttf"),
        "sans_med": os.path.join(font_dir, "Geist-Medium.ttf"),
    }


_FONT_CACHE = {}


def fnt(path, size):
    return (path, size)


def fnt_scaled(font_spec, S=1):
    if isinstance(font_spec, tuple):
        path, size = font_spec
        px = int(round(size * S))
    else:
        return font_spec
    key = (path, px)
    if key not in _FONT_CACHE:
        _FONT_CACHE[key] = ImageFont.truetype(path, px)
    return _FONT_CACHE[key]


def load_logo(assets_dir, name, size):
    p = os.path.join(assets_dir, f"brand/apps/ambysto-{name}-logo-v3.png")
    img = Image.open(p).convert("RGBA")
    return img.resize((size, size), Image.LANCZOS)


class SuperDraw:
    """4x supersampled canvas that downsamples with LANCZOS so every curve, checkmark, icon and glyph is razor-smooth."""

    def __init__(self, w, h, bg=BG, S=4):
        self.w = w
        self.h = h
        self.S = S
        self.img = Image.new("RGB", (w * S, h * S), bg)
        self.d = ImageDraw.Draw(self.img)

    def copy(self):
        c = SuperDraw.__new__(SuperDraw)
        c.w, c.h, c.S = self.w, self.h, self.S
        c.img = self.img.copy()
        c.d = ImageDraw.Draw(c.img)
        return c

    def _sc(self, box):
        S = self.S
        return [v * S for v in box]

    def rectangle(self, box, fill=None, outline=None, width=1):
        self.d.rectangle(self._sc(box), fill=fill, outline=outline, width=max(1, int(round(width * self.S))))

    def rounded_rectangle(self, box, radius=0, fill=None, outline=None, width=1):
        self.d.rounded_rectangle(
            self._sc(box),
            radius=radius * self.S,
            fill=fill,
            outline=outline,
            width=max(1, int(round(width * self.S))),
        )

    def ellipse(self, box, fill=None, outline=None, width=1):
        self.d.ellipse(self._sc(box), fill=fill, outline=outline, width=max(1, int(round(width * self.S))))

    def line(self, pts, fill=None, width=1):
        S = self.S
        spts = [(p[0] * S, p[1] * S) for p in pts]
        w_px = max(1, int(round(width * S)))
        for i in range(len(spts) - 1):
            self.d.line([spts[i], spts[i + 1]], fill=fill, width=w_px)
        if len(spts) >= 2 and w_px >= 3:
            r = (w_px - 1) / 2.0
            for px, py in spts:
                self.d.ellipse([px - r, py - r, px + r, py + r], fill=fill)

    def polygon(self, pts, fill=None, outline=None, width=1):
        S = self.S
        spts = [(p[0] * S, p[1] * S) for p in pts]
        if fill is not None:
            self.d.polygon(spts, fill=fill)
        if outline is not None:
            closed = list(pts) + [pts[0]]
            self.line(closed, fill=outline, width=width)

    def text(self, xy, text, fill=None, font=None):
        S = self.S
        f_s = fnt_scaled(font, S)
        self.d.text((xy[0] * S, xy[1] * S), text, fill=fill, font=f_s)

    def textbbox(self, xy, text, font=None):
        S = self.S
        f_s = fnt_scaled(font, S)
        b = self.d.textbbox((xy[0] * S, xy[1] * S), text, font=f_s)
        return (b[0] / S, b[1] / S, b[2] / S, b[3] / S)

    def paste_logo(self, assets_dir, name, size, xy):
        S = self.S
        logo = load_logo(assets_dir, name, int(round(size * S)))
        self.img.paste(logo, (int(round(xy[0] * S)), int(round(xy[1] * S))), logo)

    def finalize(self, crisp_frame=False):
        out = self.img.resize((self.w, self.h), Image.LANCZOS)
        if crisp_frame:
            od = ImageDraw.Draw(out)
            od.rectangle([0, 0, self.w - 1, self.h - 1], outline=RULE, width=1)
            od.line([(1, 48), (self.w - 2, 48)], fill=RULE, width=1)
        return out


def draw_frame(d, fonts, assets_dir, title_text=None, show_lang=False, logo_name="lean"):
    d.rectangle([0, 0, W - 1, H - 1], fill=BG, outline=RULE, width=1)
    d.line([(1, 48), (W - 2, 48)], fill=RULE, width=1)

    if title_text:
        d.paste_logo(assets_dir, logo_name, 22, (20, 13))
        d.text((52, 16), title_text, fill=SUB_INK, font=fnt(fonts["mono_med"], 13))

    if show_lang:
        d.rounded_rectangle([W - 190, 12, W - 96, 36], radius=4, fill=PANEL, outline=CARD_RULE, width=1)
        d.text((W - 178, 16), "English  v", fill=SUB_INK, font=fnt(fonts["mono_med"], 12))

    d.line([(W - 68, 24), (W - 54, 24)], fill=SUB_INK, width=1.5)
    cx, cy = W - 28, 24
    d.line([(cx - 6, cy - 6), (cx + 6, cy + 6)], fill=SUB_INK, width=1.5)
    d.line([(cx + 6, cy - 6), (cx - 6, cy + 6)], fill=SUB_INK, width=1.5)


def draw_primary_button(d, fonts, x, y, w, h, label):
    d.rounded_rectangle([x, y, x + w, y + h], radius=6, fill=INK, outline=INK, width=1)
    font = fnt(fonts["mono_bold"], 15)
    bbox = d.textbbox((0, 0), label, font=font)
    tw, th = bbox[2] - bbox[0], bbox[3] - bbox[1]
    d.text((x + (w - tw) / 2, y + (h - th) / 2 - 2), label, fill=BG, font=font)


def draw_ghost_button(d, fonts, x, y, w, h, label):
    d.rounded_rectangle([x, y, x + w, y + h], radius=6, fill=PANEL, outline=DIM, width=1.2)
    font = fnt(fonts["mono_med"], 15)
    bbox = d.textbbox((0, 0), label, font=font)
    tw, th = bbox[2] - bbox[0], bbox[3] - bbox[1]
    d.text((x + (w - tw) / 2, y + (h - th) / 2 - 2), label, fill=INK, font=font)


def draw_checkbox(d, x, y, checked, tone):
    d.rounded_rectangle([x, y, x + 16, y + 16], radius=3.5, fill=tone if checked else PANEL, outline=tone if checked else DIM, width=1.2)
    if checked:
        d.line([(x + 3.5, y + 8.5), (x + 6.8, y + 12.0), (x + 12.8, y + 4.5)], fill=BG, width=2.2)


def draw_underlined_terms(d, fonts, x, y, prefix="By installing this product, you agree to its ", center_w=None):
    font = fnt(fonts["sans_med"], 13)
    t1, t2, t3, t4, t5 = prefix, "EULA", " & ", "Privacy Policy", "."
    full = t1 + t2 + t3 + t4 + t5
    if center_w is not None:
        bbox = d.textbbox((0, 0), full, font=font)
        x = x + (center_w - (bbox[2] - bbox[0])) / 2

    cur_x = x
    for seg, col, uline in [(t1, SUB_INK, False), (t2, INK, True), (t3, SUB_INK, False), (t4, INK, True), (t5, SUB_INK, False)]:
        d.text((cur_x, y), seg, fill=col, font=font)
        b = d.textbbox((0, 0), seg, font=font)
        sw = b[2] - b[0]
        if uline:
            d.line([(cur_x, y + 17), (cur_x + sw, y + 17)], fill=INK, width=1.2)
        cur_x += sw


SUB_INK = (0xC6, 0xC6, 0xC2)     # bright secondary text #c6c6c2
CARD_MUTE = (0xB4, 0xB4, 0xB8)   # readable muted label #b4b4b8
CARD_RULE = (0x2A, 0x2A, 0x2E)   # crisp card border


def _draw_card_shell(d, fonts, sx, sy, sw, sh, app_full, nav_items, header_title, badge_text, tone):
    d.rounded_rectangle([sx, sy, sx + sw, sy + sh], radius=8, fill=PANEL, outline=CARD_RULE, width=1.2)
    side_w = 118
    d.rounded_rectangle([sx + 1, sy + 1, sx + side_w, sy + sh - 1], radius=8, fill=PANEL_ALT)
    d.rectangle([sx + side_w - 8, sy + 1, sx + side_w, sy + sh - 1], fill=PANEL_ALT)
    d.line([(sx + side_w, sy + 1), (sx + side_w, sy + sh - 1)], fill=CARD_RULE, width=1.2)
    d.line([(sx + 2, sy + 1), (sx + sw - 2, sy + 1)], fill=tone, width=2)

    d.text((sx + 11, sy + 15), app_full, fill=SUB_INK, font=fnt(fonts["sans_med"], 11))
    ny = sy + 46
    for label, active in nav_items:
        if active:
            d.rounded_rectangle([sx + 8, ny - 4, sx + side_w - 8, ny + 22], radius=4, fill=BG, outline=CARD_RULE, width=1)
            d.rectangle([sx + 8, ny + 1, sx + 11, ny + 17], fill=tone)
            d.text((sx + 18, ny), label, fill=INK, font=fnt(fonts["sans_med"], 12))
        else:
            d.text((sx + 18, ny), label, fill=SUB_INK, font=fnt(fonts["sans_med"], 12))
        ny += 32

    mx = sx + side_w + 14
    my = sy + 15
    d.text((mx, my), header_title, fill=SUB_INK, font=fnt(fonts["mono_med"], 11))
    bw = 94
    d.rounded_rectangle([sx + sw - bw - 12, my - 3, sx + sw - 12, my + 17], radius=4, fill=BG, outline=tone, width=1.2)
    bbox = d.textbbox((0, 0), badge_text, font=fnt(fonts["mono_bold"], 10))
    tw = bbox[2] - bbox[0]
    d.text((sx + sw - 12 - (bw + tw) / 2, my), badge_text, fill=tone, font=fnt(fonts["mono_bold"], 10))
    return mx, side_w


def _draw_floating_pill(d, fonts, sx, sy, sh, pill_text, tone):
    fx, fy, fw, fh = sx + 72, sy + sh - 54, 216, 40
    d.rounded_rectangle([fx, fy, fx + fw, fy + fh], radius=8, fill=PANEL_ALT, outline=tone, width=1.2)
    d.ellipse([fx + 12, fy + 10, fx + 32, fy + 30], fill=tone)
    d.line([(fx + 18, fy + 20), (fx + 21, fy + 24), (fx + 27, fy + 16)], fill=BG, width=2.2)
    d.text((fx + 40, fy + 11), pill_text, fill=INK, font=fnt(fonts["mono_bold"], 13))
    d.rounded_rectangle([fx + fw - 44, fy + 12, fx + fw - 12, fy + 28], radius=8, fill=tone)
    d.ellipse([fx + fw - 26, fy + 14, fx + fw - 14, fy + 26], fill=BG)


def draw_ui_card(cd, fonts, app_key, sx, sy, sw, sh):
    """Draw UI card directly onto a 4x SuperDraw canvas `cd`."""
    tone = APPS[app_key]["tone"]

    if app_key == "optimer":
        mx, side_w = _draw_card_shell(
            cd, fonts, sx, sy, sw, sh, "Ambysto Optimer",
            [("Overview", True), ("Processes", False), ("Cache Clean", False), ("Settings", False)],
            "WINDOWS LIVE READOUT", "NORMAL", tone
        )
        metrics = [("MEMORY", "6.08 GB", "76%", 6), ("CPU LOAD", "14.2 %", "8 CORES", 3), ("CACHE", "3.42 GB", "SAFE", 5)]
        tw = (sw - side_w - 26 - 16) // 3
        ty = sy + 42
        for i, (m_label, m_val, m_sub, lit_cnt) in enumerate(metrics):
            tx = mx + i * (tw + 8)
            cd.rounded_rectangle([tx, ty, tx + tw, ty + 84], radius=6, fill=BG, outline=CARD_RULE, width=1)
            cd.text((tx + 8, ty + 8), m_label, fill=SUB_INK, font=fnt(fonts["sans_med"], 11))
            cd.text((tx + 8, ty + 26), m_val, fill=INK, font=fnt(fonts["mono_bold"], 15))
            for c_i in range(8):
                cx = tx + 8 + c_i * 8.5
                cd.rectangle([cx, ty + 51, cx + 6, ty + 59], fill=tone if c_i < lit_cnt else DIM)
            cd.text((tx + 8, ty + 64), m_sub, fill=CARD_MUTE, font=fnt(fonts["mono_med"], 10))
        ly = sy + 136
        cd.rounded_rectangle([mx, ly, sx + sw - 12, sy + sh - 18], radius=6, fill=BG, outline=CARD_RULE, width=1)
        rows = [("chrome.exe", "1.84 GB", "12.4%"), ("code.exe", "1.12 GB", "4.1%"), ("Temp & Cache", "3.42 GB", "CLEAN")]
        ry = ly + 12
        for idx_r, (r_name, r_mem, r_state) in enumerate(rows):
            cd.ellipse([mx + 10, ry + 5, mx + 16, ry + 11], fill=tone)
            cd.text((mx + 22, ry), r_name, fill=INK, font=fnt(fonts["mono_med"], 12))
            cd.text((mx + 136, ry), r_mem, fill=SUB_INK, font=fnt(fonts["mono_med"], 12))
            cd.text((mx + 204, ry), r_state, fill=tone if r_state == "CLEAN" else SUB_INK, font=fnt(fonts["mono_bold"], 11))
            ry += 30
            if idx_r < 2:
                cd.line([(mx + 8, ry - 6), (sx + sw - 20, ry - 6)], fill=CARD_RULE, width=1)
        _draw_floating_pill(cd, fonts, sx, sy, sh, "1-Click Clean", tone)

    elif app_key == "devbox":
        mx, _ = _draw_card_shell(
            cd, fonts, sx, sy, sw, sh, "Ambysto DevBox",
            [("Ports (4)", True), ("Containers", False), ("Agents", False), ("History", False)],
            "ACTIVE PORTS & AGENTS", "4 ACTIVE", tone
        )
        ports = [
            (":3000", "node.exe", "vite dev", "218 MB"),
            (":5432", "postgres.exe", "db-local", "142 MB"),
            (":8080", "docker-proxy", "api-gateway", "394 MB"),
            (":9229", "claude-code", "agent-worker", "164 MB"),
        ]
        ry = sy + 44
        for p_num, p_proc, p_tag, p_mem in ports:
            cd.rounded_rectangle([mx, ry, sx + sw - 12, ry + 44], radius=6, fill=BG, outline=CARD_RULE, width=1)
            cd.text((mx + 10, ry + 12), p_num, fill=tone, font=fnt(fonts["mono_bold"], 14))
            cd.text((mx + 68, ry + 6), p_proc, fill=INK, font=fnt(fonts["mono_bold"], 12))
            cd.text((mx + 68, ry + 23), p_tag, fill=SUB_INK, font=fnt(fonts["sans_med"], 11))
            cd.text((mx + 162, ry + 13), p_mem, fill=SUB_INK, font=fnt(fonts["mono_med"], 11))
            cd.rounded_rectangle([sx + sw - 64, ry + 10, sx + sw - 18, ry + 34], radius=4, fill=PANEL_ALT, outline=SUB_INK, width=1)
            cd.text((sx + sw - 54, ry + 14), "Stop", fill=INK, font=fnt(fonts["sans_med"], 11))
            ry += 50
        _draw_floating_pill(cd, fonts, sx, sy, sh, "Port Guard", tone)

    elif app_key == "lean":
        mx, _ = _draw_card_shell(
            cd, fonts, sx, sy, sw, sh, "Ambysto Lean",
            [("Disk Map", True), ("Large & Old", False), ("Duplicates", False), ("Uninstall", False)],
            "DISK MAP & LEFTOVERS", "28.4 GB", tone
        )
        items = [
            ("node_modules & build", "14.2 GB", "18 folders", 7),
            ("Duplicate Archives", "6.8 GB", "42 copies", 5),
            ("App Leftovers", "4.3 GB", "6 removed apps", 3),
            ("Old Downloads (>90d)", "3.1 GB", "19 files", 2),
        ]
        ry = sy + 44
        for name, sz, sub, bar_n in items:
            cd.rounded_rectangle([mx, ry, sx + sw - 12, ry + 44], radius=6, fill=BG, outline=CARD_RULE, width=1)
            cd.text((mx + 10, ry + 6), name, fill=INK, font=fnt(fonts["sans_med"], 12))
            cd.text((mx + 10, ry + 23), sub, fill=SUB_INK, font=fnt(fonts["sans_med"], 11))
            for c_i in range(8):
                cx = mx + 152 + c_i * 7
                cd.rectangle([cx, ry + 17, cx + 5, ry + 27], fill=tone if c_i < bar_n else DIM)
            cd.text((sx + sw - 74, ry + 13), sz, fill=tone, font=fnt(fonts["mono_bold"], 12))
            ry += 50
        _draw_floating_pill(cd, fonts, sx, sy, sh, "Recycle First", tone)

    elif app_key == "power":
        mx, side_w = _draw_card_shell(
            cd, fonts, sx, sy, sw, sh, "Ambysto Power",
            [("Battery", True), ("Apps Energy", False), ("Thermal", False), ("Keep Awake", False)],
            "BATTERY & ENERGY", "HEALTH 96%", tone
        )
        metrics = [("CHARGE", "80 %", "CAPPED", 6), ("DRAW", "6.4 W", "QUIET", 3), ("THERMAL", "44 °C", "NOMINAL", 4)]
        tw = (sw - side_w - 26 - 16) // 3
        ty = sy + 42
        for i, (m_label, m_val, m_sub, lit_cnt) in enumerate(metrics):
            tx = mx + i * (tw + 8)
            cd.rounded_rectangle([tx, ty, tx + tw, ty + 84], radius=6, fill=BG, outline=CARD_RULE, width=1)
            cd.text((tx + 8, ty + 8), m_label, fill=SUB_INK, font=fnt(fonts["sans_med"], 11))
            cd.text((tx + 8, ty + 26), m_val, fill=INK, font=fnt(fonts["mono_bold"], 15))
            for c_i in range(8):
                cx = tx + 8 + c_i * 8.5
                cd.rectangle([cx, ty + 51, cx + 6, ty + 59], fill=tone if c_i < lit_cnt else DIM)
            cd.text((tx + 8, ty + 64), m_sub, fill=CARD_MUTE, font=fnt(fonts["mono_med"], 10))
        ly = sy + 136
        cd.rounded_rectangle([mx, ly, sx + sw - 12, sy + sh - 18], radius=6, fill=BG, outline=CARD_RULE, width=1)
        rows = [("chrome.exe", "2.8 W", "HIGH"), ("Teams.exe", "1.4 W", "MED"), ("code.exe", "0.7 W", "LOW")]
        ry = ly + 12
        for idx_r, (r_name, r_w, r_state) in enumerate(rows):
            cd.ellipse([mx + 10, ry + 5, mx + 16, ry + 11], fill=tone)
            cd.text((mx + 22, ry), r_name, fill=INK, font=fnt(fonts["mono_med"], 12))
            cd.text((mx + 136, ry), r_w, fill=SUB_INK, font=fnt(fonts["mono_med"], 12))
            cd.text((mx + 204, ry), r_state, fill=tone if r_state == "HIGH" else SUB_INK, font=fnt(fonts["mono_bold"], 11))
            ry += 30
            if idx_r < 2:
                cd.line([(mx + 8, ry - 6), (sx + sw - 20, ry - 6)], fill=CARD_RULE, width=1)
        _draw_floating_pill(cd, fonts, sx, sy, sh, "Keep Awake", tone)

    elif app_key == "tune":
        mx, _ = _draw_card_shell(
            cd, fonts, sx, sy, sw, sh, "Ambysto Tune",
            [("Explorer", True), ("Taskbar", False), ("Startup", False), ("Journal", False)],
            "REVERSIBLE SWITCHES", "JOURNALED", tone
        )
        tweaks = [
            ("Show file extensions", "Explorer · logged", True),
            ("Show hidden & dot files", "Explorer · logged", True),
            ("Disable Start web search", "Privacy · logged", True),
            ("Seconds in taskbar clock", "Taskbar · default", False),
        ]
        ry = sy + 44
        for t_name, t_sub, is_on in tweaks:
            cd.rounded_rectangle([mx, ry, sx + sw - 12, ry + 44], radius=6, fill=BG, outline=CARD_RULE, width=1)
            cd.text((mx + 12, ry + 6), t_name, fill=INK, font=fnt(fonts["sans_med"], 12))
            cd.text((mx + 12, ry + 23), t_sub, fill=SUB_INK, font=fnt(fonts["sans_med"], 11))
            tx = sx + sw - 58
            ty_sw = ry + 13
            cd.rounded_rectangle([tx, ty_sw, tx + 36, ty_sw + 18], radius=9, fill=tone if is_on else DIM)
            cx = tx + 26 if is_on else tx + 10
            cd.ellipse([cx - 6, ty_sw + 3, cx + 6, ty_sw + 15], fill=BG if is_on else INK)
            ry += 50
        _draw_floating_pill(cd, fonts, sx, sy, sh, "1-Click Undo", tone)

    elif app_key == "gate":
        mx, _ = _draw_card_shell(
            cd, fonts, sx, sy, sw, sh, "Ambysto Gate",
            [("Monitor", True), ("Rules", False), ("Prompts", False), ("Lockdown", False)],
            "OUTBOUND CONNECTIONS", "GUARDING", tone
        )
        conns = [
            ("code.exe", "api.anthropic.com:443", "ALLOWED"),
            ("git.exe", "github.com:443", "ALLOWED"),
            ("updater.exe", "telemetry.vendor.io:443", "BLOCKED"),
            ("node.exe", "registry.npmjs.org:443", "ALLOWED"),
        ]
        ry = sy + 44
        for c_proc, c_host, c_state in conns:
            cd.rounded_rectangle([mx, ry, sx + sw - 12, ry + 44], radius=6, fill=BG, outline=CARD_RULE, width=1)
            cd.text((mx + 12, ry + 6), c_proc, fill=INK, font=fnt(fonts["mono_bold"], 12))
            cd.text((mx + 12, ry + 23), c_host, fill=SUB_INK, font=fnt(fonts["mono_med"], 11))
            col = tone if c_state == "BLOCKED" else SUB_INK
            cd.text((sx + sw - 80, ry + 14), c_state, fill=col, font=fnt(fonts["mono_bold"], 11))
            ry += 50
        _draw_floating_pill(cd, fonts, sx, sy, sh, "Prompt Mode", tone)


def render_showcase_image(fonts, assets_dir, sibling_key):
    """Render 884x368 showcase block for sibling_key at 4x supersampling for crisp typography and curves."""
    sw, sh = 884, 368
    d = SuperDraw(sw, sh, BG, S=4)
    info = APPS[sibling_key]
    tone = info["tone"]

    d.text((0, 4), "Ambysto Recommendation (Optional Offer by Ambysto)", fill=SUB_INK, font=fnt(fonts["mono_med"], 12))
    d.paste_logo(assets_dir, sibling_key, 56, (10, 52))

    f_title = fnt(fonts["mono_bold"], 24)
    t_pre = "Ambysto "
    w_pre = d.textbbox((0, 0), t_pre, font=f_title)[2]
    d.text((82, 52), t_pre, fill=INK, font=f_title)
    d.text((82 + w_pre, 52), info["name"], fill=tone, font=f_title)
    d.text((82, 86), info["tagline"], fill=SUB_INK, font=fnt(fonts["sans_med"], 14))

    by = 138
    f_b_bold = fnt(fonts["mono_bold"], 14)
    f_b_reg = fnt(fonts["sans_med"], 14)
    for head, desc in info["bullets"]:
        d.line([(14, by + 9), (18, by + 13), (25, by + 5)], fill=tone, width=2.2)
        d.text((38, by), head, fill=INK, font=f_b_bold)
        hw = d.textbbox((0, 0), head, font=f_b_bold)[2]
        d.text((38 + hw + 6, by), "— " + desc, fill=SUB_INK, font=f_b_reg)
        by += 40

    draw_ui_card(d, fonts, sibling_key, 458, 46, 420, 310)
    return d.finalize()


def render_offer_bar_image(fonts, sibling_key, checked=True):
    """Render 958x122 bottom offer bar for Screen 1 (blitted at x=1, y=536) at 4x supersampling."""
    bw, bh = 958, 122
    d = SuperDraw(bw, bh, PANEL, S=4)
    info = APPS[sibling_key]
    tone = info["tone"]

    d.text((37, 18), "Ambysto Recommendation (Optional Offer by Ambysto)", fill=SUB_INK, font=fnt(fonts["mono_med"], 12))
    draw_checkbox(d, 37, 46, checked=checked, tone=tone)
    d.text((63, 44), f"Install {info['full']} to {info['oneliner']}", fill=INK, font=fnt(fonts["sans_med"], 14))
    draw_underlined_terms(d, fonts, 37, 72, prefix="By installing this product, you agree to its ")

    out = d.finalize()
    od = ImageDraw.Draw(out)
    od.line([(0, 0), (bw - 1, 0)], fill=RULE, width=1)
    return out


def draw_card_icon(d, kind, cx, cy, tone):
    if kind == "book":
        d.rounded_rectangle([cx - 11, cy - 13, cx + 11, cy + 13], radius=2.5, outline=tone, width=2)
        d.line([(cx - 6, cy - 6), (cx + 6, cy - 6)], fill=tone, width=2)
        d.line([(cx - 6, cy), (cx + 6, cy)], fill=tone, width=2)
        d.line([(cx - 6, cy + 6), (cx + 2, cy + 6)], fill=tone, width=2)
    elif kind == "bulb":
        d.ellipse([cx - 9, cy - 12, cx + 9, cy + 6], outline=tone, width=2)
        d.line([(cx - 5, cy + 9), (cx + 5, cy + 9)], fill=tone, width=2)
        d.line([(cx - 3, cy + 13), (cx + 3, cy + 13)], fill=tone, width=2)
    elif kind == "megaphone":
        d.polygon([(cx - 11, cy - 3), (cx + 7, cy - 10), (cx + 7, cy + 8), (cx - 11, cy + 2)], outline=tone, width=2)
        d.line([(cx - 7, cy + 3), (cx - 5, cy + 11)], fill=tone, width=2)
        d.line([(cx + 11, cy - 5), (cx + 14, cy - 7)], fill=tone, width=2)
        d.line([(cx + 11, cy + 2), (cx + 14, cy + 4)], fill=tone, width=2)
    elif kind == "mute":
        d.polygon([(cx - 10, cy - 4), (cx - 4, cy - 4), (cx + 3, cy - 10), (cx + 3, cy + 10), (cx - 4, cy + 4), (cx - 10, cy + 4)], outline=tone, width=2)
        d.line([(cx - 12, cy - 12), (cx + 12, cy + 12)], fill=tone, width=2)


def generate_all_for_app(app_key, font_dir, assets_dir, out_dir):
    os.makedirs(out_dir, exist_ok=True)
    fonts = get_fonts(font_dir)
    info = APPS[app_key]
    tone = info["tone"]
    written = []

    def save_png(im, name):
        p = os.path.join(out_dir, name)
        im.save(p, "PNG", optimize=False, compress_level=9)
        written.append(p)

    # 1. Screen1_Base.png (Welcome without bottom offer bar)
    d1 = SuperDraw(W, H, BG, S=4)
    draw_frame(d1, fonts, assets_dir, title_text=None, show_lang=True, logo_name=app_key)
    d1.paste_logo(assets_dir, app_key, 132, ((W - 132) // 2, 118))
    cells = [INK, INK, INK, tone, DIM]
    start_x = (W - (5 * 14 + 4 * 6)) // 2
    for i, c in enumerate(cells):
        cx = start_x + i * 20
        d1.rectangle([cx, 266, cx + 14, 274], fill=c)
    f_head = fnt(fonts["mono_bold"], 28)
    p1, p2, p3 = "Welcome to Ambysto ", info["name"], " Installer"
    w1 = d1.textbbox((0, 0), p1, font=f_head)[2]
    w2 = d1.textbbox((0, 0), p2, font=f_head)[2]
    w3 = d1.textbbox((0, 0), p3, font=f_head)[2]
    hx, hy = (W - (w1 + w2 + w3)) / 2, 304
    d1.text((hx, hy), p1, fill=INK, font=f_head)
    d1.text((hx + w1, hy), p2, fill=tone, font=f_head)
    d1.text((hx + w1 + w2, hy), p3, fill=INK, font=f_head)
    draw_underlined_terms(d1, fonts, 0, 352, prefix="By installing or using this product, you agree to its ", center_w=W)
    draw_primary_button(d1, fonts, (W - 248) // 2, 402, 248, 52, "Install")
    save_png(d1.finalize(crisp_frame=True), "Screen1_Base.png")

    # 2. Screen2_Base.png (Progress frame with Accept and Install button)
    d2 = SuperDraw(W, H, BG, S=4)
    draw_frame(d2, fonts, assets_dir, title_text=f"{info['full']} Installation Progress", show_lang=False, logo_name=app_key)
    d2.rectangle([1, 92, W - 2, 98], fill=RULE)
    draw_primary_button(d2, fonts, (W - 256) // 2, 536, 256, 48, "Accept and Install")
    draw_underlined_terms(d2, fonts, 0, 602, center_w=W)
    save_png(d2.finalize(crisp_frame=True), "Screen2_Base.png")

    # 2a. Screen2_Accepted.png (Progress frame after clicking Accept and Install)
    d2_acc = d2.copy()
    bx_acc, by_acc, bw_acc, bh_acc = (W - 256) // 2, 536, 256, 48
    d2_acc.rounded_rectangle([bx_acc, by_acc, bx_acc + bw_acc, by_acc + bh_acc], radius=6, fill=PANEL, outline=tone, width=1.5)
    f_acc = fnt(fonts["mono_bold"], 15)
    lbl_acc = "Accepted"
    bb_acc = d2_acc.textbbox((0, 0), lbl_acc, font=f_acc)
    tw_acc, th_acc = bb_acc[2] - bb_acc[0], bb_acc[3] - bb_acc[1]
    tx_acc = bx_acc + (bw_acc - tw_acc) / 2 + 8
    ty_acc = by_acc + (bh_acc - th_acc) / 2 - 2
    d2_acc.line([(tx_acc - 22, by_acc + 24), (tx_acc - 18, by_acc + 28), (tx_acc - 11, by_acc + 20)], fill=tone, width=2.2)
    d2_acc.text((tx_acc, ty_acc), lbl_acc, fill=tone, font=f_acc)
    save_png(d2_acc.finalize(crisp_frame=True), "Screen2_Accepted.png")

    # 2b. Screen2_NoOffer.png (Progress when all sibling apps are already installed)
    d2_no = SuperDraw(W, H, BG, S=4)
    draw_frame(d2_no, fonts, assets_dir, title_text=f"{info['full']} Installation Progress", show_lang=False, logo_name=app_key)
    d2_no.rectangle([1, 92, W - 2, 98], fill=RULE)
    d2_no.paste_logo(assets_dir, app_key, 132, ((W - 132) // 2, 196))
    for i, c in enumerate(cells):
        cx = start_x + i * 20
        d2_no.rectangle([cx, 344, cx + 14, 352], fill=c)
    t_inst = f"Installing {info['full']}..."
    f_inst = fnt(fonts["mono_bold"], 24)
    wi = d2_no.textbbox((0, 0), t_inst, font=f_inst)[2]
    d2_no.text(((W - wi) / 2, 378), t_inst, fill=INK, font=f_inst)
    t_sub = info["tagline"]
    f_sub = fnt(fonts["sans_med"], 15)
    ws = d2_no.textbbox((0, 0), t_sub, font=f_sub)[2]
    d2_no.text(((W - ws) / 2, 418), t_sub, fill=SUB_INK, font=f_sub)
    save_png(d2_no.finalize(crisp_frame=True), "Screen2_NoOffer.png")

    # 3. Screen3_Base.png (Post-install Offer frame with Skip This + Accept and Install)
    d3 = SuperDraw(W, H, BG, S=4)
    draw_frame(d3, fonts, assets_dir, title_text="Installation Complete", show_lang=False, logo_name=app_key)
    gap, bw, bh = 20, 252, 48
    bx1 = (W - (bw * 2 + gap)) // 2
    bx2 = bx1 + bw + gap
    draw_ghost_button(d3, fonts, bx1, 536, bw, bh, "Skip This")
    draw_primary_button(d3, fonts, bx2, 536, bw, bh, "Accept and Install")
    draw_underlined_terms(d3, fonts, 0, 602, center_w=W)
    save_png(d3.finalize(crisp_frame=True), "Screen3_Base.png")

    # 4. Screen4_Newsletter.png
    d4 = SuperDraw(W, H, BG, S=4)
    draw_frame(d4, fonts, assets_dir, title_text="Installation Complete", show_lang=False, logo_name=app_key)
    head = "Subscribe to Ambysto newsletter now to get your privileges:"
    f_h4 = fnt(fonts["mono_bold"], 21)
    hw = d4.textbbox((0, 0), head, font=f_h4)[2]
    d4.text(((W - hw) / 2, 92), head, fill=INK, font=f_h4)
    cards = [
        ("book", "Free Field Guide ($9 valued)", "Get tutorials of Windows & Mac disk,\nmemory and developer machine hygiene"),
        ("bulb", "Tips & Advices", "Get practical solutions for PC performance\nand safe cache maintenance"),
        ("megaphone", "Regular Campaign", "Win Pro suite keys in seasonal drops\nand community benchmarks"),
        ("mute", "No disturbing", "Unsubscribe anytime in one click\nif you are not satisfied"),
    ]
    positions = [(116, 168), (516, 168), (116, 326), (516, 326)]
    cw, ch = 328, 124
    for (cx, cy), (icon_kind, title, body) in zip(positions, cards):
        d4.rounded_rectangle([cx, cy, cx + cw, cy + ch], radius=8, fill=PANEL, outline=CARD_RULE, width=1.2)
        bx, by = cx - 26, cy + (ch - 52) // 2
        d4.ellipse([bx, by, bx + 52, by + 52], fill=PANEL_ALT, outline=tone, width=2)
        draw_card_icon(d4, icon_kind, bx + 26, by + 26, tone)
        d4.text((cx + 42, cy + 20), title, fill=INK, font=fnt(fonts["mono_bold"], 15))
        lines_b = body.split("\n")
        for li, line_str in enumerate(lines_b):
            d4.text((cx + 42, cy + 52 + li * 20), line_str, fill=SUB_INK, font=fnt(fonts["sans_med"], 13))
    ix, iy, iw, ih = 116, 514, 464, 44
    d4.rounded_rectangle([ix, iy, ix + iw, iy + ih], radius=6, fill=PANEL, outline=DIM, width=1.2)
    draw_primary_button(d4, fonts, ix + iw + 14, iy, 122, ih, "Subscribe")
    draw_ghost_button(d4, fonts, ix + iw + 14 + 122 + 12, iy, 122, ih, "No, thanks")
    note = "*Don't forget to check your inbox or spam folder for a confirmation email to complete your subscription."
    d4.text((116, 574), note, fill=SUB_INK, font=fnt(fonts["sans_med"], 13))
    d4.text((116, 600), "Privacy Policy", fill=INK, font=fnt(fonts["sans_med"], 13))
    pw = d4.textbbox((0, 0), "Privacy Policy", font=fnt(fonts["sans_med"], 13))[2]
    d4.line([(116, 618), (116 + pw, 618)], fill=INK, width=1.2)
    save_png(d4.finalize(crisp_frame=True), "Screen4_Newsletter.png")

    # 5. Screen5_Finish.png
    d5 = SuperDraw(W, H, BG, S=4)
    draw_frame(d5, fonts, assets_dir, title_text="Installation Complete", show_lang=False, logo_name=app_key)
    bx, by, bs = (W - 104) // 2, 166, 104
    d5.rounded_rectangle([bx, by, bx + bs, by + bs], radius=18, fill=PANEL, outline=tone, width=2.2)
    d5.rounded_rectangle([bx + 10, by + 10, bx + bs - 10, by + bs - 10], radius=12, fill=PANEL_ALT, outline=CARD_RULE, width=1.2)
    d5.line([(bx + 32, by + 54), (bx + 46, by + 68), (bx + 74, by + 38)], fill=tone, width=5)
    cells_done = [INK, INK, INK, INK, tone]
    for i, c in enumerate(cells_done):
        cx = start_x + i * 20
        d5.rectangle([cx, 288, cx + 14, 296], fill=c)
    f_ty = fnt(fonts["mono_bold"], 30)
    ty_text = "Thank you!"
    tw = d5.textbbox((0, 0), ty_text, font=f_ty)[2]
    d5.text(((W - tw) / 2, 320), ty_text, fill=INK, font=f_ty)
    f_sub5 = fnt(fonts["sans_med"], 15)
    l1 = f"{info['full']} has been installed successfully."
    l2 = "Please click the Finish button below to run it now."
    w1 = d5.textbbox((0, 0), l1, font=f_sub5)[2]
    w2 = d5.textbbox((0, 0), l2, font=f_sub5)[2]
    d5.text(((W - w1) / 2, 372), l1, fill=SUB_INK, font=f_sub5)
    d5.text(((W - w2) / 2, 396), l2, fill=SUB_INK, font=f_sub5)
    cb_label = "Learn more online"
    f_cb = fnt(fonts["sans_med"], 14)
    cw_cb = d5.textbbox((0, 0), cb_label, font=f_cb)[2]
    cb_x = int((W - (16 + 10 + cw_cb)) // 2)
    d5_off = d5.copy()

    draw_checkbox(d5, cb_x, 434, checked=True, tone=tone)
    d5.text((cb_x + 26, 432), cb_label, fill=SUB_INK, font=f_cb)
    draw_primary_button(d5, fonts, (W - 248) // 2, 468, 248, 52, "Finish")
    save_png(d5.finalize(crisp_frame=True), "Screen5_Finish.png")

    draw_checkbox(d5_off, cb_x, 434, checked=False, tone=tone)
    d5_off.text((cb_x + 26, 432), cb_label, fill=SUB_INK, font=f_cb)
    draw_primary_button(d5_off, fonts, (W - 248) // 2, 468, 248, 52, "Finish")
    save_png(d5_off.finalize(crisp_frame=True), "Screen5_Finish_off.png")

    # 6. Sibling OfferBars & Showcases for the 5 other apps
    for sib in SUITE_ORDER:
        if sib == app_key:
            continue
        save_png(render_offer_bar_image(fonts, sib, checked=True), f"OfferBar_{sib}_on.png")
        save_png(render_offer_bar_image(fonts, sib, checked=False), f"OfferBar_{sib}_off.png")
        save_png(render_showcase_image(fonts, assets_dir, sib), f"Showcase_{sib}.png")

    return written


def main():
    base = sys.argv[1] if len(sys.argv) > 1 else os.environ.get(
        "AMBYSTO_ASSETS", os.path.expanduser("~/work/ambysto/ambysto-assets"))
    svg = base if base.endswith(".svg") else os.path.join(base, SVG_NAME)
    if not os.path.exists(svg) and not base.endswith(".svg"):
        alt = os.path.join(base, "brand/apps/ambysto-lean-logo-v3.svg")
        if os.path.exists(alt):
            svg = alt
    assets_dir = os.path.dirname(os.path.dirname(os.path.dirname(svg))) if base.endswith(".svg") else base
    if not os.path.isdir(os.path.join(assets_dir, "brand/apps")):
        assets_dir = os.environ.get("AMBYSTO_ASSETS", os.path.expanduser("~/work/ambysto/ambysto-assets"))
    rects, vw, vh = read_rects(svg)
    write_ico(rects, vw, vh)
    write_banner(rects, vw, vh)
    write_dialog(rects, vw, vh)
    extra = generate_all_for_app(
        "lean",
        os.path.join(ROOT, "windows/src/Ambysto.Lean.App/Assets/Fonts"),
        assets_dir,
        os.path.dirname(DIALOG_PATH),
    )
    for p in [ICO_PATH, BANNER_PATH, DIALOG_PATH] + extra:
        print("%7d  %s" % (os.path.getsize(p), os.path.relpath(p, ROOT)))


if __name__ == "__main__":
    main()
