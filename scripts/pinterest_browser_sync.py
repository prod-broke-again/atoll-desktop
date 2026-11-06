#!/usr/bin/env python3
"""
Pinterest Browser / Chrome CDP Sync
===================================
Automates scraping pins from Pinterest using your real Chrome browser.
No extra drivers required (uses Chrome DevTools Protocol).

Usage:
  1. Login once:
     python scripts/pinterest_browser_sync.py --login

  2. Auto-sync Home Feed (background, test mode):
     python scripts/pinterest_browser_sync.py --test --limit 20

  3. Auto-sync a specific board:
     python scripts/pinterest_browser_sync.py --url "https://www.pinterest.com/username/boardname/" --test
"""

import argparse
import asyncio
import json
import logging
import os
import re
import subprocess
import sys
import time
import urllib.parse
import urllib.request
from pathlib import Path

import requests
import websockets

if sys.platform == "win32":
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
        sys.stderr.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass

logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s  %(levelname)-7s  %(message)s",
    datefmt="%H:%M:%S",
)
log = logging.getLogger("pinterest_browser_sync")

DEFAULT_PINS_DIR = Path.home() / "Pictures" / "Pins"
TEST_PINS_DIR = Path.home() / "Pictures" / "Pins_Test"
DEFAULT_PROFILE_DIR = Path.home() / ".pinterest_chrome_profile"
CHROME_PATH = r"C:\Program Files\Google\Chrome\Application\chrome.exe"
CDP_PORT = 9224

async def get_websocket_url(port: int, max_retries: int = 30) -> str | None:
    """Finds the page target's WebSocket URL via Chrome CDP endpoint."""
    for _ in range(max_retries):
        try:
            with urllib.request.urlopen(f"http://127.0.0.1:{port}/json/list", timeout=2) as resp:
                targets = json.loads(resp.read().decode())
                # Filter for main web page
                page = next(
                    (t for t in targets if t.get("type") == "page" and not t.get("url", "").startswith("chrome-extension://")),
                    None,
                )
                if page and page.get("webSocketDebuggerUrl"):
                    return page["webSocketDebuggerUrl"]
        except Exception:
            pass
        await asyncio.sleep(0.3)
    return None

class ChromeCDP:
    def __init__(self, ws_url: str):
        self.ws_url = ws_url
        self.ws = None
        self._msg_id = 1
        self._pending_futures = {}

    async def connect(self):
        self.ws = await websockets.connect(
            self.ws_url,
            max_size=30 * 1024 * 1024,
            ping_interval=None,
        )
        asyncio.create_task(self._listen())

    async def _listen(self):
        try:
            async for raw in self.ws:
                data = json.loads(raw)
                msg_id = data.get("id")
                if msg_id and msg_id in self._pending_futures:
                    fut = self._pending_futures.pop(msg_id)
                    if not fut.done():
                        fut.set_result(data)
        except Exception:
            pass

    async def send(self, method: str, params: dict | None = None, timeout: float = 20.0) -> dict:
        msg_id = self._msg_id
        self._msg_id += 1
        loop = asyncio.get_running_loop()
        fut = loop.create_future()
        self._pending_futures[msg_id] = fut

        payload = {"id": msg_id, "method": method, "params": params or {}}
        await self.ws.send(json.dumps(payload))

        resp = await asyncio.wait_for(fut, timeout=timeout)
        if "error" in resp:
            raise RuntimeError(f"CDP {method} error: {resp['error']}")
        return resp.get("result", {})

    async def evaluate(self, expression: str, timeout: float = 15.0):
        res = await self.send(
            "Runtime.evaluate",
            {
                "expression": expression,
                "returnByValue": True,
                "awaitPromise": True,
            },
            timeout=timeout,
        )
        return res.get("result", {}).get("value")

    async def close(self):
        if self.ws:
            await self.ws.close()

