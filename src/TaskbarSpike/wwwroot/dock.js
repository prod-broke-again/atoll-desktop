const body = document.body;
const dockUi = document.getElementById("dock-ui");
const barUi = document.getElementById("bar-ui");
const islandDesks = document.getElementById("island-desks");
const islandDock = document.getElementById("island-dock");
const islandSys = document.getElementById("island-sys");
const desksEl = document.getElementById("desks");
const appsHostDock = document.getElementById("apps-dock");
const appsHostBar = document.getElementById("apps-bar");
const trayHostDock = document.getElementById("tray-dock");
const trayHostBar = document.getElementById("tray-bar");
// Один набор кнопок приложений и трея; он переезжает в активный режим.
let arranging = false;
let overflowId = "";
let mode = "dock";

const $all = (sel) => [...document.querySelectorAll(sel)];

function post(message) {
  window.chrome?.webview?.postMessage(message);
}

/* ---------- Иконки интерфейса (линейные, 24×24) ---------- */

const svg = (size, inner) =>
  '<svg class="ic" width="' + size + '" height="' + size + '" viewBox="0 0 24 24" aria-hidden="true">' + inner + "</svg>";
const SPK = "M4 9.5h3l4.5-4v13L7 14.5H4z";
const ICONS = {
  monitor: '<rect x="3" y="4" width="18" height="12" rx="1.5"/><path d="M8 20h8M12 16v4"/>',
  wifi: '<path d="M3 9.5a14 14 0 0 1 18 0"/><path d="M6 13a9.5 9.5 0 0 1 12 0"/><path d="M9 16.4a5 5 0 0 1 6 0"/><circle cx="12" cy="19.4" r=".6"/>',
  vol0: '<path d="' + SPK + '"/><path d="M15.5 9.5l5 5M20.5 9.5l-5 5"/>',
  vol1: '<path d="' + SPK + '"/><path d="M14.5 10a2.8 2.8 0 0 1 0 4"/>',
  vol2: '<path d="' + SPK + '"/><path d="M14.5 10a2.8 2.8 0 0 1 0 4"/><path d="M16.8 7.8a6 6 0 0 1 0 8.4"/>',
  vol3: '<path d="' + SPK + '"/><path d="M14.5 10a2.8 2.8 0 0 1 0 4"/><path d="M16.8 7.8a6 6 0 0 1 0 8.4"/><path d="M19 5.5a9.2 9.2 0 0 1 0 13"/>',
  bell: '<path d="M6 16.5V11a6 6 0 0 1 12 0v5.5l1.5 1.5h-15z"/><path d="M10 20.5a2 2 0 0 0 4 0"/>',
  bellOff: '<path d="M6 16.5V11a6 6 0 0 1 12 0v5.5l1.5 1.5h-15z"/><path d="M10 20.5a2 2 0 0 0 4 0"/><path d="M4 4l16 16"/>',
};

function setIcon(el, key, size) {
  if (el.dataset.key === key + size) return;
  el.dataset.key = key + size;
  el.innerHTML = svg(size, ICONS[key]);
}

function volumeKey(muted, volume) {
  if (muted || volume <= 0) return "vol0";
  if (volume < 34) return "vol1";
  if (volume < 67) return "vol2";
  return "vol3";
}

/* ---------- Превью и подписи ---------- */

let previewId = null;
let previewAt = 0;
let previewLive = false;
let previewTimer = 0;

function postPreview(button) {
  const rect = button.getBoundingClientRect();
  post({
    type: "preview",
    id: button.dataset.id,
    name: button.dataset.name || "",
    anchor: (rect.left + rect.width / 2) / (window.innerWidth || 1),
    top: rect.top,
    count: Number(button.dataset.count) || 0,
  });
}

function requestPreview(button, force) {
  const now = Date.now();
  const id = button.dataset.id;
  if (!force && previewLive && previewId === id && now - previewAt < 800) return;
  previewId = id;
  previewAt = now;
  previewLive = true;
  postPreview(button);
}

