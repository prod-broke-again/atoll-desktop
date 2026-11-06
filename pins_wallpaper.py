"""
Pinterest Pins → Color-Harmonious Grid Wallpaper Generator
==========================================================
Reads images from ~/Pictures/Pins, analyzes dominant colors,
groups them by color harmony, builds a grid collage, and sets
it as the desktop wallpaper. Designed to run every hour via
Windows Task Scheduler.

Usage:
    python pins_wallpaper.py              # generate & set wallpaper
    python pins_wallpaper.py --preview    # generate but don't set wallpaper
    python pins_wallpaper.py --reindex    # rebuild the color index cache
    python pins_wallpaper.py --style=hex  # force a style: diagonal | bento | hex | classic
"""

import ctypes
import hashlib
import math
import json
import logging
import os
import random
import sys
import time
from collections import defaultdict
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw, ImageFilter, ImageEnhance
from sklearn.cluster import KMeans

# ── Configuration ──────────────────────────────────────────────

PINS_DIR = Path.home() / "Pictures" / "Pins"
OUTPUT_DIR = Path.home() / "Pictures" / "PinsWallpaper"
INDEX_FILE = OUTPUT_DIR / "color_index.json"
HISTORY_FILE = OUTPUT_DIR / "wallpaper_history.json"
WALLPAPER_FILE = OUTPUT_DIR / "current_wallpaper.bmp"

SCREEN_W, SCREEN_H = 1920, 1080

# Grid layout: how many images in the collage
GRID_COLS = 5
GRID_ROWS = 3
TOTAL_CELLS = GRID_COLS * GRID_ROWS  # 15 images per wallpaper

# Color analysis
N_DOMINANT_COLORS = 3       # extract top N colors per image
N_COLOR_GROUPS = 8          # number of color palette groups
HISTORY_LIMIT = 200         # remember last N wallpapers to avoid repeats

# Padding and style (used by the "classic" style)
CELL_PADDING = 4            # px between images
CORNER_RADIUS = 12          # rounded corners on each image
BG_COLOR_AUTO = True        # auto-pick background from palette

# Collage styles. Each run picks one of ENABLED_STYLES at random
# (or the one given with --style=NAME). Available: fan, diagonal, bento, hex, classic
ENABLED_STYLES = ["fan", "diagonal", "bento", "hex"]

# Fan style parameters
FAN_CARDS = 9               # number of cards in the fan
FAN_CARD_W = 340            # card width at 1080p
FAN_CARD_H = 530            # card height at 1080p
FAN_MAX_ROTATION = 18       # degrees at outer edge
FAN_ARC_HEIGHT = 65         # vertical arching offset (px)
FAN_CORNER_RADIUS = 16      # rounded corners for cards
FAN_BORDER_WIDTH = 2        # border thickness
FAN_BORDER_COLOR = (255, 255, 255, 140)  # subtle light border
FAN_SHADOW_COLOR = (0, 0, 0, 150)
FAN_SHADOW_OFFSET = 12
FAN_SHADOW_BLUR = 18
FAN_BG_BLUR = 45            # blur radius for background image
FAN_BG_DARKEN = 0.35        # brightness multiplier for background (0 = black, 1 = unchanged)

# Style parameters (sizes are for a 1080px-high screen and scale automatically)
BENTO_MARGIN, BENTO_GAP, BENTO_RADIUS = 28, 14, 22
BENTO_COLS, BENTO_ROWS = 8, 4
# (col, row, col_span, row_span) for every tile of the bento grid
BENTO_LAYOUT = [
    (0, 0, 3, 2), (3, 0, 2, 1), (5, 0, 1, 2), (6, 0, 2, 2), (3, 1, 2, 1),
    (0, 2, 1, 2), (1, 2, 2, 1), (3, 2, 1, 2), (4, 2, 2, 2), (6, 2, 1, 1),
    (7, 2, 1, 2), (1, 3, 2, 1), (6, 3, 1, 1),
]
HEX_RADIUS = 165            # center-to-vertex, px at 1080p
HEX_GAP = 7
HEX_CORNER = 14
DIAG_STRIPS = 7
DIAG_ANGLE = 12             # degrees of slant
DIAG_GAP = 12
SUPERSAMPLE = 2             # mask anti-aliasing factor

logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s  %(levelname)-7s  %(message)s",
    datefmt="%H:%M:%S",
)
log = logging.getLogger("pins_wallpaper")


# ── Color Analysis ─────────────────────────────────────────────

def get_dominant_colors(img_path: Path, n_colors: int = N_DOMINANT_COLORS) -> list[tuple]:
    """Extract dominant colors from an image using K-Means clustering."""
    try:
        img = Image.open(img_path).convert("RGB")
        # Resize for speed
        img = img.resize((80, 80), Image.LANCZOS)
        pixels = np.array(img).reshape(-1, 3).astype(float)

        kmeans = KMeans(n_clusters=n_colors, n_init=3, max_iter=100, random_state=42)
        kmeans.fit(pixels)

        # Sort by cluster size (most dominant first)
        labels, counts = np.unique(kmeans.labels_, return_counts=True)
        sorted_indices = np.argsort(-counts)

        colors = []
        weights = []
        for idx in sorted_indices:
            color = tuple(int(c) for c in kmeans.cluster_centers_[idx])
            weight = float(counts[idx]) / len(pixels)
            colors.append(color)
            weights.append(weight)

        return list(zip(colors, weights))
    except Exception as e:
        log.warning(f"Failed to analyze {img_path.name}: {e}")
        return []


def rgb_to_hsv(r, g, b):
    """Convert RGB (0-255) to HSV (H: 0-360, S: 0-1, V: 0-1)."""
    r, g, b = r / 255.0, g / 255.0, b / 255.0
    mx, mn = max(r, g, b), min(r, g, b)
    diff = mx - mn

    if diff == 0:
        h = 0
    elif mx == r:
        h = (60 * ((g - b) / diff) + 360) % 360
    elif mx == g:
        h = (60 * ((b - r) / diff) + 120) % 360
    else:
        h = (60 * ((r - g) / diff) + 240) % 360

    s = 0 if mx == 0 else diff / mx
    v = mx
    return h, s, v


def color_distance_hsv(c1, c2):
    """Perceptual color distance in HSV space."""
    h1, s1, v1 = rgb_to_hsv(*c1)
    h2, s2, v2 = rgb_to_hsv(*c2)

    # Hue is circular
    dh = min(abs(h1 - h2), 360 - abs(h1 - h2)) / 180.0
    ds = abs(s1 - s2)
    dv = abs(v1 - v2)

    return (dh * 1.5) ** 2 + ds ** 2 + dv ** 2


# ── Indexing ───────────────────────────────────────────────────

def file_hash(path: Path) -> str:
    """Quick hash based on filename + size + mtime."""
    stat = path.stat()
    key = f"{path.name}:{stat.st_size}:{stat.st_mtime_ns}"
    return hashlib.md5(key.encode()).hexdigest()


def scan_images(directory: Path) -> list[Path]:
    """Find all image files in directory."""
    exts = {".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif"}
    images = []
    for f in directory.iterdir():
        if f.is_file() and f.suffix.lower() in exts:
            images.append(f)
    return sorted(images)


