#!/usr/bin/env python3
"""
Pinterest Cookie / API Sync with SQLite Catalog
==============================================
Fetches pins directly from your Pinterest Home Feed, your boards,
GraphQL explore topics, or search queries using your session cookie,
and downloads high-resolution images (originals) to your Pins directory.

Every downloaded pin is immediately cataloged in SQLite (pins_catalog.sqlite)
with its category, dominant color, and HueGroup.

Usage:
    # 1. Download 20 pins by topic / search into category 'Крутые тачки':
    python scripts/pinterest_cookie_sync.py --search "крутые тачки" --category "Крутые тачки" --limit 20

    # 2. Download from personalized ideas (GraphQL):
    python scripts/pinterest_cookie_sync.py --ideas

    # 3. Download from a specific board:
    python scripts/pinterest_cookie_sync.py --board "Обои для телефона" --limit 25

    # 4. Stream from Home Feed:
    python scripts/pinterest_cookie_sync.py --limit 30 --category "Общее"
"""

import argparse
import json
import logging
import os
import re
import sys
import time
import urllib.parse
from pathlib import Path
from typing import Any

import requests

if sys.platform == "win32":
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
        sys.stderr.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass

# Ensure scripts dir is on sys.path to import pins_db
SCRIPTS_DIR = Path(__file__).resolve().parent
if str(SCRIPTS_DIR) not in sys.path:
    sys.path.insert(0, str(SCRIPTS_DIR))

import pins_db

logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s  %(levelname)-7s  %(message)s",
    datefmt="%H:%M:%S",
)
log = logging.getLogger("pinterest_cookie_sync")

from PIL import Image

DEFAULT_PINS_DIR = Path.home() / "Pictures" / "Pins"
TEST_PINS_DIR = Path.home() / "Pictures" / "Pins_Test"
COOKIE_FILE_PATH = Path("pinterest_cookie.txt")

DEFAULT_ALLOWED_CATEGORIES = [
    "Крутые тачки",
    "Аниме",
    "Эстетика",
    "Япония",
    "Обои фоны",
]

BLOCKED_KEYWORDS = [
    "мем", "meme", "прикол", "юмор", "humor", "аватарк", "цитат",
    "доброе утро", "открытк", "funny", "lol", "joke", "comics", "комикс",
    "рецепт", "маникюр", "одежда", "outfit", "поэзи", "текст", "гороскоп",
    "советы", "лайфхак", "ногти", "прическа", "вязание", "рисунки людей"
]

def is_blocked_text(text: str | None) -> bool:
    """Checks whether any blocked keyword is in the given text."""
    if not text:
        return False
    lower = text.lower()
    return any(b in lower for b in BLOCKED_KEYWORDS)

CHROME_UA = (
    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
    "(KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36"
)

def load_cookie(cli_cookie: str | None, cookie_file: Path) -> str:
    """Finds the session cookie from CLI, file, or environment."""
    if cli_cookie:
        return cli_cookie.strip()

    env_cookie = os.environ.get("PINTEREST_SESS")
    if env_cookie:
        return env_cookie.strip()

    if cookie_file.exists():
        content = cookie_file.read_text(encoding="utf-8").strip()
        if content:
            if "_pinterest_sess=" in content:
                for part in content.split(";"):
                    part = part.strip()
                    if part.startswith("_pinterest_sess="):
                        return part.split("=", 1)[1].strip()
            for line in content.splitlines():
                line = line.strip()
                if line and not line.startswith("#"):
                    return line

    return ""

