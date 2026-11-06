#!/usr/bin/env python3
"""
Pins Visual Cleaner & Triage Tool
=================================
Launches a fast, local interactive web gallery in your browser to review,
filter, and one-click trash or hide unwanted images from ~/Pictures/Pins/.

Usage:
    python scripts/pins_cleaner.py
"""

import http.server
import json
import os
import shutil
import socketserver
import sqlite3
import sys
import urllib.parse
import webbrowser
from pathlib import Path

if sys.platform == "win32":
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
        sys.stderr.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass

PINS_DIR = Path.home() / "Pictures" / "Pins"
TRASH_DIR = PINS_DIR / ".trashed"
DB_PATH = PINS_DIR / "pins_catalog.sqlite"
PORT = 5055

BLOCKED_CATEGORY_NAMES = {
    "веселые мемы", "мемы", "доброе утро стильные картинки",
    "аватарки в тг", "рисунки людей", "рисунки с персонажами"
}

HTML_PAGE = """<!DOCTYPE html>
<html lang="ru">
<head>
  <meta charset="UTF-8">
  <title>LiveWallpaper — Чистка галереи пинов</title>
  <meta name="viewport" content="width=device-width, initial-scale=1.0">
  <style>
    :root {
      --bg: #0f1115;
      --card-bg: #181b21;
      --card-border: #262a34;
      --accent: #8b5cf6;
      --accent-hover: #7c3aed;
      --danger: #ef4444;
      --danger-hover: #dc2626;
      --text: #f3f4f6;
      --text-muted: #9ca3af;
    }
    * { box-sizing: border-box; margin: 0; padding: 0; font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif; }
    body { background: var(--bg); color: var(--text); padding-bottom: 80px; }
    header {
      position: sticky; top: 0; z-index: 50;
      background: rgba(15, 17, 21, 0.92); backdrop-filter: blur(12px);
      border-bottom: 1px solid var(--card-border);
      padding: 14px 24px;
      display: flex; align-items: center; justify-content: space-between; flex-wrap: wrap; gap: 12px;
    }
    .brand { display: flex; align-items: center; gap: 10px; font-weight: 700; font-size: 1.15rem; }
    .brand span { color: var(--accent); }
    .actions { display: flex; align-items: center; gap: 10px; }
    button {
      background: var(--card-bg); border: 1px solid var(--card-border); color: var(--text);
      padding: 8px 16px; border-radius: 8px; font-size: 0.88rem; font-weight: 600; cursor: pointer;
      display: inline-flex; align-items: center; gap: 6px; transition: all 0.15s ease;
    }
    button:hover { background: #262a34; border-color: #3b4252; }
    button.btn-danger { background: var(--danger); border-color: var(--danger); color: #fff; }
    button.btn-danger:hover { background: var(--danger-hover); }
    button.btn-danger:disabled { opacity: 0.4; cursor: not-allowed; }
    button.btn-accent { background: var(--accent); border-color: var(--accent); color: #fff; }
    button.btn-accent:hover { background: var(--accent-hover); }

    .filters-bar {
      padding: 12px 24px; display: flex; gap: 8px; overflow-x: auto;
      border-bottom: 1px solid var(--card-border); background: #13161c;
    }
    .filter-chip {
      background: var(--card-bg); border: 1px solid var(--card-border); color: var(--text-muted);
      padding: 6px 14px; border-radius: 999px; font-size: 0.82rem; font-weight: 600; cursor: pointer;
      white-space: nowrap; transition: all 0.15s ease;
    }
    .filter-chip:hover { color: var(--text); background: #232731; }
    .filter-chip.active { background: var(--accent); border-color: var(--accent); color: #fff; }
    .filter-chip.danger-chip { border-color: #ef444455; color: #fca5a5; }
    .filter-chip.danger-chip.active { background: var(--danger); border-color: var(--danger); color: #fff; }

    .info-bar { padding: 12px 24px; font-size: 0.85rem; color: var(--text-muted); display: flex; justify-content: space-between; }

    .gallery {
      display: grid;
      grid-template-columns: repeat(auto-fill, minmax(180px, 1fr));
      gap: 14px;
      padding: 16px 24px;
    }
    .pin-card {
      position: relative; background: var(--card-bg); border: 2px solid var(--card-border);
      border-radius: 12px; overflow: hidden; cursor: pointer; transition: transform 0.15s ease, border-color 0.15s ease;
      aspect-ratio: 9 / 16;
      display: flex; flex-direction: column;
    }
    .pin-card:hover { transform: translateY(-3px); border-color: #4c566a; }
    .pin-card.selected {
      border-color: var(--danger);
      box-shadow: 0 0 0 3px rgba(239, 68, 68, 0.4);
    }
    .pin-card.selected::after {
      content: "✕ Удалить";
      position: absolute; top: 10px; right: 10px;
      background: var(--danger); color: #fff;
      font-size: 0.75rem; font-weight: 700; padding: 4px 8px; border-radius: 6px;
      box-shadow: 0 2px 6px rgba(0,0,0,0.5);
    }
    .pin-img-wrap { flex: 1; width: 100%; height: 100%; overflow: hidden; background: #0b0c0e; display: flex; align-items: center; justify-content: center; }
    .pin-card img { width: 100%; height: 100%; object-fit: cover; display: block; transition: opacity 0.2s ease; }
    .pin-meta {
      position: absolute; bottom: 0; left: 0; right: 0;
      background: linear-gradient(to top, rgba(10,12,16,0.95), rgba(10,12,16,0.6) 70%, transparent);
      padding: 24px 8px 8px 8px; font-size: 0.72rem; color: #e5e7eb;
      display: flex; flex-direction: column; gap: 2px; pointer-events: none;
    }
    .pin-tag { font-weight: 700; color: #a78bfa; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .pin-dims { color: var(--text-muted); font-size: 0.68rem; }
    .badge-suspicious {
      position: absolute; top: 8px; left: 8px;
      background: #ef4444ee; color: #fff; font-size: 0.68rem; font-weight: 700;
      padding: 3px 6px; border-radius: 4px;
    }

    .bottom-bar {
      position: fixed; bottom: 0; left: 0; right: 0;
      background: rgba(15, 17, 21, 0.95); backdrop-filter: blur(12px);
      border-top: 1px solid var(--card-border);
      padding: 12px 24px; display: flex; align-items: center; justify-content: space-between;
      z-index: 50;
    }
    .count-indicator { font-size: 0.9rem; font-weight: 600; }
    .count-indicator span { color: var(--danger); font-size: 1.05rem; }
  </style>
</head>
<body>
  <header>
    <div class="brand">
      🖼️ <span>LiveWallpaper</span> — Быстрая чистка пинов
    </div>
    <div class="actions">
      <button onclick="autoSelectSuspicious()">⚠️ Выбрать очевидный мусор (<span id="suspCount">0</span>)</button>
      <button onclick="selectAllVisible()">Выбрать все в текущей вкладке</button>
      <button onclick="clearSelection()">Снять выделение</button>
    </div>
  </header>

  <div class="filters-bar" id="filtersBar"></div>

  <div class="info-bar">
    <div id="statsInfo">Загрузка пинов...</div>
    <div>Клик по картинке — пометить на удаление. Подсказка: клавиша <kbd>Space</kbd> или <kbd>Delete</kbd> над карточкой.</div>
  </div>

  <main class="gallery" id="gallery"></main>

  <div class="bottom-bar">
    <div class="count-indicator">
      Выбрано для удаления: <span id="selectedCount">0</span> шт.
    </div>
    <button class="btn-danger" id="deleteBtn" disabled onclick="trashSelected()">
      🗑️ Удалить выбранные в корзину (.trashed)
    </button>
  </div>

  <script>
    let allPins = [];
    let selectedIds = new Set();
    let currentFilter = 'ALL';

    async function loadPins() {
      const res = await fetch('/api/pins');
      allPins = await res.json();
      renderFilters();
      renderGallery();
    }

    function isSuspicious(p) {
      const cat = (p.category || '').toLowerCase();
      const suspiciousCats = ['веселые мемы', 'доброе утро стильные картинки', 'аватарки в тг', 'рисунки людей', 'рисунки с персонажами'];
      if (suspiciousCats.includes(cat)) return true;
      if (p.width > 0 && p.width < 500) return true;
      if (p.height > 0 && p.height < 500) return true;
      return false;
    }

    function renderFilters() {
      const bar = document.getElementById('filtersBar');
      const catCounts = {};
      let suspCount = 0;

      for (const p of allPins) {
        const cat = p.category || 'Общее';
        catCounts[cat] = (catCounts[cat] || 0) + 1;
        if (isSuspicious(p)) suspCount++;
      }
      document.getElementById('suspCount').textContent = suspCount;

      let html = `<button class="filter-chip ${currentFilter === 'ALL' ? 'active' : ''}" onclick="setFilter('ALL')">Все (${allPins.length})</button>`;
      if (suspCount > 0) {
        html += `<button class="filter-chip danger-chip ${currentFilter === 'SUSPICIOUS' ? 'active' : ''}" onclick="setFilter('SUSPICIOUS')">⚠️ Мусор / Мелкие (${suspCount})</button>`;
      }

      const sortedCats = Object.entries(catCounts).sort((a,b) => b[1] - a[1]);
      for (const [cat, cnt] of sortedCats) {
        html += `<button class="filter-chip ${currentFilter === cat ? 'active' : ''}" onclick="setFilter('${cat}')">${cat} (${cnt})</button>`;
      }
      bar.innerHTML = html;
    }

    function setFilter(cat) {
      currentFilter = cat;
      renderFilters();
      renderGallery();
    }

    function getFilteredPins() {
      if (currentFilter === 'ALL') return allPins;
      if (currentFilter === 'SUSPICIOUS') return allPins.filter(isSuspicious);
      return allPins.filter(p => p.category === currentFilter);
    }

    function renderGallery() {
      const gallery = document.getElementById('gallery');
      const pins = getFilteredPins();

      document.getElementById('statsInfo').textContent = `Показано: ${pins.length} из ${allPins.length} пинов`;

      gallery.innerHTML = pins.map(p => {
        const isSel = selectedIds.has(p.id);
        const susp = isSuspicious(p);
        return `
          <div class="pin-card ${isSel ? 'selected' : ''}" data-id="${p.id}" onclick="toggleSelect('${p.id}')">
            ${susp ? '<div class="badge-suspicious">Мусор / Мелкое</div>' : ''}
            <div class="pin-img-wrap">
              <img src="/image/${encodeURIComponent(p.filename)}" loading="lazy" alt="${p.filename}">
            </div>
            <div class="pin-meta">
              <div class="pin-tag">${p.category || 'Общее'}</div>
              <div class="pin-dims">${p.width} × ${p.height} • ${p.hue_group || ''}</div>
            </div>
          </div>
        `;
      }).join('');

      updateSelectedUI();
    }

    function toggleSelect(id) {
      if (selectedIds.has(id)) {
        selectedIds.delete(id);
      } else {
        selectedIds.add(id);
      }
      const el = document.querySelector(`.pin-card[data-id="${id}"]`);
      if (el) el.classList.toggle('selected', selectedIds.has(id));
      updateSelectedUI();
    }

    function autoSelectSuspicious() {
      for (const p of allPins) {
        if (isSuspicious(p)) {
          selectedIds.add(p.id);
        }
      }
      renderGallery();
    }

    function selectAllVisible() {
      const visible = getFilteredPins();
      for (const p of visible) {
        selectedIds.add(p.id);
      }
      renderGallery();
    }

    function clearSelection() {
      selectedIds.clear();
      renderGallery();
    }

    function updateSelectedUI() {
      const count = selectedIds.size;
      document.getElementById('selectedCount').textContent = count;
      const btn = document.getElementById('deleteBtn');
      btn.disabled = count === 0;
      btn.textContent = count === 0 
        ? '🗑️ Удалить выбранные в корзину (.trashed)' 
        : `🗑️ Переместить ${count} пинов в корзину (.trashed)`;
    }

    async function trashSelected() {
      if (selectedIds.size === 0) return;
      if (!confirm(`Переместить ${selectedIds.size} выбранных пинов в корзину (.trashed)? Они сразу исчезнут из обоев.`)) {
        return;
      }

      const res = await fetch('/api/trash', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: json = JSON.stringify({ ids: Array.from(selectedIds) })
      });

      const data = await res.json();
      alert(`Успешно перемещено в корзину: ${data.trashed} файлов.`);
      selectedIds.clear();
      await loadPins();
    }

    // Keyboard navigation
    window.addEventListener('keydown', (e) => {
      if (e.key === 'Delete' || e.code === 'Space') {
        const hovered = document.querySelector('.pin-card:hover');
        if (hovered) {
          e.preventDefault();
          const id = hovered.getAttribute('data-id');
          if (id) toggleSelect(id);
        }
      }
    });

    loadPins();
  </script>
</body>
</html>
"""