def build_color_index(images: list[Path], force: bool = False) -> dict:
    """
    Build or load cached color index.
    Returns: {filename: {hash, dominant_colors: [((r,g,b), weight), ...], hue_group}}
    """
    index = {}

    # Load existing index
    if INDEX_FILE.exists() and not force:
        try:
            with open(INDEX_FILE, "r", encoding="utf-8") as f:
                index = json.load(f)
            log.info(f"Loaded index with {len(index)} entries")
        except Exception:
            index = {}

    updated = 0
    for img_path in images:
        name = img_path.name
        fhash = file_hash(img_path)

        # Skip if already indexed and unchanged
        if name in index and index[name].get("hash") == fhash:
            continue

        colors = get_dominant_colors(img_path)
        if not colors:
            continue

        # Determine primary hue group
        primary_color = colors[0][0]
        h, s, v = rgb_to_hsv(*primary_color)

        # Classify into color groups
        if s < 0.15 and v > 0.8:
            hue_group = "white"
        elif s < 0.15 and v < 0.3:
            hue_group = "black"
        elif s < 0.15:
            hue_group = "gray"
        elif h < 15 or h >= 345:
            hue_group = "red"
        elif 15 <= h < 45:
            hue_group = "orange"
        elif 45 <= h < 70:
            hue_group = "yellow"
        elif 70 <= h < 165:
            hue_group = "green"
        elif 165 <= h < 195:
            hue_group = "cyan"
        elif 195 <= h < 260:
            hue_group = "blue"
        elif 260 <= h < 290:
            hue_group = "purple"
        else:
            hue_group = "pink"

        index[name] = {
            "hash": fhash,
            "dominant_colors": [list(c) + [w] for c, w in colors],
            "hue_group": hue_group,
            "primary_hsv": [round(h, 1), round(s, 3), round(v, 3)],
        }
        updated += 1

        if updated % 10 == 0:
            log.info(f"Indexed {updated} new images...")

    # Remove entries for deleted files
    current_names = {img.name for img in images}
    removed = [k for k in index if k not in current_names]
    for k in removed:
        del index[k]

    if updated > 0 or removed:
        log.info(f"Index updated: +{updated} new, -{len(removed)} removed, {len(index)} total")
        with open(INDEX_FILE, "w", encoding="utf-8") as f:
            json.dump(index, f, ensure_ascii=False, indent=2)

    return index


# ── Image Selection ────────────────────────────────────────────

def get_history() -> list[list[str]]:
    """Load wallpaper history."""
    if HISTORY_FILE.exists():
        try:
            with open(HISTORY_FILE, "r", encoding="utf-8") as f:
                return json.load(f)
        except Exception:
            pass
    return []


def save_history(history: list[list[str]]):
    """Save wallpaper history."""
    # Keep only last N
    history = history[-HISTORY_LIMIT:]
    with open(HISTORY_FILE, "w", encoding="utf-8") as f:
        json.dump(history, f, ensure_ascii=False)


def select_images_by_color(index: dict, count: int = TOTAL_CELLS) -> list[str]:
    """
    Select a harmonious set of images:
    1. Pick a random color group (weighted by how many images it has)
    2. If not enough images, include adjacent hue groups
    3. Sort selected images by hue for a gradient effect
    4. Avoid recently used combinations
    """
    history = get_history()
    recently_used = set()
    for past in history[-20:]:  # last 20 wallpapers
        recently_used.update(past)

    # Group images by hue
    groups = defaultdict(list)
    for name, info in index.items():
        groups[info["hue_group"]].append(name)

    # Filter out groups that are too small
    viable_groups = {g: imgs for g, imgs in groups.items() if len(imgs) >= 3}

    if not viable_groups:
        log.warning("Not enough images in any color group, using all")
        all_names = list(index.keys())
        random.shuffle(all_names)
        return all_names[:count]

    # Hue adjacency map for expanding selection
    hue_order = ["red", "orange", "yellow", "green", "cyan", "blue", "purple", "pink"]
    neutral_groups = ["black", "gray", "white"]

    # Pick a primary group (weighted by size, but prefer groups with fresh images)
    weights = []
    group_names = list(viable_groups.keys())
    for g in group_names:
        fresh_count = sum(1 for n in viable_groups[g] if n not in recently_used)
        weights.append(max(fresh_count, 1))

    primary_group = random.choices(group_names, weights=weights, k=1)[0]
    log.info(f"Selected primary color group: {primary_group}")

    # Collect candidates: primary group + adjacent groups
    candidates = list(groups.get(primary_group, []))

    if primary_group in hue_order:
        idx = hue_order.index(primary_group)
        adjacent = [
            hue_order[(idx - 1) % len(hue_order)],
            hue_order[(idx + 1) % len(hue_order)],
        ]
        for adj in adjacent:
            candidates.extend(groups.get(adj, []))
    elif primary_group in neutral_groups:
        # For neutrals, add all other neutrals
        for ng in neutral_groups:
            if ng != primary_group:
                candidates.extend(groups.get(ng, []))

    # Prefer fresh images
    fresh = [c for c in candidates if c not in recently_used]
    if len(fresh) >= count:
        candidates = fresh

    # Deduplicate
    candidates = list(dict.fromkeys(candidates))

    if len(candidates) < count:
        # Add more from any group
        all_names = list(index.keys())
        random.shuffle(all_names)
        for name in all_names:
            if name not in candidates:
                candidates.append(name)
            if len(candidates) >= count:
                break

    # Sort by hue for gradient effect
    def sort_key(name):
        info = index.get(name, {})
        hsv = info.get("primary_hsv", [0, 0, 0])
        return (hsv[0], hsv[2], hsv[1])  # hue, then value, then saturation

    candidates = candidates[:count * 2]  # keep some extras
    candidates.sort(key=sort_key)

    # Pick evenly spaced from sorted list for smooth gradient
    if len(candidates) > count:
        step = len(candidates) / count
        selected = [candidates[int(i * step)] for i in range(count)]
    else:
        selected = candidates[:count]

    return selected