function scheduleHover(button) {
  clearTimeout(previewTimer);
  previewTimer = setTimeout(() => requestPreview(button, false), 300);
}

function hidePreview() {
  clearTimeout(previewTimer);
  if (!previewLive) return;
  previewLive = false;
  previewId = null;
  post({ type: "preview-hide" });
}

/* ---------- Увеличение дока ---------- */

function setMagnify(target) {
  const items = [...islandDock.querySelectorAll(".dock-item")];
  const idx = target ? items.indexOf(target) : -1;
  items.forEach((el, i) => {
    const v = i === idx ? "1" : Math.abs(i - idx) === 1 && idx >= 0 ? "n" : "";
    if (v) el.dataset.mag = v;
    else delete el.dataset.mag;
  });
}

islandDock.addEventListener("pointerover", (event) => {
  if (mode !== "dock" || arranging) return;
  const item = event.target.closest && event.target.closest(".dock-item");
  setMagnify(item || null);
});
islandDock.addEventListener("pointerleave", () => setMagnify(null));

/* ---------- Часы ---------- */

function tick() {
  const now = new Date();
  const time = now.toLocaleTimeString("ru-RU", { hour: "2-digit", minute: "2-digit" });
  const dockDate = now.toLocaleDateString("ru-RU", { weekday: "short", day: "numeric", month: "short" });
  const barDate = now.toLocaleDateString("ru-RU", { day: "2-digit", month: "2-digit", year: "numeric" });
  $all(".js-time").forEach((el) => { el.textContent = time; });
  $all(".js-date").forEach((el) => { el.textContent = el.dataset.fmt === "bar" ? barDate : dockDate; });
}
tick();
setInterval(tick, 1000);

/* ---------- Кнопки ---------- */

function anchorOf(el) {
  const rect = el.getBoundingClientRect();
  return (rect.left + rect.width / 2) / (window.innerWidth || 1);
}

const barSearch = document.getElementById("bar-search");
let searchBlur = 0;
if (barSearch) {
  const sendQuery = () => post({ type: "search", anchor: anchorOf(barSearch), q: barSearch.value });
  // Re-focusing the dock window makes the browser restore focus to this input; that is not the
  // user asking for search, and used to flash the Start panel open under the tray click.
  let searchIntent = false;
  const markIntent = () => { searchIntent = true; };
  barSearch.addEventListener("pointerdown", markIntent, true);
  const searchLabel = barSearch.closest("label");
  if (searchLabel) searchLabel.addEventListener("pointerdown", markIntent, true);
  barSearch.addEventListener("keydown", markIntent, true);
  barSearch.addEventListener("focus", () => {
    if (searchIntent || barSearch.value) sendQuery();
  });
  barSearch.addEventListener("blur", () => { searchIntent = false; });
  barSearch.addEventListener("input", sendQuery);
  barSearch.addEventListener("keydown", (event) => {
    if (event.key !== "Escape") return;
    barSearch.value = "";
    post({ type: "search-close" });
  });
  barSearch.addEventListener("blur", () => {
    clearTimeout(searchBlur);
    searchBlur = setTimeout(() => {
      if (document.activeElement === barSearch) return;
      post({ type: "search-blur" });
    }, 180);
  });
}

document.addEventListener("click", (event) => {
  const el = event.target.closest && event.target.closest("[data-act]");
  if (!el) return;
  const act = el.dataset.act;
  switch (act) {
    case "start":
    case "search":
    case "quick":
      post({ type: act, anchor: anchorOf(el) });
      break;
    case "clock":
    case "lang":
    case "exit":
    case "desktop-toggle":
    case "desks-view":
      post({ type: act, anchor: anchorOf(el) });
      break;
    case "mode":
      post({ type: "mode", value: mode === "bar" ? "dock" : "bar" });
      break;
    case "tray-toggle":
      if (overflowId) post({ type: "tray", id: overflowId, anchor: anchorOf(el) });
      break;
  }
});

/* ---------- Снимок ---------- */