class CleanerHTTPHandler(http.server.SimpleHTTPRequestHandler):
    def do_GET(self):
        parsed = urllib.parse.urlparse(self.path)
        if parsed.path in ("/", "/index.html"):
            self.send_response(200)
            self.send_header("Content-Type", "text/html; charset=utf-8")
            self.end_headers()
            self.wfile.write(HTML_PAGE.encode("utf-8"))
            return

        if parsed.path == "/api/pins":
            conn = sqlite3.connect(DB_PATH)
            conn.row_factory = sqlite3.Row
            rows = conn.execute("""
                SELECT id, filename, category, source, dominant_color, hue_group, width, height, is_favorite, is_hidden 
                FROM pins 
                WHERE is_hidden = 0
                ORDER BY added_at DESC
            """).fetchall()
            data = [dict(r) for r in rows]
            conn.close()

            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.end_headers()
            self.wfile.write(json.dumps(data, ensure_ascii=False).encode("utf-8"))
            return

        if parsed.path.startswith("/image/"):
            filename = urllib.parse.unquote(parsed.path[7:])
            filepath = PINS_DIR / filename
            if filepath.exists() and filepath.is_file():
                ext = filepath.suffix.lower()
                mime = "image/jpeg"
                if ext == ".png": mime = "image/png"
                elif ext == ".webp": mime = "image/webp"

                self.send_response(200)
                self.send_header("Content-Type", mime)
                self.send_header("Cache-Control", "public, max-age=86400")
                self.end_headers()
                with open(filepath, "rb") as f:
                    shutil.copyfileobj(f, self.wfile)
                return
            else:
                self.send_error(404, "Image Not Found")
                return

        self.send_error(404)

    def do_POST(self):
        if self.path == "/api/trash":
            content_length = int(self.headers.get("Content-Length", 0))
            body = self.rfile.read(content_length).decode("utf-8")
            data = json.loads(body)
            target_ids = data.get("ids", [])

            TRASH_DIR.mkdir(parents=True, exist_ok=True)
            conn = sqlite3.connect(DB_PATH)
            conn.row_factory = sqlite3.Row

            trashed_count = 0
            with conn:
                for pin_id in target_ids:
                    row = conn.execute("SELECT filename FROM pins WHERE id = ?", (pin_id,)).fetchone()
                    if row:
                        fn = row["filename"]
                        src_file = PINS_DIR / fn
                        dest_file = TRASH_DIR / fn

                        if src_file.exists():
                            try:
                                shutil.move(str(src_file), str(dest_file))
                            except Exception as ex:
                                print(f"Не удалось переместить {fn}: {ex}")

                        conn.execute("UPDATE pins SET is_hidden = 1 WHERE id = ?", (pin_id,))
                        trashed_count += 1

            conn.close()

            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.end_headers()
            self.wfile.write(json.dumps({"success": True, "trashed": trashed_count}).encode("utf-8"))
            return

        self.send_error(404)

    def log_message(self, format, *args):
        # Silence standard HTTP logs for clean terminal
        pass