# ── Wallpaper Generation ──────────────────────────────────────

def crop_to_fill(img: Image.Image, target_w: int, target_h: int) -> Image.Image:
    """Crop image to fill target dimensions (center crop)."""
    src_ratio = img.width / img.height
    tgt_ratio = target_w / target_h

    if src_ratio > tgt_ratio:
        # Image is wider — crop sides
        new_w = int(img.height * tgt_ratio)
        left = (img.width - new_w) // 2
        img = img.crop((left, 0, left + new_w, img.height))
    else:
        # Image is taller — crop top/bottom
        new_h = int(img.width / tgt_ratio)
        top = (img.height - new_h) // 2
        img = img.crop((0, top, img.width, top + new_h))

    return img.resize((target_w, target_h), Image.LANCZOS)


def compute_average_color(index: dict, names: list[str]) -> tuple:
    """Compute weighted average color of selected images."""
    total_r, total_g, total_b, total_w = 0, 0, 0, 0
    for name in names:
        info = index.get(name, {})
        for color_data in info.get("dominant_colors", []):
            r, g, b, w = color_data
            total_r += r * w
            total_g += g * w
            total_b += b * w
            total_w += w

    if total_w == 0:
        return (30, 30, 30)

    avg = (int(total_r / total_w), int(total_g / total_w), int(total_b / total_w))

    # Darken for background
    h, s, v = rgb_to_hsv(*avg)
    v = max(0.05, v * 0.15)  # very dark version
    s = min(1.0, s * 0.6)

    # Convert back to RGB
    import colorsys
    r, g, b = colorsys.hsv_to_rgb(h / 360.0, s, v)
    return (int(r * 255), int(g * 255), int(b * 255))


# ── Style layouts ─────────────────────────────────────────────
# Every layout returns a list of (x, y, w, h, mask_fn) "slots".
# mask_fn(size, ss) -> "L" mask image of the slot's own size (or None = rectangle).

def _scale() -> float:
    return SCREEN_H / 1080


def _rounded_mask(radius: float):
    def fn(size, ss):
        w, h = size
        m = Image.new("L", (w * ss, h * ss), 0)
        ImageDraw.Draw(m).rounded_rectangle(
            [(0, 0), (w * ss - 1, h * ss - 1)], radius=radius * ss, fill=255)
        return m.resize((w, h), Image.LANCZOS)
    return fn