function applyStatus(data) {
  const net = data.network || {};
  const connected = !!net.connected;
  const audio = data.audio || {};
  const muted = !!audio.muted;
  const volume = Number(audio.volume) || 0;
  const dnd = data.dnd === true;
  const barMode = mode === "bar";
  const isize = barMode ? 17 : 18;
  $all(".js-net").forEach((el) => {
    setIcon(el, net.kind === "wifi" && connected ? "wifi" : "monitor", isize);
    el.classList.toggle("off", !connected);
  });
  $all(".js-vol").forEach((el) => {
    setIcon(el, volumeKey(muted, volume), isize);
  });
  $all(".js-bell").forEach((el) => setIcon(el, dnd ? "bellOff" : "bell", 17));
  $all(".js-bell-bar").forEach((el) => {
    el.hidden = !dnd;
    if (dnd) setIcon(el, "bellOff", 16);
  });
  const device = typeof audio.device === "string" ? audio.device : "";
  const netTitle = [net.name, net.status].filter(Boolean).join(" — ") || "Сеть";
  const audioTitle = [device, muted ? "Звук выключен" : volume + "%"].filter(Boolean).join(" — ") || "Звук";
  $all(".js-quick").forEach((el) => {
    el.title = netTitle + "\n" + audioTitle;
    el.classList.toggle("open", data.quick === true);
  });
  $all(".js-clock").forEach((el) => el.classList.toggle("open", data.clock === true));
  $all(".js-trayq").forEach((el) => el.classList.toggle("open", data.trayOpen === true));
  $all("[data-act='desks-view']").forEach((el) => el.classList.toggle("open", data.deskOpen === true));
  $all("#dock-start, #bar-start").forEach((el) => el.classList.toggle("open", data.start === true));
  $all("#bar-start").forEach((el) => {
    el.style.color = data.start === true ? "var(--accent)" : "";
  });
}

function applyMode(next) {
  if (next !== "dock" && next !== "bar") next = "dock";
  if (next === mode && body.classList.contains("v-" + next)) return;
  mode = next;
  body.classList.toggle("v-bar", next === "bar");
  body.classList.toggle("v-dock", next === "dock");
  dockUi.hidden = false;
  barUi.hidden = next !== "bar";
  dockUi.style.display = next === "bar" ? "none" : "";
  const fromApps = next === "bar" ? appsHostDock : appsHostBar;
  const fromTray = next === "bar" ? trayHostDock : trayHostBar;
  (next === "bar" ? appsHostBar : appsHostDock).append(...fromApps.children);
  (next === "bar" ? trayHostBar : trayHostDock).append(...fromTray.children);
  setMagnify(null);
  scheduleLayout();
}

function appButtons() {
  const host = mode === "bar" ? appsHostBar : appsHostDock;
  return [...host.querySelectorAll(".app")];
}
function appHost() {
  return mode === "bar" ? appsHostBar : appsHostDock;
}
function trayHost() {
  return mode === "bar" ? trayHostBar : trayHostDock;
}

function findApp(id) {
  return appButtons().find((b) => b.dataset.id === id) || null;
}