def init_pinterest_session(sess_cookie: str) -> tuple[requests.Session, str, str]:
    """Initializes requests session with headers, cookie and fetches CSRF token and username."""
    s = requests.Session()
    s.headers.update({
        "User-Agent": CHROME_UA,
        "Accept": "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8",
        "Accept-Language": "en-US,en;q=0.9,ru;q=0.8",
    })

    if sess_cookie:
        s.cookies.set("_pinterest_sess", sess_cookie, domain=".pinterest.com")

    csrf = ""
    username = ""
    try:
        r = s.get("https://www.pinterest.com/", timeout=15)
        csrf = s.cookies.get("csrftoken", "")
        m = re.search(r'"username"\s*:\s*"([^"]+)"', r.text)
        if m:
            username = m.group(1)
    except Exception as e:
        log.warning(f"Could not reach pinterest.com for initial handshake: {e}")

    s.headers.update({
        "X-Requested-With": "XMLHttpRequest",
        "X-CSRFToken": csrf,
        "X-Pinterest-AppState": "active",
        "X-Pinterest-PWS-Handler": "www/[root].js",
        "Accept": "application/json, text/javascript, */*, q=0.01",
        "Referer": "https://www.pinterest.com/",
    })

    return s, csrf, username

def normalize_category(raw_title: str) -> str:
    """Cleans up and normalizes Pinterest topic into a clean category name."""
    if not raw_title:
        return "Общее"
    lower = raw_title.lower()
    if any(k in lower for k in ["тачки", "supra", "gt-r", "car", "авто", "bmw", "porsche", "audi"]):
        return "Крутые тачки"
    if any(k in lower for k in ["аниме", "мику", "хацунэ", "soul eater", "тян", "manga"]):
        return "Аниме"
    if any(k in lower for k in ["япония", "токио", "japan"]):
        return "Япония"
    if any(k in lower for k in ["эстетика", "y2k", "aesthetic", "неон", "вайб"]):
        return "Эстетика"
    if any(k in lower for k in ["обои", "фоны", "wallpaper"]):
        return "Обои фоны"
    return raw_title.strip()

def get_high_res_image_url(pin_dict: dict) -> str | None:
    """Extracts original or highest resolution image URL from pin JSON."""
    images = pin_dict.get("images") or {}
    
    orig = images.get("orig", {}).get("url")
    if orig:
        return orig

    p736 = images.get("736x", {}).get("url")
    if p736:
        return re.sub(r"/\d+x/", "/originals/", p736)

    for k in ["600x315", "564x", "474x", "236x"]:
        if k in images and images[k].get("url"):
            return re.sub(r"/\d+x/", "/originals/", images[k]["url"])

    return None

def download_image(
    url: str,
    dest_dir: Path,
    session: requests.Session,
    min_w: int = 500,
    min_h: int = 600,
) -> Path | None:
    """Downloads an image URL into dest_dir, validating resolution and wallpaper format."""
    filename = Path(urllib.parse.urlparse(url).path).name
    if not filename:
        return None

    dest_path = dest_dir / filename
    if dest_path.exists() and dest_path.stat().st_size > 1024:
        return dest_path

    try:
        content = None
        r = session.get(url, timeout=15)
        if r.status_code == 200 and len(r.content) > 1024:
            content = r.content
        elif "/originals/" in url:
            fallback_url = url.replace("/originals/", "/736x/")
            r_fb = session.get(fallback_url, timeout=15)
            if r_fb.status_code == 200 and len(r_fb.content) > 1024:
                content = r_fb.content

        if not content:
            return None

        dest_path.write_bytes(content)

        # Quality & dimensions check
        try:
            with Image.open(dest_path) as img:
                w, h = img.size
                if w < min_w or h < min_h:
                    log.info(f"Пропуск {filename}: слишком маленькое разрешение ({w}x{h})")
                    dest_path.unlink(missing_ok=True)
                    return None
                if w > h * 1.25 and w < 1920:
                    log.info(f"Пропуск {filename}: неподходящий формат для обоев ({w}x{h})")
                    dest_path.unlink(missing_ok=True)
                    return None
        except Exception:
            pass

        return dest_path
    except Exception as e:
        log.warning(f"Не удалось скачать {url}: {e}")

    return None

