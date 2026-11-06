#!/usr/bin/env python3
"""
Pins SQLite Catalog & Color Classifier
======================================
Manages local SQLite database for wallpaper pins with:
- Category tagging
- Fast dominant color extraction
- HueGroup classification identical to C# LiveWallpaper enum
"""

import argparse
import colorsys
import sqlite3
import sys
from pathlib import Path
from typing import Any

from PIL import Image

if sys.platform == "win32":
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
        sys.stderr.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass

DEFAULT_PINS_DIR = Path.home() / "Pictures" / "Pins"
DEFAULT_DB_PATH = DEFAULT_PINS_DIR / "pins_catalog.sqlite"

SCHEMA_SQL = """
CREATE TABLE IF NOT EXISTS pins (
    id TEXT PRIMARY KEY,
    filename TEXT NOT NULL UNIQUE,
    category TEXT NOT NULL,
    source TEXT,
    dominant_color TEXT,
    hue_group TEXT,
    width INTEGER DEFAULT 0,
    height INTEGER DEFAULT 0,
    is_favorite INTEGER DEFAULT 0,
    is_hidden INTEGER DEFAULT 0,
    added_at DATETIME DEFAULT CURRENT_TIMESTAMP
);

CREATE INDEX IF NOT EXISTS idx_pins_cat ON pins(category, is_hidden);
CREATE INDEX IF NOT EXISTS idx_pins_hue ON pins(hue_group, is_hidden);
CREATE INDEX IF NOT EXISTS idx_pins_fav ON pins(is_favorite, is_hidden);
"""

def get_connection(db_path: Path = DEFAULT_DB_PATH) -> sqlite3.Connection:
    """Connects to SQLite database and ensures schema exists."""
    db_path.parent.mkdir(parents=True, exist_ok=True)
    conn = sqlite3.connect(str(db_path))
    conn.row_factory = sqlite3.Row
    with conn:
        conn.executescript(SCHEMA_SQL)
    return conn

def hex_to_rgb(hex_code: str) -> tuple[int, int, int]:
    """Converts #RRGGBB or RRGGBB string to (r, g, b)."""
    s = hex_code.strip().lstrip("#")
    if len(s) == 3:
        s = "".join([c * 2 for c in s])
    if len(s) != 6:
        return (40, 40, 45)
    return (int(s[0:2], 16), int(s[2:4], 16), int(s[4:6], 16))

def rgb_to_hex(r: int, g: int, b: int) -> str:
    """Converts (r, g, b) to #rrggbb."""
    return f"#{r:02x}{g:02x}{b:02x}"

def classify_hue(r: int, g: int, b: int) -> str:
    """
    Classifies RGB into HueGroup matching C# LiveWallpaper.Core.Collage.HueClassifier:
    NeutralDark, NeutralLight, NeutralMuted, Red, Orange, Yellow, Green, Cyan, Blue, Purple, Magenta.
    """
    h, s, v = colorsys.rgb_to_hsv(r / 255.0, g / 255.0, b / 255.0)
    h_deg = h * 360.0

    if v < 0.22:
        return "NeutralDark"
    if s < 0.16 and v >= 0.75:
        return "NeutralLight"
    if s < 0.18:
        return "NeutralMuted"

    if h_deg >= 345.0 or h_deg < 15.0:
        return "Red"
    elif 15.0 <= h_deg < 45.0:
        return "Orange"
    elif 45.0 <= h_deg < 70.0:
        return "Yellow"
    elif 70.0 <= h_deg < 165.0:
        return "Green"
    elif 165.0 <= h_deg < 200.0:
        return "Cyan"
    elif 200.0 <= h_deg < 260.0:
        return "Blue"
    elif 260.0 <= h_deg < 305.0:
        return "Purple"
    else:
        return "Magenta"