function render(data) {
  applyMode(data.mode === "bar" ? "bar" : "dock");
  applyStatus(data);
  if (typeof data.language === "string" && data.language.length > 0) {
    $all(".js-lang").forEach((el) => { el.textContent = data.language; });
  }

  renderDesks(Array.isArray(data.desktops) ? data.desktops : []);
  const host = appHost();
  const apps = Array.isArray(data.apps) ? data.apps : [];
  const seen = new Set();
  const desired = [];
  for (const app of apps) {
    if (!app || typeof app.id !== "string" || !/^[0-9A-F]{16}$/i.test(app.id)) continue;
    seen.add(app.id);
    desired.push(app.id);
    let button = findApp(app.id);
    if (!button) {
      button = document.createElement("button");
      button.type = "button";
      button.className = "app dock-item btn-reset";
      button.dataset.id = app.id;
      const img = document.createElement("img");
      img.alt = "";
      img.hidden = true;
      const fallback = document.createElement("span");
      fallback.className = "fallback";
      const badge = document.createElement("span");
      badge.className = "badge";
      badge.hidden = true;
      button.append(img, fallback, badge);
      bindDrag(button);
      button.addEventListener("click", () => {
        if (Number(button.dataset.suppress || 0) > performance.now()) {
          delete button.dataset.suppress;
          return;
        }
        clearTimeout(previewTimer);
        if ((Number(button.dataset.count) || 0) > 1) {
          requestPreview(button, true);
          return;
        }
        post({ type: "app", id: button.dataset.id });
      });
      button.addEventListener("contextmenu", (event) => {
        event.preventDefault();
        clearTimeout(previewTimer);
        hidePreview();
        post({ type: "app-context", id: button.dataset.id, anchor: anchorOf(button) });
      });
      button.addEventListener("pointerenter", () => {
        if (!arranging) scheduleHover(button);
      });
      button.addEventListener("pointerleave", () => hidePreview());
      if (!arranging) host.append(button);
    }

    button.classList.toggle("running", !!app.running);
    button.classList.toggle("active", !!app.active);
    button.dataset.name = app.name || "";
    const titles = Array.isArray(app.titles) ? app.titles.filter(Boolean) : [];
    button.dataset.count = String(Number(app.count) || 0);
    const notes = Number(app.badge) || 0;
    const badge = button.querySelector(".badge");
    const badgeText = notes > 99 ? "99+" : String(notes);
    if (button.dataset.badge !== badgeText) {
      button.dataset.badge = badgeText;
      badge.hidden = notes < 1;
      badge.textContent = badgeText;
    }
    // Подпись рисует окно превью, нативную подсказку не показываем.
    button.removeAttribute("title");
    button.setAttribute("aria-label", [app.name, ...titles].filter(Boolean).join(", "));

    const img = button.querySelector("img");
    const fallback = button.querySelector(".fallback");
    if (typeof app.icon === "string" && app.icon.startsWith("data:image/png;base64,")) {
      if (img.dataset.icon !== app.icon) {
        img.dataset.icon = app.icon;
        img.src = app.icon;
      }
      img.hidden = false;
      fallback.hidden = true;
    } else {
      img.hidden = true;
      fallback.hidden = false;
      fallback.textContent = (app.name || "?").trim().slice(0, 1).toUpperCase();
    }
  }

  for (const button of appButtons()) {
    if (!seen.has(button.dataset.id)) button.remove();
  }
  if (!arranging) reorderChildren(host, desired);
  renderTray(Array.isArray(data.tray) ? data.tray : []);
  scheduleLayout();
}

function reorderChildren(container, desiredIds) {
  const nodes = [...container.children].filter((n) => n.dataset && n.dataset.id);
  const currentIds = nodes.map((node) => node.dataset.id);
  const dirty =
    currentIds.length !== desiredIds.length || currentIds.some((id, index) => id !== desiredIds[index]);
  if (!dirty) return;
  const byId = new Map(nodes.map((node) => [node.dataset.id, node]));
  for (const id of desiredIds) {
    const node = byId.get(id);
    if (node) container.append(node);
  }
}

let desksKey = "";

function renderDesks(desks) {
  const signature =
    desks.length + "|" +
    desks.map((d) => (d && typeof d.index === "number" ? d.index + ":" + (d.current ? 1 : 0) + ":" + (typeof d.name === "string" ? d.name : "") : "x")).join("|");
  if (signature === desksKey) return;
  desksKey = signature;
  islandDesks.hidden = desks.length === 0;
  desksEl.replaceChildren();
  if (desks.length === 0) {
    scheduleLayout();
    return;
  }
  for (const desk of desks) {
    if (!desk || typeof desk.index !== "number") continue;
    const button = document.createElement("button");
    button.type = "button";
    button.className = "desk btn-reset" + (desk.current ? " current" : "");
    button.textContent = typeof desk.name === "string" && desk.name.length > 0 ? desk.name : String(desk.index + 1);
    button.title = desks.length > 1 ? "Рабочий стол. Правый клик закрывает." : "Рабочий стол";
    button.addEventListener("click", () => post({ type: "desktop", index: desk.index }));
    button.addEventListener("contextmenu", (event) => {
      event.preventDefault();
      if (desks.length > 1) post({ type: "desktop-close", index: desk.index });
    });
    desksEl.append(button);
  }
  const add = document.createElement("button");
  add.type = "button";
  add.className = "desk btn-reset";
  add.title = "Новый рабочий стол";
  add.setAttribute("aria-label", "Новый рабочий стол");
  add.innerHTML = svg(16, '<path d="M12 6v12M6 12h12"/>');
  add.addEventListener("click", () => post({ type: "desktop-add" }));
  desksEl.append(add);
  scheduleLayout();
}