def record_pin_to_db(
    db_conn: Any,
    image_path: Path,
    category: str,
    source: str,
    known_hex: str | None = None,
) -> None:
    """Calculates image colors and records/updates it in SQLite."""
    if not db_conn or not image_path.exists():
        return
    try:
        meta = pins_db.analyze_image_file(image_path, known_hex=known_hex)
        pin_data = {
            "id": image_path.stem,
            "filename": image_path.name,
            "category": category,
            "source": source,
            "dominant_color": meta["dominant_color"],
            "hue_group": meta["hue_group"],
            "width": meta["width"],
            "height": meta["height"],
            "is_favorite": 0,
            "is_hidden": 0,
        }
        pins_db.upsert_pin(db_conn, pin_data)
    except Exception as ex:
        log.debug(f"Ошибка каталогизации {image_path.name}: {ex}")

def sync_search_stream(
    session: requests.Session,
    query: str,
    category: str,
    dest_dir: Path,
    limit: int,
    db_conn: Any = None,
    dry_run: bool = False,
) -> tuple[int, int]:
    """Searches Pinterest for `query` and downloads up to `limit` pins into `category`."""
    url = "https://www.pinterest.com/resource/BaseSearchResource/get/"
    bookmarks: list[str] = []
    new_downloads = 0
    skipped_existing = 0
    seen_urls: set[str] = set()

    existing_files = {f.name.lower() for f in dest_dir.glob("*") if f.is_file()}
    if dest_dir != DEFAULT_PINS_DIR and DEFAULT_PINS_DIR.exists():
        existing_files.update(f.name.lower() for f in DEFAULT_PINS_DIR.glob("*") if f.is_file())

    log.info(f"Поиск по запросу: '{query}' | Категория: '{category}' | Цель: {limit} пинов...")

    batch_num = 0
    while new_downloads < limit:
        batch_num += 1
        data_param = {
            "options": {
                "query": query,
                "scope": "pins",
                "bookmarks": bookmarks,
            },
            "context": {}
        }
        try:
            resp = session.get(
                url,
                params={"source_url": f"/search/pins/?q={urllib.parse.quote(query)}", "data": json.dumps(data_param)},
                timeout=15,
            )
            if resp.status_code != 200:
                log.error(f"Ошибка API поиска: {resp.status_code}")
                break

            payload = resp.json()
            res_resp = payload.get("resource_response", {})
            batch = res_resp.get("data", {}).get("results", [])
            if not batch or not isinstance(batch, list):
                log.info("Больше нет пинов по этому запросу.")
                break

            valid_batch = [
                p for p in batch
                if isinstance(p, dict)
                and p.get("type") in ("pin", None)
                and not p.get("is_promoted")
                and not is_blocked_text(p.get("title"))
                and not is_blocked_text(p.get("grid_title"))
                and not is_blocked_text(p.get("description"))
            ]
            log.info(f"Пакет #{batch_num}: получено {len(valid_batch)} пинов")

            for pin in valid_batch:
                img_url = get_high_res_image_url(pin)
                if not img_url or img_url in seen_urls:
                    continue
                seen_urls.add(img_url)

                filename = Path(urllib.parse.urlparse(img_url).path).name
                target_path = dest_dir / filename
                known_color = pin.get("dominant_color")

                if filename.lower() in existing_files:
                    skipped_existing += 1
                    # Ensure existing file is tagged with this category in DB
                    if target_path.exists() and db_conn:
                        record_pin_to_db(db_conn, target_path, category, f"search:{query}", known_color)
                    continue

                if dry_run:
                    new_downloads += 1
                    log.info(f"[DRY-RUN] {new_downloads:02d}/{limit}: {img_url} ({category})")
                    if new_downloads >= limit:
                        break
                    continue

                saved_path = download_image(img_url, dest_dir, session)
                if saved_path:
                    new_downloads += 1
                    existing_files.add(filename.lower())
                    record_pin_to_db(db_conn, saved_path, category, f"search:{query}", known_color)

                    size_kb = saved_path.stat().st_size // 1024
                    log.info(f"[{new_downloads:02d}/{limit}] Скачано: {filename} ({size_kb} КБ) → [{category}]")
                    if new_downloads >= limit:
                        break
                    time.sleep(0.15)

            bookmark = res_resp.get("bookmark")
            if not bookmark or bookmark == "-end-":
                break
            bookmarks = [bookmark]
            time.sleep(0.8)

        except Exception as e:
            log.error(f"Сетевой сбой при поиске: {e}")
            break

    return new_downloads, skipped_existing