def auto_trash_obvious() -> int:
    """Moves obvious meme categories and tiny/distorted images into .trashed."""
    TRASH_DIR.mkdir(parents=True, exist_ok=True)
    conn = sqlite3.connect(DB_PATH)
    conn.row_factory = sqlite3.Row

    cur = conn.execute("""
        SELECT id, filename, category, width, height 
        FROM pins 
        WHERE is_hidden = 0
    """)
    rows = cur.fetchall()
    trashed = 0

    with conn:
        for r in rows:
            cat_clean = (r["category"] or "").strip().lower()
            w = r["width"] or 0
            h = r["height"] or 0

            is_meme_cat = cat_clean in BLOCKED_CATEGORY_NAMES
            is_too_small = (w > 0 and w < 500) or (h > 0 and h < 500)

            if is_meme_cat or is_too_small:
                fn = r["filename"]
                src_file = PINS_DIR / fn
                dest_file = TRASH_DIR / fn

                if src_file.exists():
                    try:
                        shutil.move(str(src_file), str(dest_file))
                    except Exception as ex:
                        print(f"Ошибка перемещения {fn}: {ex}")

                conn.execute("UPDATE pins SET is_hidden = 1 WHERE id = ?", (r["id"],))
                trashed += 1
                print(f"  [Мусор перемещен в .trashed] {fn} (кат: {r['category']}, {w}x{h})")

    conn.close()
    return trashed