/* ---------- Сортировка перетаскиванием (раздел 7) ---------- */

function flip(run) {
  const before = new Map(appButtons().map((el) => [el, el.getBoundingClientRect().left]));
  run();
  for (const [el, left] of before) {
    const dx = left - el.getBoundingClientRect().left;
    if (Math.abs(dx) < 1) continue;
    el.animate(
      [{ transform: "translateX(" + dx + "px)" }, { transform: "translateX(0)" }],
      { duration: 180, easing: "cubic-bezier(.2,.8,.2,1)" },
    );
  }
}

function bindDrag(button) {
  button.addEventListener("pointerdown", (event) => {
    if (event.button !== 0) return;
    const origin = event.clientX;
    const startOrder = appButtons().map((el) => el.dataset.id);
    let dragged = false;
    let cancelled = false;

    const finish = (commit) => {
      window.removeEventListener("pointermove", move);
      window.removeEventListener("pointerup", up);
      window.removeEventListener("pointercancel", cancel);
      window.removeEventListener("keydown", key, true);
      button.classList.remove("dragging");
      if (!dragged) return;
      arranging = false;
      body.classList.remove("arranging");
      button.dataset.suppress = String(performance.now() + 250);
      if (commit) {
        post({ type: "reorder", ids: appButtons().map((el) => el.dataset.id) });
      }
      scheduleLayout();
    };
    const move = (ev) => {
      if (!dragged && Math.abs(ev.clientX - origin) < 8) return;
      if (!dragged) {
        dragged = true;
        arranging = true;
        body.classList.add("arranging");
        setMagnify(null);
        button.classList.add("dragging");
        try { button.setPointerCapture(ev.pointerId); } catch { /* ignore */ }
        hidePreview();
      }
      const hit = document.elementFromPoint(ev.clientX, ev.clientY);
      const other = hit && hit.closest ? hit.closest(".app") : null;
      const host = appHost();
      if (!other || other === button || other.parentElement !== host) return;
      const box = other.getBoundingClientRect();
      const before = ev.clientX < box.left + box.width / 2;
      const ref = before ? other : other.nextSibling;
      if (ref === button || (before && other.previousSibling === button)) return;
      flip(() => host.insertBefore(button, ref));
    };
    const up = () => finish(!cancelled);
    const cancel = () => { cancelled = true; finish(false); };
    const key = (ev) => {
      if (ev.key !== "Escape" || !dragged) return;
      ev.preventDefault();
      cancelled = true;
      const host = appHost();
      flip(() => {
        for (const id of startOrder) {
          const el = findApp(id);
          if (el) host.append(el);
        }
      });
    };
    // Window-level listeners: re-inserting the dragged node (FLIP reorder) drops its
    // pointer capture, so the button itself would stop receiving moves.
    window.addEventListener("pointermove", move);
    window.addEventListener("pointerup", up);
    window.addEventListener("pointercancel", cancel);
    window.addEventListener("keydown", key, true);
  });
}

/* ---------- Трей ---------- */