def analyze_image_file(path: Path, known_hex: str | None = None) -> dict[str, Any]:
    """
    Extracts dimensions, dominant color, and HueGroup.
    If known_hex is provided (e.g. from Pinterest JSON), uses it,
    otherwise calculates it via PIL palette quantization.
    """
    w, h = 0, 0
    dom_rgb = (40, 40, 45)

    try:
        with Image.open(path) as img:
            w, h = img.size
            if known_hex and len(known_hex.strip().lstrip("#")) in (3, 6):
                dom_rgb = hex_to_rgb(known_hex)
            else:
                rgb_img = img.convert("RGB")
                small = rgb_img.resize((48, 48), Image.Resampling.BOX)
                q = small.quantize(colors=4)
                palette = q.getpalette()[:12]
                color_counts = q.getcolors()
                if color_counts and palette:
                    dom_idx = max(color_counts, key=lambda x: x[0])[1]
                    dom_rgb = (
                        palette[dom_idx * 3],
                        palette[dom_idx * 3 + 1],
                        palette[dom_idx * 3 + 2],
                    )
    except Exception as e:
        if known_hex:
            dom_rgb = hex_to_rgb(known_hex)

    hex_color = rgb_to_hex(*dom_rgb)
    hue_group = classify_hue(*dom_rgb)

    return {
        "dominant_color": hex_color,
        "hue_group": hue_group,
        "width": w,
        "height": h,
    }

def upsert_pin(conn: sqlite3.Connection, pin: dict[str, Any]) -> None:
    """Inserts or updates a pin record in the database."""
    sql = """
    INSERT INTO pins (
        id, filename, category, source, dominant_color, hue_group, width, height, is_favorite, is_hidden
    ) VALUES (
        :id, :filename, :category, :source, :dominant_color, :hue_group, :width, :height,
        COALESCE(:is_favorite, 0), COALESCE(:is_hidden, 0)
    )
    ON CONFLICT(id) DO UPDATE SET
        filename = excluded.filename,
        category = CASE WHEN pins.category IS NOT NULL AND pins.category != 'Общее' THEN pins.category ELSE excluded.category END,
        source = COALESCE(excluded.source, pins.source),
        dominant_color = COALESCE(excluded.dominant_color, pins.dominant_color),
        hue_group = COALESCE(excluded.hue_group, pins.hue_group),
        width = CASE WHEN excluded.width > 0 THEN excluded.width ELSE pins.width END,
        height = CASE WHEN excluded.height > 0 THEN excluded.height ELSE pins.height END;
    """
    with conn:
        conn.execute(sql, pin)

def upsert_pins_batch(conn: sqlite3.Connection, pins: list[dict[str, Any]]) -> int:
    """Bulk inserts/updates a list of pins in a single transaction."""
    if not pins:
        return 0
    sql = """
    INSERT INTO pins (
        id, filename, category, source, dominant_color, hue_group, width, height, is_favorite, is_hidden
    ) VALUES (
        :id, :filename, :category, :source, :dominant_color, :hue_group, :width, :height,
        COALESCE(:is_favorite, 0), COALESCE(:is_hidden, 0)
    )
    ON CONFLICT(id) DO UPDATE SET
        filename = excluded.filename,
        category = CASE WHEN pins.category IS NOT NULL AND pins.category != 'Общее' THEN pins.category ELSE excluded.category END,
        source = COALESCE(excluded.source, pins.source),
        dominant_color = COALESCE(excluded.dominant_color, pins.dominant_color),
        hue_group = COALESCE(excluded.hue_group, pins.hue_group),
        width = CASE WHEN excluded.width > 0 THEN excluded.width ELSE pins.width END,
        height = CASE WHEN excluded.height > 0 THEN excluded.height ELSE pins.height END;
    """
    with conn:
        conn.executemany(sql, pins)
    return len(pins)

def get_categories_stats(conn: sqlite3.Connection) -> list[dict[str, Any]]:
    """Returns list of categories with total count, visible count, and favorites."""
    sql = """
    SELECT 
        category, 
        COUNT(*) as total_count,
        SUM(CASE WHEN is_hidden = 0 THEN 1 ELSE 0 END) as visible_count,
        SUM(is_favorite) as favorites_count
    FROM pins
    GROUP BY category
    ORDER BY total_count DESC;
    """
    cur = conn.execute(sql)
    return [dict(r) for r in cur.fetchall()]