def extract_high_res_urls(html_or_urls: list[str] | str) -> set[str]:
    """Extracts unique original image URLs from Pinterest image links."""
    text = html_or_urls if isinstance(html_or_urls, str) else "\n".join(html_or_urls)
    raw_matches = re.findall(
        r"https://i\.pinimg\.com/(?:originals|\d+x)/([0-9a-f/]+\.(?:jpg|png|webp|jpeg))",
        text,
        re.IGNORECASE,
    )
    results = set()
    for rel_path in raw_matches:
        results.add(f"https://i.pinimg.com/originals/{rel_path}")
    return results

def download_image(url: str, dest_dir: Path, session: requests.Session) -> Path | None:
    """Downloads an image URL into dest_dir, falling back to 736x if originals returns 404."""
    filename = Path(urllib.parse.urlparse(url).path).name
    if not filename:
        return None

    dest_path = dest_dir / filename
    if dest_path.exists():
        return None

    try:
        r = session.get(url, timeout=15)
        if r.status_code == 200 and len(r.content) > 1024:
            dest_path.write_bytes(r.content)
            return dest_path

        if "/originals/" in url:
            fallback = url.replace("/originals/", "/736x/")
            r_fb = session.get(fallback, timeout=15)
            if r_fb.status_code == 200 and len(r_fb.content) > 1024:
                dest_path.write_bytes(r_fb.content)
                return dest_path
    except Exception as e:
        log.warning(f"Ошибка загрузки {url}: {e}")

    return None

async def run_login_mode(profile_dir: Path):
    """Opens visible Chrome to let the user log into Pinterest once."""
    profile_dir.mkdir(parents=True, exist_ok=True)
    log.info("Запускаю Google Chrome для авторизации...")
    log.info(f"Профиль будет сохранён в: {profile_dir}")
    print("\n" + "=" * 65)
    print("ИНСТРУКЦИЯ ПО ВХОДУ:")
    print("1. В открывшемся окне Chrome войдите в свой Pinterest аккаунт.")
    print("2. Убедитесь, что открылась ваша лента рекомендаций.")
    print("3. Как только вошли — вернитесь сюда и нажмите ENTER.")
    print("=" * 65 + "\n")

    cmd = [
        CHROME_PATH,
        f"--user-data-dir={profile_dir}",
        "--no-first-run",
        "--disable-extensions",
        "--start-maximized",
        "https://www.pinterest.com/",
    ]
    proc = subprocess.Popen(cmd)
    try:
        input("Нажмите [ENTER] здесь, когда закончите вход в Pinterest...")
    finally:
        log.info("Закрываю окно браузера...")
        proc.terminate()
        try:
            proc.wait(timeout=5)
        except Exception:
            proc.kill()
    print("\n[OK] Авторизация сохранена! Теперь можно запускать скрипт без окна (--test или штатно).")