function renderTray(icons) {
  const host = trayHost();
  const seen = new Set();
  const desired = [];
  overflowId = "";
  for (const icon of icons) {
    if (!icon || typeof icon.id !== "string" || !/^[0-9A-F]{16}$/i.test(icon.id)) continue;
    if (icon.overflow) {
      overflowId = icon.id;
      continue;
    }
    seen.add(icon.id);
    desired.push(icon.id);
    let button = [...host.querySelectorAll(".tray-icon")].find((b) => b.dataset.id === icon.id);
    if (!button) {
      button = document.createElement("button");
      button.type = "button";
      button.className = "tray-icon btn-reset";
      button.dataset.id = icon.id;
      const img = document.createElement("img");
      img.alt = "";
      img.hidden = true;
      const fallback = document.createElement("span");
      fallback.className = "fallback";
      button.append(img, fallback);
      button.addEventListener("click", () => {
        post({ type: "tray", id: button.dataset.id, anchor: anchorOf(button) });
      });
      button.addEventListener("contextmenu", (event) => {
        event.preventDefault();
        post({ type: "tray-context", id: button.dataset.id });
      });
      host.append(button);
    }
    button.title = icon.name || "";
    const img = button.querySelector("img");
    const fallback = button.querySelector(".fallback");
    if (typeof icon.icon === "string" && icon.icon.startsWith("data:image/png;base64,")) {
      if (img.dataset.icon !== icon.icon) {
        img.dataset.icon = icon.icon;
        img.src = icon.icon;
      }
      img.hidden = false;
      fallback.hidden = true;
    } else {
      img.hidden = true;
      fallback.hidden = false;
      fallback.textContent = (icon.name || "?").trim().slice(0, 1).toUpperCase();
    }
  }
  for (const button of [...host.querySelectorAll(".tray-icon")]) {
    if (!seen.has(button.dataset.id)) button.remove();
  }
  reorderChildren(host, desired);
}

/* ---------- Геометрия островов для региона окна (8.1) ---------- */

let layoutQueued = false;
let layoutKey = "";

function scheduleLayout() {
  if (layoutQueued) return;
  layoutQueued = true;
  requestAnimationFrame(() => {
    layoutQueued = false;
    postLayout();
  });
}

function postLayout() {
  let rects = [];
  if (mode === "bar") {
    const h = Math.min(48, window.innerHeight);
    rects.push({ x: 0, y: window.innerHeight - h, w: window.innerWidth, h, r: 0 });
  } else {
    // Центральный остров не должен наезжать на боковые.
    const side = Math.max(islandDesks.hidden ? 0 : islandDesks.offsetWidth, islandSys.offsetWidth);
    document.documentElement.style.setProperty("--side", side + 24 + "px");
    for (const el of [islandDesks, islandDock, islandSys]) {
      if (el.hidden) continue;
      const r = el.getBoundingClientRect();
      if (r.width < 1 || r.height < 1) continue;
      rects.push({
        x: Math.round(r.left * 100) / 100,
        y: Math.round(r.top * 100) / 100,
        w: Math.round(r.width * 100) / 100,
        h: Math.round(r.height * 100) / 100,
        r: 16,
      });
    }
  }
  const key = mode + "|" + window.innerWidth + "x" + window.innerHeight + "|" + JSON.stringify(rects);
  if (key === layoutKey) return;
  layoutKey = key;
  post({ type: "layout", mode, rects });
}

const resizeObserver = new ResizeObserver(scheduleLayout);
[islandDesks, islandDock, islandSys].forEach((el) => resizeObserver.observe(el));
window.addEventListener("resize", scheduleLayout);

const SURFACE_MIX = 0.07;
const SURFACE_BASE = [12, 13, 20];

function channel(from, to, t) {
  return Math.round(Math.min(255, Math.max(0, from + (to - from) * t)));
}

function rgbToHsl(r, g, b) {
  const rn = r / 255;
  const gn = g / 255;
  const bn = b / 255;
  const max = Math.max(rn, gn, bn);
  const min = Math.min(rn, gn, bn);
  const l = (max + min) / 2;
  let h = 0;
  let s = 0;
  const d = max - min;
  if (d !== 0) {
    s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
    if (max === rn) h = ((gn - bn) / d) % 6;
    else if (max === gn) h = (bn - rn) / d + 2;
    else h = (rn - gn) / d + 4;
    h /= 6;
    if (h < 0) h += 1;
  }
  return [h, s, l];
}