def get_hue_groups_stats(conn: sqlite3.Connection) -> list[dict[str, Any]]:
    """Returns count of pins per HueGroup."""
    sql = """
    SELECT 
        hue_group, 
        COUNT(*) as count
    FROM pins
    WHERE is_hidden = 0
    GROUP BY hue_group
    ORDER BY count DESC;
    """
    cur = conn.execute(sql)
    return [dict(r) for r in cur.fetchall()]

def index_existing_directory(
    pins_dir: Path,
    db_path: Path = DEFAULT_DB_PATH,
    default_category: str = "Общее",
    reindex_all: bool = False,
) -> tuple[int, int]:
    """
    Indexes all local images in pins_dir into SQLite database.
    Skips images already in DB unless reindex_all is True.
    """
    conn = get_connection(db_path)
    supported_exts = {".jpg", ".jpeg", ".png", ".webp"}

    image_files = [
        f for f in pins_dir.glob("*.*")
        if f.suffix.lower() in supported_exts and not f.name.startswith(".trashed")
    ]

    # Check which IDs already exist in the database
    cur = conn.execute("SELECT id FROM pins;")
    existing_ids = {row["id"] for row in cur.fetchall()}

    pins_to_upsert: list[dict[str, Any]] = []
    skipped = 0

    print(f"Сканирование папки {pins_dir} (найдено {len(image_files)} файлов)...")
    for idx, f in enumerate(image_files, 1):
        pin_id = f.stem
        if pin_id in existing_ids and not reindex_all:
            skipped += 1
            continue

        meta = analyze_image_file(f)
        pin_data = {
            "id": pin_id,
            "filename": f.name,
            "category": default_category,
            "source": "local_existing",
            "dominant_color": meta["dominant_color"],
            "hue_group": meta["hue_group"],
            "width": meta["width"],
            "height": meta["height"],
            "is_favorite": 0,
            "is_hidden": 0,
        }
        pins_to_upsert.append(pin_data)

        if idx % 50 == 0 or idx == len(image_files):
            print(f"Обработано {idx}/{len(image_files)} файлов...")

    if pins_to_upsert:
        upsert_pins_batch(conn, pins_to_upsert)

    return len(pins_to_upsert), skipped

def main():
    parser = argparse.ArgumentParser(description="Управление каталогом пинов в SQLite")
    parser.add_argument("--index", action="store_true", help="Проиндексировать существующие локальные картинки в базу данных")
    parser.add_argument("--dir", type=Path, default=DEFAULT_PINS_DIR, help="Путь к папке с пинами")
    parser.add_argument("--db", type=Path, default=DEFAULT_DB_PATH, help="Путь к файлу базы данных SQLite")
    parser.add_argument("--category", default="Общее", help="Категория по умолчанию для существующих файлов")
    parser.add_argument("--reindex", action="store_true", help="Пересчитать цвета и обновить все файлы в базе")
    parser.add_argument("--stats", action="store_true", help="Показать статистику базы данных")
    args = parser.parse_args()

    if args.index:
        added, skipped = index_existing_directory(
            pins_dir=args.dir,
            db_path=args.db,
            default_category=args.category,
            reindex_all=args.reindex,
        )
        print("\n" + "=" * 50)
        print(f"Индексация завершена успешно:")
        print(f"  База данных:  {args.db}")
        print(f"  Добавлено:    {added}")
        print(f"  Пропущено:    {skipped}")
        print("=" * 50 + "\n")

    conn = get_connection(args.db)
    cur = conn.execute("SELECT COUNT(*) as count FROM pins;")
    total_count = cur.fetchone()["count"]

    if args.stats or args.index:
        cat_stats = get_categories_stats(conn)
        hue_stats = get_hue_groups_stats(conn)

        print("\n" + "-" * 50)
        print(f"СТАТИСТИКА КАТАЛОГА PINS (Всего: {total_count})")
        print("-" * 50)
        print("Категории:")
        for c in cat_stats:
            print(f"  • {c['category']}: {c['total_count']} (видимых: {c['visible_count']}, избранных: {c['favorites_count']})")
        print("\nЦветовые группы (HueGroup):")
        for h in hue_stats:
            print(f"  • {h['hue_group']}: {h['count']}")
        print("-" * 50 + "\n")

if __name__ == "__main__":
    main()