def sync_home_feed_stream(
    session: requests.Session,
    dest_dir: Path,
    limit: int,
    category: str = "Общее",
    db_conn: Any = None,
    dry_run: bool = False,
) -> tuple[int, int]:
    """Streams home feed batches and downloads until target count of new images is reached."""
    url = "https://www.pinterest.com/resource/UserHomefeedResource/get/"
    bookmarks: list[str] = []
    new_downloads = 0
    skipped_existing = 0
    seen_urls: set[str] = set()

    existing_files = {f.name.lower() for f in dest_dir.glob("*") if f.is_file()}
    if dest_dir != DEFAULT_PINS_DIR and DEFAULT_PINS_DIR.exists():
        existing_files.update(f.name.lower() for f in DEFAULT_PINS_DIR.glob("*") if f.is_file())

    log.info(f"Запуск синхронизации ленты: цель {limit} уникальных новых изображений...")

    batch_num = 0
    while new_downloads < limit:
        batch_num += 1
        data_param = {
            "options": {
                "bookmarks": bookmarks,
            },
            "context": {}
        }
        try:
            resp = session.get(
                url,
                params={"source_url": "/", "data": json.dumps(data_param)},
                timeout=15,
            )
            if resp.status_code == 401:
                log.error("401 Unauthorized: сессия устарела.")
                break
            if resp.status_code != 200:
                log.error(f"Ошибка API: {resp.status_code}")
                break

            payload = resp.json()
            res_resp = payload.get("resource_response", {})
            batch = res_resp.get("data", [])
            if not batch or not isinstance(batch, list):
                log.info("Больше нет пинов в ленте.")
                break

            valid_batch = [
                p for p in batch
                if isinstance(p, dict)
                and p.get("type") in ("pin", None)
                and not p.get("is_promoted")
                and not is_blocked_text(p.get("title"))
                and not is_blocked_text(p.get("grid_title"))
                and not is_blocked_text(p.get("description"))
            ]
            log.info(f"Пакет #{batch_num}: получено {len(valid_batch)} пинов из ленты")

            for pin in valid_batch:
                img_url = get_high_res_image_url(pin)
                if not img_url or img_url in seen_urls:
                    continue
                seen_urls.add(img_url)

                filename = Path(urllib.parse.urlparse(img_url).path).name
                target_path = dest_dir / filename
                known_color = pin.get("dominant_color")

                if filename.lower() in existing_files:
                    skipped_existing += 1
                    if target_path.exists() and db_conn:
                        record_pin_to_db(db_conn, target_path, category, "feed", known_color)
                    continue

                if dry_run:
                    new_downloads += 1
                    log.info(f"[DRY-RUN] {new_downloads:02d}/{limit}: {img_url}")
                    if new_downloads >= limit:
                        break
                    continue

                saved_path = download_image(img_url, dest_dir, session)
                if saved_path:
                    new_downloads += 1
                    existing_files.add(filename.lower())
                    record_pin_to_db(db_conn, saved_path, category, "feed", known_color)

                    size_kb = saved_path.stat().st_size // 1024
                    log.info(f"[{new_downloads:02d}/{limit}] Скачано: {filename} ({size_kb} КБ)")
                    if new_downloads >= limit:
                        break
                    time.sleep(0.15)

            bookmark = res_resp.get("bookmark")
            if not bookmark or bookmark == "-end-":
                break
            bookmarks = [bookmark]
            time.sleep(0.8)

        except Exception as e:
            log.error(f"Сетевой сбой при обработке ленты: {e}")
            break

    return new_downloads, skipped_existing