def main():
    if not PINS_DIR.exists():
        print(f"Ошибка: папка {PINS_DIR} не существует!")
        sys.exit(1)

    import argparse
    parser = argparse.ArgumentParser(description="Интерактивная чистка галереи пинов")
    parser.add_argument("--auto", action="store_true", help="Автоматически переместить очевидный мусор (мемы, мелкие аватарки) в корзину .trashed")
    args = parser.parse_args()

    if args.auto:
        print("Сканирование на очевидный мусор...")
        count = auto_trash_obvious()
        print(f"Готово. Перемещено в корзину: {count} файлов.")
        return

    url = f"http://127.0.0.1:{PORT}/"
    print("=" * 60)
    print("🎨 LiveWallpaper — Интерактивный чистильщик пинов")
    print("=" * 60)
    print(f"Сервер запущен на: {url}")
    print(f"Папка пинов:       {PINS_DIR}")
    print(f"Корзина (.trashed): {TRASH_DIR}")
    print("=" * 60)
    print("Открываю галерею в вашем браузере...")
    print("Нажмите Ctrl+C в этом окне для завершения работы.")
    print("=" * 60)

    webbrowser.open(url)

    socketserver.TCPServer.allow_reuse_address = True
    with socketserver.TCPServer(("127.0.0.1", PORT), CleanerHTTPHandler) as httpd:
        try:
            httpd.serve_forever()
        except KeyboardInterrupt:
            print("\nЧистильщик остановлен.")

if __name__ == "__main__":
    main()