async def run_sync(
    profile_dir: Path,
    target_url: str,
    dest_dir: Path,
    limit: int,
    headless: bool = True,
    scroll_count: int = 5,
    dry_run: bool = False,
):
    profile_dir.mkdir(parents=True, exist_ok=True)
    dest_dir.mkdir(parents=True, exist_ok=True)

    cmd = [
        CHROME_PATH,
        f"--remote-debugging-port={CDP_PORT}",
        f"--user-data-dir={profile_dir}",
        "--disable-extensions",
        "--disable-gpu",
        "--no-first-run",
        "--window-size=1920,1080",
        "about:blank",
    ]
    if headless:
        cmd.append("--headless=new")

    log.info(f"Запуск Chrome (headless={headless})...")
    proc = subprocess.Popen(cmd)
    cdp = None

    try:
        ws_url = await get_websocket_url(CDP_PORT)
        if not ws_url:
            log.error("Не удалось подключиться к CDP порту Chrome.")
            return

        cdp = ChromeCDP(ws_url)
        await cdp.connect()
        await cdp.send("Page.enable")
        await cdp.send("Runtime.enable")

        log.info(f"Переход на {target_url}...")
        await cdp.send("Page.navigate", {"url": target_url})

        # Wait for initial render
        await asyncio.sleep(4.0)

        collected_originals: set[str] = set()

        log.info(f"Сбор пинов с прокруткой ({scroll_count} шагов)...")
        for step in range(1, scroll_count + 1):
            # Extract HTML content and image sources
            html_snippet = await cdp.evaluate("""
                (() => {
                    const imgs = Array.from(document.querySelectorAll('img')).map(i => i.src + ' ' + (i.srcset || '')).join(' ');
                    const links = Array.from(document.querySelectorAll('a[href*="/pin/"]')).map(a => a.innerHTML).join(' ');
                    return imgs + ' ' + links;
                })()
            """)

            new_found = extract_high_res_urls(html_snippet or "")
            collected_originals.update(new_found)
            log.info(f"Шаг {step}/{scroll_count}: обнаружено картинок: {len(collected_originals)}")

            if len(collected_originals) >= limit:
                break

            # Scroll down
            await cdp.evaluate("window.scrollBy(0, 1600);")
            await asyncio.sleep(1.8)

        if not collected_originals:
            log.warning("Пины не обнаружены на странице. Если требуется авторизация, запустите: python scripts/pinterest_browser_sync.py --login")
            return

        log.info(f"Всего найдено оригиналов: {len(collected_originals)} (лимит: {limit})")

        s = requests.Session()
        s.headers.update({"User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36"})

        new_count = 0
        skipped = 0

        for url in list(collected_originals)[:limit]:
            filename = Path(urllib.parse.urlparse(url).path).name
            target_path = dest_dir / filename

            if target_path.exists():
                skipped += 1
                continue

            if dry_run:
                log.info(f"[DRY-RUN] {url}")
                new_count += 1
                continue

            saved = download_image(url, dest_dir, s)
            if saved:
                new_count += 1
                log.info(f"[{new_count}] Скачано: {filename} ({saved.stat().st_size // 1024} КБ)")
            time.sleep(0.2)

        print("\n" + "=" * 45)
        print("ИТОГИ СИНХРОНИЗАЦИИ:")
        print(f"  Папка назначения: {dest_dir}")
        print(f"  Новых сохранено:  {new_count}")
        print(f"  Уже существовало: {skipped}")
        print("=" * 45 + "\n")

    finally:
        if cdp:
            try:
                await cdp.close()
            except Exception:
                pass
        proc.terminate()
        try:
            proc.wait(timeout=3)
        except Exception:
            proc.kill()

def main():
    parser = argparse.ArgumentParser(description="Синхронизация ленты или досок Pinterest через Chrome CDP")
    parser.add_argument("--login", action="store_true", help="Запустить видимое окно Chrome для разового входа в Pinterest")
    parser.add_argument("--url", default="https://www.pinterest.com/", help="URL ленты или доски для сбора")
    parser.add_argument("--limit", type=int, default=30, help="Максимальное количество пинов за запуск (по умолчанию: 30)")
    parser.add_argument("--scrolls", type=int, default=6, help="Сколько раз проскроллить страницу (по умолчанию: 6)")
    parser.add_argument("--head", action="store_true", help="Показывать окно браузера во время сбора")
    parser.add_argument("--output", type=Path, help="Папка для сохранения картинок (по умолчанию: ~/Pictures/Pins)")
    parser.add_argument("--test", action="store_true", help="Тестовый режим: сохранять в ~/Pictures/Pins_Test")
    parser.add_argument("--dry-run", action="store_true", help="Только показать найденные ссылки, не скачивая")
    parser.add_argument("--profile-dir", type=Path, default=DEFAULT_PROFILE_DIR, help="Папка профиля Chrome")
    args = parser.parse_args()

    if not os.path.exists(CHROME_PATH):
        log.error(f"Google Chrome не найден по пути: {CHROME_PATH}")
        sys.exit(1)

    if args.login:
        asyncio.run(run_login_mode(args.profile_dir))
        return

    if args.output:
        dest_dir = args.output
    elif args.test:
        dest_dir = TEST_PINS_DIR
    else:
        dest_dir = DEFAULT_PINS_DIR

    asyncio.run(run_sync(
        profile_dir=args.profile_dir,
        target_url=args.url,
        dest_dir=dest_dir,
        limit=args.limit,
        headless=not args.head,
        scroll_count=args.scrolls,
        dry_run=args.dry_run,
    ))

if __name__ == "__main__":
    main()