def fetch_graphql_ideas(s: requests.Session, csrf: str) -> list[dict]:
    """Fetches personalized explore / ideas topics via Pinterest's GraphQL endpoint."""
    url = "https://ru.pinterest.com/_/graphql/"
    headers = {
        "accept": "application/json",
        "content-type": "application/json",
        "x-csrftoken": csrf,
        "x-pinterest-appstate": "background",
        "x-pinterest-graphql-name": "SearchPageSearchTabHandlerQuery",
        "x-pinterest-pws-handler": "www/index.js",
        "x-pinterest-source-url": "/",
        "x-requested-with": "XMLHttpRequest",
        "referer": "https://ru.pinterest.com/",
    }
    body = {
        "queryHash": "a0959c548d14cf06adbc04230c8151f3aec27082f8328131fbf4ac52af844041",
        "variables": {}
    }
    log.info("Запрашиваю персонализированные идеи и тренды через GraphQL...")
    try:
        resp = s.post(url, headers=headers, json=body, timeout=15)
        if resp.status_code != 200:
            log.error(f"GraphQL вернул ошибку {resp.status_code}")
            return []
        
        edges = resp.json().get("data", {}).get("v3SearchTabHandlerQuery", {}).get("data", {}).get("connection", {}).get("edges", [])
        pins = []
        for edge in edges:
            story = edge.get("node", {})
            story_title = story.get("title", {}).get("format", "Идеи")
            if is_blocked_text(story_title):
                log.info(f"Пропуск темы (стоп-слово): '{story_title}'")
                continue

            for obj in story.get("objects", []):
                topic = obj.get("title", {}).get("format", "")
                if is_blocked_text(topic):
                    log.info(f"Пропуск темы (стоп-слово): '{topic}'")
                    continue

                dominant_colors = obj.get("dominantColors", [])
                cover_imgs = obj.get("coverImage474x") or obj.get("coverImage236x") or obj.get("coverImages") or []
                img_url = cover_imgs[0].get("url") if cover_imgs else None
                if img_url:
                    clean_cat = normalize_category(topic if topic else story_title)
                    # Strict whitelist check: only allow desired categories!
                    if clean_cat not in DEFAULT_ALLOWED_CATEGORIES:
                        log.info(f"Пропуск темы вне белого списка: '{clean_cat}' (Pinterest тема: '{topic or story_title}')")
                        continue

                    pins.append({
                        "title": f"{story_title}: {topic}",
                        "category": clean_cat,
                        "source": f"graphql:{topic or story_title}",
                        "dominant_color": dominant_colors[0] if dominant_colors else None,
                        "images": {
                            "orig": {"url": re.sub(r"/\d+x/", "/originals/", img_url)}
                        }
                    })
        log.info(f"Получено {len(pins)} тематических картинок из GraphQL.")
        return pins
    except Exception as e:
        log.error(f"Ошибка при запросе GraphQL: {e}")
        return []

def fetch_user_boards(s: requests.Session, username: str = "") -> list[dict]:
    """Fetches all boards of the authenticated user."""
    url = "https://www.pinterest.com/resource/BoardsResource/get/"
    options = {}
    if username:
        options["username"] = username
    source_url = f"/{username}/_saved/" if username else "/"
    try:
        resp = s.get(url, params={"source_url": source_url, "data": json.dumps({"options": options, "context": {}})}, timeout=15)
        if resp.status_code == 200:
            return resp.json().get("resource_response", {}).get("data", []) or []
    except Exception as e:
        log.error(f"Ошибка при получении досок: {e}")
    return []