function hslToRgb(h, s, l) {
  const hue = (p, q, t) => {
    let x = t;
    if (x < 0) x += 1;
    if (x > 1) x -= 1;
    if (x < 1 / 6) return p + (q - p) * 6 * x;
    if (x < 1 / 2) return q;
    if (x < 2 / 3) return p + (q - p) * (2 / 3 - x) * 6;
    return p;
  };
  if (s === 0) {
    const gray = Math.round(l * 255);
    return [gray, gray, gray];
  }
  const q = l < 0.5 ? l * (1 + s) : l + s - l * s;
  const p = 2 * l - q;
  return [hue(p, q, h + 1 / 3), hue(p, q, h), hue(p, q, h - 1 / 3)].map((v) => Math.round(v * 255));
}

function luminance(r, g, b) {
  const lin = (c) => {
    const s = c / 255;
    return s <= 0.04045 ? s / 12.92 : Math.pow((s + 0.055) / 1.055, 2.4);
  };
  return 0.2126 * lin(r) + 0.7152 * lin(g) + 0.0722 * lin(b);
}

function contrast(a, b) {
  const hi = Math.max(luminance(a[0], a[1], a[2]), luminance(b[0], b[1], b[2]));
  const lo = Math.min(luminance(a[0], a[1], a[2]), luminance(b[0], b[1], b[2]));
  return (hi + 0.05) / (lo + 0.05);
}

function mixToward(rgb, target, t) {
  return rgb.map((channelValue, index) => channel(channelValue, target[index], t));
}

function readableAccent(rgb, surface) {
  if (contrast(rgb, surface) >= 3) return rgb;
  const once = mixToward(rgb, [255, 255, 255], 0.55);
  if (contrast(once, surface) >= 3) return once;
  return mixToward(once, [255, 255, 255], 0.45);
}

function applyTheme(accent) {
  const match = /^#?([0-9a-f]{6})$/i.exec(accent || "");
  const packed = match ? parseInt(match[1], 16) : 0x8caaff;
  const rgb = [(packed >> 16) & 255, (packed >> 8) & 255, packed & 255];
  const surface = SURFACE_BASE.map((from, index) => channel(from, rgb[index], SURFACE_MIX));
  const ink = readableAccent(rgb, surface);
  const [h, s, l] = rgbToHsl(rgb[0], rgb[1], rgb[2]);
  const second = hslToRgb((h + 40 / 360) % 1, s, l);
  const darkInk = [22, 18, 16];
  const lightInk = [255, 255, 255];
  // The accent fill (Start tile, pressed toggles) is the raw accent, so the glyph colour is judged against it, not the lightened ink.
  const onAccent = contrast(darkInk, rgb) >= contrast(lightInk, rgb) ? darkInk : lightInk;
  const root = document.documentElement.style;
  root.setProperty("--accent-rgb", rgb.join(", "));
  root.setProperty("--accent-2-rgb", second.join(", "));
  root.setProperty("--accent-ink-rgb", ink.join(", "));
  root.setProperty("--on-accent-rgb", onAccent.join(", "));
  root.setProperty("--surface-rgb", surface.join(", "));
}

applyTheme(window.__spikeAccent || "#8CAAFF");

window.chrome?.webview?.addEventListener("message", (event) => {
  const data = typeof event.data === "string" ? JSON.parse(event.data) : event.data;
  if (!data || typeof data.type !== "string") return;
  if (data.type === "theme") {
    applyTheme(data.accent);
    return;
  }
  if (data.type === "glass") {
    body.classList.toggle("no-glass", data.on === false);
    return;
  }
  if (data.type === "search-clear") {
    const input = document.getElementById("bar-search");
    if (input) input.value = "";
    return;
  }
  if (data.type === "language") {
    if (typeof data.language === "string" && data.language.length > 0) {
      $all(".js-lang").forEach((el) => { el.textContent = data.language; });
    }
    return;
  }
  if (data.type === "snapshot") {
    render(data);
  }
});

scheduleLayout();