def layout_classic():
    cw = (SCREEN_W - CELL_PADDING * (GRID_COLS + 1)) // GRID_COLS
    ch = (SCREEN_H - CELL_PADDING * (GRID_ROWS + 1)) // GRID_ROWS
    slots = []
    for i in range(GRID_COLS * GRID_ROWS):
        x = CELL_PADDING + (i % GRID_COLS) * (cw + CELL_PADDING)
        y = CELL_PADDING + (i // GRID_COLS) * (ch + CELL_PADDING)
        slots.append((x, y, cw, ch, _rounded_mask(CORNER_RADIUS)))
    return slots


def layout_bento():
    k = _scale()
    m, g, r = BENTO_MARGIN * k, BENTO_GAP * k, BENTO_RADIUS * k
    uw = (SCREEN_W - 2 * m - (BENTO_COLS - 1) * g) / BENTO_COLS
    uh = (SCREEN_H - 2 * m - (BENTO_ROWS - 1) * g) / BENTO_ROWS
    slots = []
    for col, row, sw, sh in BENTO_LAYOUT:
        x = m + col * (uw + g)
        y = m + row * (uh + g)
        w = sw * uw + (sw - 1) * g
        h = sh * uh + (sh - 1) * g
        slots.append((round(x), round(y), round(w), round(h), _rounded_mask(r)))
    return slots


def layout_hex():
    k = _scale()
    R = HEX_RADIUS * k
    w = math.sqrt(3) * R
    v = 1.5 * R
    Rd = R - HEX_GAP * k
    slots = []
    for row in range(-1, math.ceil(SCREEN_H / v) + 2):
        for col in range(-1, math.ceil(SCREEN_W / w) + 2):
            cx = col * w + (w / 2 if abs(row) % 2 else 0)
            cy = row * v + 40 * k
            # skip hexes completely outside the screen
            if cx + w / 2 < 0 or cx - w / 2 > SCREEN_W or cy + Rd < 0 or cy - Rd > SCREEN_H:
                continue
            bw, bh = round(w), round(2 * Rd)
            slots.append((round(cx - w / 2), round(cy - Rd), bw, bh, _hex_mask(Rd, HEX_CORNER * k)))
    return slots


def _hex_mask(Rd: float, corner: float):
    def fn(size, ss):
        w, h = size
        m = Image.new("L", (w * ss, h * ss), 0)
        cx, cy = w * ss / 2, h * ss / 2
        pts = [(cx + Rd * ss * math.cos(math.radians(60 * i - 90)),
                cy + Rd * ss * math.sin(math.radians(60 * i - 90))) for i in range(6)]
        ImageDraw.Draw(m).polygon(pts, fill=255)
        # blur + threshold = rounded corners
        m = m.filter(ImageFilter.GaussianBlur(corner * ss * 0.55)).point(lambda v: 255 if v > 128 else 0)
        return m.resize((w, h), Image.LANCZOS)
    return fn


def _poly_mask(points):
    def fn(size, ss):
        w, h = size
        m = Image.new("L", (w * ss, h * ss), 0)
        ImageDraw.Draw(m).polygon([(x * ss, y * ss) for x, y in points], fill=255)
        return m.resize((w, h), Image.LANCZOS)
    return fn


def layout_diagonal():
    k = _scale()
    n = DIAG_STRIPS
    sw = SCREEN_W / n
    g = DIAG_GAP * k
    dx = SCREEN_H * math.tan(math.radians(DIAG_ANGLE))
    slots = []
    for i in range(-1, n + 1):
        x0 = i * sw
        bw, bh = round(sw + dx), SCREEN_H
        bx = round(x0)
        # parallelogram in the slot's local coordinates
        local_x0 = x0 - bx
        pts = [(local_x0 + dx + g / 2, 0), (local_x0 + sw + dx - g / 2, 0),
               (local_x0 + sw - g / 2, SCREEN_H), (local_x0 + g / 2, SCREEN_H)]
        slots.append((bx, 0, bw, bh, _poly_mask(pts)))
    return slots


def generate_fan_wallpaper(
    selected_names: list[str],
    index: dict,
    images_dir: Path,
) -> Path:
    """Generate a Fan-style wallpaper: arched cards with shadows and blurred backdrop."""
    k = _scale()
    n_cards = min(FAN_CARDS, len(selected_names)) if selected_names else FAN_CARDS
    if n_cards % 2 == 0:
        n_cards -= 1
    mid_idx = n_cards // 2

    # 1. Background: blurred and darkened central image (or average color)
    center_img_path = images_dir / selected_names[mid_idx % len(selected_names)] if selected_names else None
    if center_img_path and center_img_path.exists():
        try:
            bg = Image.open(center_img_path).convert("RGB")
            bg = crop_to_fill(bg, SCREEN_W, SCREEN_H)
            bg = bg.filter(ImageFilter.GaussianBlur(FAN_BG_BLUR * k))
            bg = ImageEnhance.Brightness(bg).enhance(FAN_BG_DARKEN)
        except Exception:
            bg = None
    else:
        bg = None

    if bg is None:
        avg_color = compute_average_color(index, selected_names)
        bg = Image.new("RGB", (SCREEN_W, SCREEN_H), avg_color)

    wallpaper = bg.convert("RGBA")

    # 2. Render cards
    max_offset = (n_cards - 1) / 2 if n_cards > 1 else 1
    spacing = (SCREEN_W * 0.68) / max(n_cards - 1, 1)

    # Order of rendering: outer cards first (in background), central card LAST (on top of everything)
    # e.g. for 9 cards: 0, 8, 1, 7, 2, 6, 3, 5, 4
    render_order = []
    for step in range(mid_idx, 0, -1):
        left_idx = mid_idx - step
        right_idx = mid_idx + step
        if 0 <= left_idx < n_cards:
            render_order.append(left_idx)
        if 0 <= right_idx < n_cards:
            render_order.append(right_idx)
    render_order.append(mid_idx)  # Center card is drawn LAST so it sits on top!

    for i in render_order:
        offset = i - (n_cards - 1) / 2
        norm_offset = offset / max_offset if max_offset else 0

        # Card scale: center is largest and closest to viewer, sides gently scale down
        scale = 1.06 - abs(norm_offset) * 0.045
        cw = int(FAN_CARD_W * k * scale)
        ch = int(FAN_CARD_H * k * scale)
        radius = int(FAN_CORNER_RADIUS * k * scale)

        # Rotation and arc offset (gentle arc down towards the sides)
        angle = norm_offset * FAN_MAX_ROTATION
        cx = int(SCREEN_W / 2 + offset * spacing)
        cy = int(SCREEN_H / 2 + 10 * k + (abs(norm_offset) ** 1.5) * FAN_ARC_HEIGHT * k)

        name = selected_names[i % len(selected_names)]
        img_path = images_dir / name
        if not img_path.exists():
            continue

        try:
            img = Image.open(img_path).convert("RGB")
            img = crop_to_fill(img, cw, ch).convert("RGBA")

            # Supersampled rounded mask with border
            ss = 2
            card_mask = Image.new("L", (cw * ss, ch * ss), 0)
            draw_mask = ImageDraw.Draw(card_mask)
            draw_mask.rounded_rectangle(
                [(0, 0), (cw * ss - 1, ch * ss - 1)],
                radius=radius * ss,
                fill=255,
            )
            card_mask = card_mask.resize((cw, ch), Image.LANCZOS)
            img.putalpha(card_mask)

            # Draw subtle light border
            border_overlay = Image.new("RGBA", (cw * ss, ch * ss), (0, 0, 0, 0))
            draw_border = ImageDraw.Draw(border_overlay)
            bw = int(FAN_BORDER_WIDTH * ss)
            draw_border.rounded_rectangle(
                [(bw // 2, bw // 2), (cw * ss - 1 - bw // 2, ch * ss - 1 - bw // 2)],
                radius=radius * ss,
                outline=FAN_BORDER_COLOR,
                width=bw,
            )
            border_overlay = border_overlay.resize((cw, ch), Image.LANCZOS)
            img = Image.alpha_composite(img, border_overlay)

            # Drop shadow
            pad = int((FAN_SHADOW_BLUR + FAN_SHADOW_OFFSET) * k * 2)
            shadow_canvas = Image.new("RGBA", (cw + pad * 2, ch + pad * 2), (0, 0, 0, 0))
            shadow_draw = ImageDraw.Draw(shadow_canvas)
            shadow_rect = [
                (pad, pad + int(FAN_SHADOW_OFFSET * k)),
                (pad + cw - 1, pad + int(FAN_SHADOW_OFFSET * k) + ch - 1),
            ]
            shadow_draw.rounded_rectangle(shadow_rect, radius=radius, fill=FAN_SHADOW_COLOR)
            shadow_canvas = shadow_canvas.filter(ImageFilter.GaussianBlur(int(FAN_SHADOW_BLUR * k)))

            # Rotate shadow and card
            rot_shadow = shadow_canvas.rotate(-angle, expand=True, resample=Image.BICUBIC)
            rot_card = img.rotate(-angle, expand=True, resample=Image.BICUBIC)

            # Paste shadow
            sx = int(cx - rot_shadow.width / 2)
            sy = int(cy - rot_shadow.height / 2)
            wallpaper.alpha_composite(rot_shadow, (sx, sy))

            # Paste card
            px = int(cx - rot_card.width / 2)
            py = int(cy - rot_card.height / 2)
            wallpaper.alpha_composite(rot_card, (px, py))

        except Exception as e:
            log.warning(f"Failed to process fan card {name}: {e}")

    result = wallpaper.convert("RGB")
    result.save(WALLPAPER_FILE, "BMP")
    png_preview = OUTPUT_DIR / "current_wallpaper.png"
    result.save(png_preview, "PNG")
    log.info(f"Fan wallpaper saved: {png_preview}")
    return png_preview


LAYOUTS = {
    "classic": layout_classic,
    "bento": layout_bento,
    "hex": layout_hex,
    "diagonal": layout_diagonal,
    "fan": lambda: [None] * FAN_CARDS,
}


def pick_style() -> str:
    """--style=NAME wins, otherwise a random one from ENABLED_STYLES."""
    for arg in sys.argv[1:]:
        if arg.startswith("--style="):
            name = arg.split("=", 1)[1].strip().lower()
            if name in LAYOUTS:
                return name
            log.warning(f"Unknown style '{name}', choosing randomly")
    return random.choice(ENABLED_STYLES)


def generate_wallpaper(
    selected_names: list[str],
    index: dict,
    images_dir: Path,
    slots: list,
) -> Path:
    """Generate a collage wallpaper: fill every slot with a selected image."""
    if BG_COLOR_AUTO:
        bg_color = compute_average_color(index, selected_names)
    else:
        bg_color = (20, 20, 20)

    wallpaper = Image.new("RGB", (SCREEN_W, SCREEN_H), bg_color)

    for i, (x, y, w, h, mask_fn) in enumerate(slots):
        if not selected_names:
            break
        name = selected_names[i % len(selected_names)]
        img_path = images_dir / name
        if not img_path.exists():
            continue

        try:
            img = Image.open(img_path).convert("RGB")
            img = crop_to_fill(img, w, h)
            mask = mask_fn((w, h), SUPERSAMPLE) if mask_fn else None
            wallpaper.paste(img, (x, y), mask)
        except Exception as e:
            log.warning(f"Failed to process {name}: {e}")

    # Save as BMP for compatibility, and PNG for viewing and setting
    wallpaper.save(WALLPAPER_FILE, "BMP")
    png_preview = OUTPUT_DIR / "current_wallpaper.png"
    wallpaper.save(png_preview, "PNG")
    log.info(f"Wallpaper saved: {png_preview}")
    return png_preview


# ── Set Wallpaper ──────────────────────────────────────────────

def set_wallpaper(path: Path):
    """Set the desktop wallpaper on Windows 10/11 silently using native IDesktopWallpaper COM API."""
    resolved_path = str(path.resolve())

    # Method 1: Pure in-process COM API call (no console window, no powershell process, zero gaming interruptions)
    try:
        from ctypes import wintypes, HRESULT, POINTER, c_void_p

        ole32 = ctypes.oledll.ole32
        ole32.CoInitialize(None)

        class GUID(ctypes.Structure):
            _fields_ = [
                ('Data1', wintypes.DWORD),
                ('Data2', wintypes.WORD),
                ('Data3', wintypes.WORD),
                ('Data4', wintypes.BYTE * 8)
            ]

        CLSID_DesktopWallpaper = GUID(
            0xC2CF3110, 0x460E, 0x4FC1,
            (wintypes.BYTE * 8)(0xB9, 0xD0, 0x8A, 0x1C, 0x0C, 0x9C, 0xC4, 0xBD)
        )
        IID_IDesktopWallpaper = GUID(
            0xB92B56A9, 0x8B55, 0x4E14,
            (wintypes.BYTE * 8)(0x9A, 0x89, 0x01, 0x99, 0xBB, 0xB6, 0xF9, 0x3B)
        )

        pWallpaper = c_void_p()
        CLSCTX_LOCAL_SERVER = 4
        CLSCTX_INPROC_SERVER = 1
        hr = ole32.CoCreateInstance(
            ctypes.byref(CLSID_DesktopWallpaper),
            None,
            CLSCTX_LOCAL_SERVER | CLSCTX_INPROC_SERVER,
            ctypes.byref(IID_IDesktopWallpaper),
            ctypes.byref(pWallpaper)
        )

        if hr == 0 and pWallpaper.value:
            vtable = ctypes.cast(pWallpaper, POINTER(POINTER(c_void_p))).contents
            # SetWallpaper is slot 3 (QueryInterface=0, AddRef=1, Release=2, SetWallpaper=3)
            SetWallpaperProto = ctypes.WINFUNCTYPE(HRESULT, c_void_p, wintypes.LPCWSTR, wintypes.LPCWSTR)
            SetWallpaper = SetWallpaperProto(vtable[3])
            hr_set = SetWallpaper(pWallpaper, None, resolved_path)
            if hr_set == 0:
                log.info("Desktop wallpaper updated silently via native IDesktopWallpaper COM API!")
                return
            else:
                log.warning(f"IDesktopWallpaper::SetWallpaper returned hr={hex(hr_set & 0xffffffff)}")
    except Exception as e:
        log.warning(f"Native IDesktopWallpaper COM call failed: {e}")

    # Method 2: Fallback to classic SystemParametersInfoW
    SPI_SETDESKWALLPAPER = 0x0014
    SPIF_UPDATEINIFILE = 0x01
    SPIF_SENDWININICHANGE = 0x02

    result = ctypes.windll.user32.SystemParametersInfoW(
        SPI_SETDESKWALLPAPER,
        0,
        resolved_path,
        SPIF_UPDATEINIFILE | SPIF_SENDWININICHANGE,
    )

    if result:
        log.info("Desktop wallpaper updated via SystemParametersInfoW!")
    else:
        log.error("Failed to set wallpaper")


# ── Main ───────────────────────────────────────────────────────

def main():
    preview_only = "--preview" in sys.argv
    force_reindex = "--reindex" in sys.argv

    OUTPUT_DIR.mkdir(parents=True, exist_ok=True)

    # 1. Scan images
    images = scan_images(PINS_DIR)
    if not images:
        log.error(f"No images found in {PINS_DIR}")
        log.info("Make sure to copy your Pinterest Pins to that folder first!")
        sys.exit(1)

    log.info(f"Found {len(images)} images in {PINS_DIR}")

    # 2. Build/load color index
    index = build_color_index(images, force=force_reindex)

    # 3. Choose a style and select a harmonious image set for it
    style = pick_style()
    slots = LAYOUTS[style]()
    log.info(f"Style: {style} ({len(slots)} tiles)")
    if len(index) < len(slots):
        log.warning(f"Only {len(index)} indexed images for {len(slots)} tiles, some will repeat")
    selected = select_images_by_color(index, len(slots))
    log.info(f"Selected {len(selected)} images for wallpaper")

    # 4. Generate wallpaper
    if style == "fan":
        wallpaper_path = generate_fan_wallpaper(selected, index, PINS_DIR)
    else:
        wallpaper_path = generate_wallpaper(selected, index, PINS_DIR, slots)

    # 5. Update history
    history = get_history()
    history.append(selected)
    save_history(history)

    # 6. Set as wallpaper
    if not preview_only:
        set_wallpaper(wallpaper_path)
    else:
        log.info("Preview mode — wallpaper not applied")
        png_preview = OUTPUT_DIR / "current_wallpaper.png"
        if png_preview.exists():
            os.startfile(png_preview)

    log.info("Done!")


if __name__ == "__main__":
    main()