def fetch_board_feed(s: requests.Session, board_id: str, board_url: str, board_name: str, max_pins: int = 50) -> list[dict]:
    """Fetches pins from a specific board using BoardFeedResource."""
    url = "https://www.pinterest.com/resource/BoardFeedResource/get/"
    pins: list[dict] = []
    bookmarks: list[str] = []

    log.info(f"Запрашиваю пины доски '{board_name}' (ID: {board_id}, лимит: {max_pins})...")

    while len(pins) < max_pins:
        data_param = {
            "options": {
                "board_id": board_id,
                "bookmarks": bookmarks,
            },
            "context": {}
        }
        try:
            resp = s.get(
                url,
                params={"source_url": board_url, "data": json.dumps(data_param)},
                timeout=15,
            )
            if resp.status_code != 200:
                log.error(f"Ошибка получения доски: {resp.status_code}")
                break
            payload = resp.json()
            res_resp = payload.get("resource_response", {})
            batch = res_resp.get("data", [])
            if not batch or not isinstance(batch, list):
                break
            valid = [p for p in batch if isinstance(p, dict) and p.get("type") in ("pin", None)]
            for p in valid:
                p["category"] = board_name
                p["source"] = f"board:{board_name}"
            pins.extend(valid)
            log.info(f"Получено +{len(valid)} пинов с доски (всего: {len(pins)})")
            bookmark = res_resp.get("bookmark")
            if not bookmark or bookmark == "-end-":
                break
            bookmarks = [bookmark]
            time.sleep(1.0)
        except Exception as e:
            log.error(f"Ошибка: {e}")
            break

    return pins[:max_pins]

def main():
    parser = argparse.ArgumentParser(description="Синхронизация пинов Pinterest с локальным SQLite каталогом")
    parser.add_argument("--cookie", "-c", help="Значение куки _pinterest_sess")
    parser.add_argument("--cookie-file", type=Path, default=COOKIE_FILE_PATH, help="Файл с кукой (по умолчанию: pinterest_cookie.txt)")
    parser.add_argument("--list-boards", action="store_true", help="Показать список всех ваших досок в Pinterest")
    parser.add_argument("--ideas", action="store_true", help="Скачать тематические подборки («Идеи для вас» / тренды) через GraphQL")
    parser.add_argument("--board", help="Имя или ID доски для скачивания (вместо общей ленты)")
    parser.add_argument("--search", "-q", help="Поисковый запрос для скачивания пинов (например, 'крутые тачки', 'аниме')")
    parser.add_argument("--category", help="Принудительно задать категорию для скачиваемых пинов")
    parser.add_argument("--limit", type=int, default=30, help="Максимальное количество пинов за запуск (по умолчанию: 30)")
    parser.add_argument("--output", type=Path, help="Папка для сохранения картинок (по умолчанию: ~/Pictures/Pins)")
    parser.add_argument("--db", type=Path, help="Путь к файлу базы данных SQLite (по умолчанию: [Папка]/pins_catalog.sqlite)")
    parser.add_argument("--test", action="store_true", help="Тестовый режим: сохранять в ~/Pictures/Pins_Test")
    parser.add_argument("--dry-run", action="store_true", help="Только показать найденные ссылки, не скачивая")
    args = parser.parse_args()

    # Determine target directory
    if args.output:
        dest_dir = args.output
    elif args.test:
        dest_dir = TEST_PINS_DIR
    else:
        dest_dir = DEFAULT_PINS_DIR

    dest_dir.mkdir(parents=True, exist_ok=True)
    db_path = args.db or (dest_dir / "pins_catalog.sqlite")
    db_conn = pins_db.get_connection(db_path)

    cookie = load_cookie(args.cookie, args.cookie_file)
    if not cookie:
        print("\n" + "=" * 65)
        print("ВНИМАНИЕ: Кука _pinterest_sess не найдена!")
        print("=" * 65)
        print("Вставь значение куки в файл: " + str(args.cookie_file.resolve()))
        print("=" * 65 + "\n")
        sys.exit(1)

    log.info(f"Инициализация сессии Pinterest...")
    session, csrf, username = init_pinterest_session(cookie)

    # Mode: list boards
    if args.list_boards:
        boards = fetch_user_boards(session, username)
        print("\n" + "=" * 50)
        print(f"ВАШИ ДОСКИ В PINTEREST (всего: {len(boards)}):")
        print("=" * 50)
        for b in boards:
            print(f"- {b.get('name')} (пинов: {b.get('pin_count', 0)}, ID: {b.get('id')})")
            print(f"  URL: https://www.pinterest.com{b.get('url')}")
        print("=" * 50 + "\n")
        return

    # Mode 1: Search query
    if args.search:
        target_cat = args.category or normalize_category(args.search)
        new_downloads, skipped_existing = sync_search_stream(
            session=session,
            query=args.search,
            category=target_cat,
            dest_dir=dest_dir,
            limit=args.limit,
            db_conn=db_conn,
            dry_run=args.dry_run,
        )
        print("\n" + "-" * 40)
        print(f"Итог синхронизации поиска:")
        print(f"  Категория:        {target_cat}")
        print(f"  Новых скачано:    {new_downloads}")
        print(f"  Уже существовало: {skipped_existing}")
        print("-" * 40 + "\n")
        return

    # Mode 2: GraphQL ideas vs board download vs home feed
    if args.ideas:
        pins = fetch_graphql_ideas(session, csrf)
    elif args.board:
        boards = fetch_user_boards(session, username)
        target_board = None
        for b in boards:
            if args.board.lower() in b.get("name", "").lower() or args.board == str(b.get("id")):
                target_board = b
                break
        
        if not target_board:
            log.error(f"Доска с именем или ID '{args.board}' не найдена! Проверьте список через --list-boards")
            sys.exit(1)

        board_cat = args.category or target_board["name"]
        pins = fetch_board_feed(session, board_id=target_board["id"], board_url=target_board["url"], board_name=board_cat, max_pins=args.limit)
    else:
        target_cat = args.category or "Общее"
        new_downloads, skipped_existing = sync_home_feed_stream(
            session=session,
            dest_dir=dest_dir,
            limit=args.limit,
            category=target_cat,
            db_conn=db_conn,
            dry_run=args.dry_run,
        )
        print("\n" + "-" * 40)
        print(f"Итог синхронизации:")
        print(f"  Папка назначения: {dest_dir}")
        print(f"  Новых скачано:    {new_downloads}")
        print(f"  Уже существовало: {skipped_existing}")
        print("-" * 40 + "\n")
        return

    if not pins:
        log.warning("Не удалось получить пины. Проверьте актуальность куки.")
        sys.exit(1)

    log.info(f"Найдено {len(pins)} пинов для обработки.")

    new_downloads = 0
    skipped_existing = 0

    for idx, pin in enumerate(pins, 1):
        img_url = get_high_res_image_url(pin)
        if not img_url:
            continue

        filename = Path(urllib.parse.urlparse(img_url).path).name
        target_file = dest_dir / filename
        pin_cat = args.category or pin.get("category") or "Общее"
        source = pin.get("source") or "sync"
        known_color = pin.get("dominant_color")

        if target_file.exists():
            skipped_existing += 1
            record_pin_to_db(db_conn, target_file, pin_cat, source, known_color)
            continue

        if args.dry_run:
            log.info(f"[DRY-RUN] {idx:02d}: {img_url} ({pin_cat})")
            new_downloads += 1
            continue

        saved_path = download_image(img_url, dest_dir, session)
        if saved_path:
            new_downloads += 1
            record_pin_to_db(db_conn, saved_path, pin_cat, source, known_color)
            size_kb = saved_path.stat().st_size // 1024
            log.info(f"[{new_downloads}] Скачано: {filename} ({size_kb} КБ) → [{pin_cat}]")
        
        time.sleep(0.2)

    print("\n" + "-" * 40)
    print(f"Итог синхронизации:")
    print(f"  Папка назначения: {dest_dir}")
    print(f"  Новых скачано:    {new_downloads}")
    print(f"  Уже существовало: {skipped_existing}")
    print("-" * 40 + "\n")

if __name__ == "__main__":
    main()
