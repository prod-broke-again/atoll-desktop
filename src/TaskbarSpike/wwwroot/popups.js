/* Всплывающие панели (HANDOFF 5, 8.2). Этап 2: «Сеть и звук».
   Страница живёт всегда: данные приходят снимками {type:"data"} пока окно скрыто,
   открытие = {type:"popup"} → синхронный рендер → {type:"popup-size"} → окно показывается. */

const body = document.body;
const post = (message) => window.chrome?.webview?.postMessage(message);

/* ---------- Иконки ---------- */

const svg = (size, inner) =>
  '<svg class="ic" width="' + size + '" height="' + size + '" viewBox="0 0 24 24" aria-hidden="true">' + inner + "</svg>";
const SPK = "M4 9.5h3l4.5-4v13L7 14.5H4z";
const P = {
  monitor: '<rect x="3" y="4" width="18" height="12" rx="1.5"/><path d="M8 20h8M12 16v4"/>',
  wifi: '<path d="M3 9.5a14 14 0 0 1 18 0"/><path d="M6 13a9.5 9.5 0 0 1 12 0"/><path d="M9 16.4a5 5 0 0 1 6 0"/><circle cx="12" cy="19.4" r=".6"/>',
  vol0: '<path d="' + SPK + '"/><path d="M15.5 9.5l5 5M20.5 9.5l-5 5"/>',
  vol2: '<path d="' + SPK + '"/><path d="M14.5 10a2.8 2.8 0 0 1 0 4"/><path d="M16.8 7.8a6 6 0 0 1 0 8.4"/>',
  chevron: '<path d="M9 6l6 6-6 6"/>',
  back: '<path d="M19 12H5M11 6l-6 6 6 6"/>',
  mixer: '<path d="M4 7h9M17 7h3M4 17h3M11 17h9"/><circle cx="15" cy="7" r="2"/><circle cx="9" cy="17" r="2"/>',
  pencil: '<path d="M4 20h4L19 9l-4-4L4 16z"/><path d="M13.5 6.5l4 4"/>',
  gear: '<circle cx="12" cy="12" r="3"/><path d="M12 3v2.5M12 18.5V21M3 12h2.5M18.5 12H21M5.6 5.6l1.8 1.8M16.6 16.6l1.8 1.8M5.6 18.4l1.8-1.8M16.6 7.4l1.8-1.8"/>',
  external: '<path d="M14 4h6v6M20 4l-9 9M18 14v5a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1V7a1 1 0 0 1 1-1h5"/>',
  shield: '<path d="M12 3l7 3v5c0 4.5-3 8.5-7 10-4-1.5-7-5.5-7-10V6z"/>',
  head: '<path d="M4 15v-3a8 8 0 0 1 16 0v3"/><rect x="3.5" y="14" width="4" height="6" rx="1.5"/><rect x="16.5" y="14" width="4" height="6" rx="1.5"/>',
  speaker: '<rect x="6" y="3" width="12" height="18" rx="2"/><circle cx="12" cy="14" r="3"/><path d="M12 7h.01"/>',
  bt: '<path d="M7 7l10 10-5 4V3l5 4L7 17"/>',
  night: '<path d="M19.5 14.5A8 8 0 1 1 9.5 4.5a6.5 6.5 0 0 0 10 10z"/>',
  dnd: '<path d="M6 16.5V11a6 6 0 0 1 12 0v5.5l1.5 1.5h-15z"/><path d="M10 20.5a2 2 0 0 0 4 0"/><path d="M4 4l16 16"/>',
  hotspot: '<circle cx="12" cy="12" r="1.6"/><path d="M8.5 15.5a5 5 0 0 1 0-7M15.5 8.5a5 5 0 0 1 0 7M5.6 18.4a9 9 0 0 1 0-12.8M18.4 5.6a9 9 0 0 1 0 12.8"/>',
  a11y: '<circle cx="12" cy="5" r="1.6"/><path d="M5 8.5l7 1.5 7-1.5M12 10v4.5l-3 6M12 14.5l3 6"/>',
  cast: '<path d="M3 8V6.5A1.5 1.5 0 0 1 4.5 5h15A1.5 1.5 0 0 1 21 6.5v11a1.5 1.5 0 0 1-1.5 1.5H14"/><path d="M3 12a7 7 0 0 1 7 7M3 15.5A3.5 3.5 0 0 1 6.5 19"/><path d="M3 19h.01"/>',
};

// Плитки: «toggle» переключает (или честно открывает настройки), «link» только открывает окно и не горит.
const TILES = [
  { key: "bluetooth", label: "Bluetooth", icon: "bt", kind: "toggle" },
  { key: "night", label: "Ночной свет", icon: "night", kind: "toggle" },
  { key: "dnd", label: "Не беспокоить", icon: "dnd", kind: "toggle" },
  { key: "hotspot", label: "Хот-спот", icon: "hotspot", kind: "toggle" },
  { key: "a11y", label: "Спец. возможности", icon: "a11y", kind: "link" },
  { key: "cast", label: "Трансляция", icon: "cast", kind: "link" },
];

const QUICK_PREF = "taskbarspike.quickTiles";
const defaultQuickKeys = () => TILES.map((tile) => tile.key);
function loadQuickKeys() {
  try {
    const raw = localStorage.getItem(QUICK_PREF);
    if (!raw) return defaultQuickKeys();
    const parsed = JSON.parse(raw);
    if (!Array.isArray(parsed)) return defaultQuickKeys();
    const allowed = new Set(TILES.map((tile) => tile.key));
    const keys = parsed.filter((key) => typeof key === "string" && allowed.has(key));
    return keys.length ? keys : defaultQuickKeys();
  } catch (err) {
    return defaultQuickKeys();
  }
}
function saveQuickKeys(keys) {
  try {
    localStorage.setItem(QUICK_PREF, JSON.stringify(keys));
  } catch (err) {
    /* private mode / quota */
  }
}

const esc = (text) => String(text ?? "").replace(/[&<>"]/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" })[c]);

/* ---------- Состояние ---------- */

const state = {
  audio: { volume: 0, muted: false, device: "", devices: [], sessions: [] },
  network: { kind: "none", connected: false, name: "", status: "", internet: false, vpn: false },
  quick: { bluetooth: false, hotspot: false, night: false, dnd: false, available: {} },
  notifications: { ok: true, items: [] },
  tray: [],
  desktops: [],
  wallpaper: "",
  start: { user: { name: "", initial: "?", avatar: null }, pinned: [], recommended: [], groups: [], tiles: [], results: [] },
  phone: null,
};

const panel = document.getElementById("panel-quick");
const clockEl = document.getElementById("panel-clock");
const trayEl = document.getElementById("panel-tray");
const desksEl = document.getElementById("panel-desks");
const startEl = document.getElementById("panel-start");
const panels = [panel, clockEl, trayEl, desksEl, startEl];
let page = "main"; // main | sound | net
let signature = "";
let dragging = false;
let holdUntil = 0; // после касания ползунков не перезаписываем их устаревшими данными
let panelOpen = false;
let localQuery = "";
let startView = "home"; // home | all — results follow the query
let powerOpen = false;
let railOpen = false;

function closeRail() {
  if (!railOpen) return;
  railOpen = false;
  const panel = startEl.querySelector(".rail-panel");
  if (panel) {
    panel.classList.remove("open");
    const btn = panel.querySelector("[data-act='rail']");
    if (btn) btn.setAttribute("aria-expanded", "false");
  }
}

let recMore = false;
let quickEdit = false;
let quickKeys = loadQuickKeys();
let tileEdit = false;

/* ---------- Шаблоны ---------- */

const netIcon = () => (state.network.kind === "wifi" ? P.wifi : P.monitor);
const volIcon = () => (state.audio.muted || state.audio.volume <= 0 ? P.vol0 : P.vol2);

function volumeRow(withSettings) {
  return (
    '<div class="vol-row">' +
    '<button class="icon-btn btn-reset js-mute" data-act="mute" type="button"></button>' +
    '<input class="slider js-vol" type="range" min="0" max="100" aria-label="Громкость">' +
    '<span class="val js-volval"></span>' +
    (withSettings
      ? '<button class="icon-btn wide btn-reset" data-act="go" data-page="sound" type="button" aria-label="Настройки звука: устройство вывода и микшер" title="Устройство вывода и микшер">' +
        svg(18, P.mixer) + svg(14, P.chevron) + "</button>"
      : "") +
    "</div>"
  );
}

function templateMain() {
  const visible = new Set(quickKeys);
  const source = quickEdit ? TILES : TILES.filter((tile) => visible.has(tile.key));
  const tiles = source.map((tile) => {
    const on = visible.has(tile.key);
    const action = quickEdit
      ? '<button class="tile-edit btn-reset" type="button" data-act="quick-vis" data-key="' + tile.key + '" aria-label="' +
        (on ? "Скрыть" : "Показать") + '">' + (on ? "−" : "+") + "</button>"
      : "";
    return (
      '<div class="tile' + (quickEdit && !on ? " dim" : "") + '">' +
      '<div class="tile-wrap">' +
      '<button class="tile-btn btn-reset" type="button" data-act="tile" data-key="' + tile.key + '" aria-pressed="false" aria-label="' + esc(tile.label) + '"' +
      (quickEdit ? " disabled" : "") + ">" +
      svg(20, P[tile.icon]) + "</button>" + action +
      '</div><span class="tile-label">' + esc(tile.label) + "</span></div>"
    );
  }).join("");
  return (
    '<div class="qs-body">' +
    '<button class="row-btn btn-reset" type="button" data-act="go" data-page="net">' +
    '<span class="js-netico"></span><span class="col"><span class="strong ellipsis js-netname"></span><span class="muted small ellipsis js-netstatus"></span></span>' +
    svg(16, P.chevron) + "</button>" +
    '<div class="tiles">' + tiles + "</div>" +
    '<div class="vol-block">' + volumeRow(true) +
    '<button class="dev-line btn-reset ellipsis js-devline" type="button" data-act="go" data-page="sound"></button></div>' +
    "</div>" +
    '<div class="qs-foot">' +
    (quickEdit
      ? '<button class="link-btn btn-reset" type="button" data-act="edit-quick">Готово</button>'
      : '<button class="icon-btn btn-reset" type="button" data-act="edit-quick" aria-label="Изменить быстрые настройки" title="Изменить быстрые настройки">' + svg(17, P.pencil) + "</button>") +
    '<button class="icon-btn btn-reset" type="button" data-act="open" data-key="settings" aria-label="Параметры" title="Параметры">' + svg(18, P.gear) + "</button>" +
    "</div>"
  );
}

function appIcon(session) {
  if (typeof session.icon === "string" && session.icon.startsWith("data:image/png;base64,")) {
    return '<img class="app-ico" alt="" src="' + session.icon + '">';
  }
  return '<span class="app-ico letter">' + esc((session.name || "?").trim().slice(0, 1).toUpperCase()) + "</span>";
}

function templateSound() {
  const devices = state.audio.devices
    .map(
      (device) =>
        '<button class="list-item btn-reset" type="button" data-act="device" data-id="' + esc(device.id) + '" aria-pressed="false">' +
        '<span class="radio"></span><span class="col"><span class="ellipsis">' + esc(device.name) + "</span>" +
        (device.sub ? '<span class="faint ellipsis" style="font-size:11.5px">' + esc(device.sub) + "</span>" : "") +
        "</span>" + '<span class="faint">' + svg(18, P[device.kind] || P.speaker) + "</span></button>"
    )
    .join("");
  const mixer = state.audio.sessions
    .map(
      (session) =>
        '<div class="mix" data-key="' + esc(session.key) + '">' + appIcon(session) +
        '<span class="mix-name ellipsis">' + esc(session.name) + "</span>" +
        '<input class="slider js-sess" type="range" min="0" max="100" aria-label="Громкость: ' + esc(session.name) + '">' +
        '<span class="val"></span></div>'
    )
    .join("");
  return (
    '<div class="qs-body">' +
    '<div class="sub-head"><button class="icon-btn btn-reset" type="button" data-act="go" data-page="main" aria-label="Назад">' + svg(18, P.back) + "</button><span>Звук</span></div>" +
    volumeRow(false) +
    '<div class="sec">Устройство вывода</div><div class="list">' + devices + "</div>" +
    '<div class="sec">Микшер громкости</div><div style="display:flex;flex-direction:column;gap:2px">' + mixer + "</div>" +
    "</div>" +
    '<div class="qs-foot"><button class="link-btn btn-reset" type="button" data-act="open" data-key="sound-settings">Дополнительные параметры звука</button>' + svg(14, P.external) + "</div>"
  );
}

function templateNet() {
  return (
    '<div class="qs-body">' +
    '<div class="sub-head"><button class="icon-btn btn-reset" type="button" data-act="go" data-page="main" aria-label="Назад">' + svg(18, P.back) + "</button><span>Сеть</span></div>" +
    '<div class="row-btn card"><span class="js-netico"></span><span class="col"><span class="strong ellipsis js-netname"></span><span class="muted small js-netstatus"></span></span><span class="dot-ok js-netdot"></span></div>' +
    '<button class="row-btn btn-reset" type="button" data-act="open" data-key="vpn">' + svg(20, P.shield) +
    '<span class="col"><span>VPN</span><span class="muted small js-vpn"></span></span>' + svg(16, P.chevron) + "</button>" +
    "</div>" +
    '<div class="qs-foot"><button class="link-btn btn-reset" type="button" data-act="open" data-key="network-settings">Параметры сети и Интернета</button>' + svg(14, P.external) + "</div>"
  );
}

/* ---------- Отрисовка ---------- */

function track(fill, value) {
  const rest = "rgba(255,255,255,.22)";
  return "linear-gradient(to right," + fill + " 0%," + fill + " " + value + "%," + rest + " " + value + "%," + rest + " 100%)";
}

function setSlider(el, value, silent) {
  el.value = String(value);
  el.style.background = track(silent ? "#8A8F9B" : "var(--accent)", value);
}

function sliderBusy() {
  return dragging || Date.now() < holdUntil;
}

function sig() {
  if (page === "sound") {
    return "sound|" + state.audio.devices.map((d) => d.id + (d.current ? "*" : "") + d.name + d.sub + d.kind).join(",") +
      "|" + state.audio.sessions.map((s) => s.key + (s.icon ? "i" : "")).join(",");
  }
  if (page === "main") {
    return "main|" + (quickEdit ? "e" : "v") + "|" + quickKeys.join(",");
  }
  return page;
}

function paint() {
  const a = state.audio;
  const n = state.network;
  const silent = a.muted || a.volume <= 0;

  for (const el of panel.querySelectorAll(".js-netico")) {
    const key = n.kind + (n.connected ? "1" : "0");
    if (el.dataset.k !== key) {
      el.dataset.k = key;
      el.innerHTML = svg(20, netIcon());
      el.style.color = n.connected ? "" : "var(--text-3)";
    }
  }
  // Кабель называем «Ethernet» (как в макете), Wi-Fi — именем сети. «Подключено» → с пояснением про интернет.
  const netName = n.kind === "ethernet" ? "Ethernet" : n.name || "Нет подключения";
  const netStatus = n.internet && n.status === "Подключено" ? "Подключено, есть доступ к интернету" : n.status || "";
  panel.querySelectorAll(".js-netname").forEach((el) => { el.textContent = netName; });
  panel.querySelectorAll(".js-netstatus").forEach((el) => { el.textContent = netStatus; });
  panel.querySelectorAll(".js-netdot").forEach((el) => { el.hidden = !n.internet; });
  panel.querySelectorAll(".js-vpn").forEach((el) => { el.textContent = n.vpn ? "Подключено" : "Не подключено"; });

  panel.querySelectorAll(".js-mute").forEach((el) => {
    const label = a.muted ? "Включить звук" : "Выключить звук";
    el.title = label;
    el.setAttribute("aria-label", label);
    const key = silent ? "0" : "1";
    if (el.dataset.k !== key) {
      el.dataset.k = key;
      el.innerHTML = svg(20, silent ? P.vol0 : P.vol2);
    }
  });
  if (!sliderBusy()) {
    panel.querySelectorAll(".js-vol").forEach((el) => setSlider(el, a.volume, silent));
    panel.querySelectorAll(".js-sess").forEach((el) => {
      const row = el.closest(".mix");
      const session = a.sessions.find((s) => s.key === row.dataset.key);
      if (session) setSlider(el, session.volume, false);
    });
  }
  panel.querySelectorAll(".js-volval").forEach((el) => { el.textContent = String(a.volume); });
  panel.querySelectorAll(".mix").forEach((row) => {
    const session = a.sessions.find((s) => s.key === row.dataset.key);
    const val = row.querySelector(".val");
    if (session && val && !sliderBusy()) val.textContent = String(session.volume);
  });
  panel.querySelectorAll(".js-devline").forEach((el) => { el.textContent = a.device || ""; });

  panel.querySelectorAll(".list-item").forEach((el) => {
    const current = a.devices.find((d) => d.id === el.dataset.id)?.current === true;
    el.classList.toggle("sel", current);
    el.setAttribute("aria-pressed", current ? "true" : "false");
    el.querySelector(".radio").classList.toggle("on", current);
  });

  panel.querySelectorAll(".tile-btn").forEach((el) => {
    const tile = TILES.find((t) => t.key === el.dataset.key);
    const on = tile.kind === "toggle" && state.quick.available?.[tile.key] === true && state.quick[tile.key] === true;
    el.setAttribute("aria-pressed", on ? "true" : "false");
  });
}

function render(forceBuild) {
  const next = sig();
  if (forceBuild || next !== signature) {
    signature = next;
    panel.innerHTML = page === "sound" ? templateSound() : page === "net" ? templateNet() : templateMain();
  }
  paint();
}

function reportSize() {
  const el = panels.find((item) => item && !item.hidden);
  if (!el) return;
  const splitEl = el.querySelector("[data-split]");
  post({
    type: "popup-size",
    panel: el.dataset.panel,
    w: el.offsetWidth,
    h: el.offsetHeight,
    split: splitEl ? splitEl.offsetHeight : 0,
  });
}

function go(next) {
  page = next;
  if (next !== "main") quickEdit = false;
  render(true);
  reportSize();
}

/* ---------- Действия ---------- */

let volTimer = 0;
let volPending = 0;
function sendVolume(value) {
  volPending = value;
  if (volTimer) return;
  volTimer = setTimeout(() => {
    volTimer = 0;
    post({ type: "volume", value: volPending });
  }, 30);
}

const sessTimers = new Map();
function sendSession(key, value) {
  clearTimeout(sessTimers.get(key));
  sessTimers.set(key, setTimeout(() => post({ type: "session-volume", key, value }), 30));
}

panel.addEventListener("click", (event) => {
  const el = event.target.closest && event.target.closest("[data-act]");
  if (!el) return;
  switch (el.dataset.act) {
    case "go":
      go(el.dataset.page);
      break;
    case "mute": {
      state.audio.muted = !state.audio.muted;
      holdUntil = Date.now() + 600;
      paint();
      post({ type: "mute", value: state.audio.muted });
      break;
    }
    case "device":
      state.audio.devices.forEach((d) => { d.current = d.id === el.dataset.id; });
      paint();
      post({ type: "audio-device", id: el.dataset.id });
      break;
    case "tile": {
      const tile = TILES.find((t) => t.key === el.dataset.key);
      if (tile.kind === "link") {
        post({ type: "quick-open", key: tile.key });
      } else {
        if (state.quick.available?.[tile.key]) {
          state.quick[tile.key] = !state.quick[tile.key]; // сразу, C# подтвердит снимком
          paint();
        }
        post({ type: "quick-toggle", key: tile.key });
      }
      break;
    }
    case "open":
      post({ type: "quick-open", key: el.dataset.key });
      break;
    case "edit-quick":
      quickEdit = !quickEdit;
      if (!quickEdit) saveQuickKeys(quickKeys);
      render(true);
      reportSize();
      break;
    case "quick-vis": {
      const key = el.dataset.key;
      if (!key) break;
      if (quickKeys.includes(key)) {
        if (quickKeys.length > 1) quickKeys = quickKeys.filter((item) => item !== key);
      } else {
        const order = TILES.map((tile) => tile.key);
        quickKeys = order.filter((item) => item === key || quickKeys.includes(item));
      }
      saveQuickKeys(quickKeys);
      render(true);
      reportSize();
      break;
    }
  }
});

panel.addEventListener("input", (event) => {
  const el = event.target;
  if (!(el instanceof HTMLInputElement)) return;
  const value = Number(el.value);
  holdUntil = Date.now() + 600;
  if (el.classList.contains("js-vol")) {
    if (state.audio.muted) {
      state.audio.muted = false;
      post({ type: "mute", value: false }); // перетаскивание снимает mute
    }
    state.audio.volume = value;
    sendVolume(value);
    paint();
  } else if (el.classList.contains("js-sess")) {
    const row = el.closest(".mix");
    el.style.background = track("var(--accent)", value);
    row.querySelector(".val").textContent = String(value);
    const session = state.audio.sessions.find((s) => s.key === row.dataset.key);
    if (session) session.volume = value;
    sendSession(row.dataset.key, value);
  }
});

panel.addEventListener("pointerdown", (event) => {
  if (event.target instanceof HTMLInputElement) dragging = true;
});
window.addEventListener("pointerup", () => {
  if (dragging) {
    dragging = false;
    holdUntil = Date.now() + 600;
  }
});
window.addEventListener("keydown", (event) => {
  if (event.key === "Escape") {
    if (railOpen) {
      closeRail();
      return;
    }
    if (powerOpen) {
      powerOpen = false;
      paintStart();
      return;
    }
    post({ type: "popup-close" });
  }
  if (event.key === "Tab") body.classList.add("kbd");
});
window.addEventListener("pointerdown", (event) => {
  body.classList.remove("kbd");
  if (railOpen && !event.target.closest?.(".rail-panel")) {
    closeRail();
  }
  if (powerOpen && !event.target.closest?.(".power-menu") && !event.target.closest?.("[data-act='power-toggle']") && !event.target.closest?.("[data-act='rail-power']")) {
    powerOpen = false;
    paintStart();
  }
}, true);
window.addEventListener("contextmenu", (event) => event.preventDefault());

new ResizeObserver(() => {
  if (panelOpen) reportSize();
}).observe(panel);
for (const el of [clockEl, trayEl, desksEl]) {
  new ResizeObserver(() => {
    if (panelOpen && !el.hidden) reportSize();
  }).observe(el);
}

/* ---------- Тема ---------- */

function applyTheme(accent) {
  const match = /^#?([0-9a-f]{6})$/i.exec(accent || "");
  const packed = match ? parseInt(match[1], 16) : 0x8caaff;
  const rgb = [(packed >> 16) & 255, (packed >> 8) & 255, packed & 255];
  const lin = (c) => {
    const s = c / 255;
    return s <= 0.04045 ? s / 12.92 : Math.pow((s + 0.055) / 1.055, 2.4);
  };
  const lum = (c) => 0.2126 * lin(c[0]) + 0.7152 * lin(c[1]) + 0.0722 * lin(c[2]);
  const contrast = (a, b) => (Math.max(lum(a), lum(b)) + 0.05) / (Math.min(lum(a), lum(b)) + 0.05);
  const dark = [22, 18, 16];
  const light = [255, 255, 255];
  const on = contrast(dark, rgb) >= contrast(light, rgb) ? dark : light;
  const root = document.documentElement.style;
  root.setProperty("--accent-rgb", rgb.join(", "));
  root.setProperty("--on-accent-rgb", on.join(", "));
}
applyTheme(window.__spikeAccent || "#8CAAFF");

/* ---------- Сообщения от C# ---------- */

window.chrome?.webview?.addEventListener("message", (event) => {
  const data = typeof event.data === "string" ? JSON.parse(event.data) : event.data;
  if (!data || typeof data.type !== "string") return;
  switch (data.type) {
    case "theme":
      applyTheme(data.accent);
      break;
    case "glass":
      body.classList.toggle("no-glass", data.on === false);
      break;
    case "data":
      if (data.audio) Object.assign(state.audio, data.audio);
      if (data.network) Object.assign(state.network, data.network);
      if (data.quick) Object.assign(state.quick, data.quick);
      if (data.notifications) state.notifications = data.notifications;
      if (Array.isArray(data.tray)) state.tray = data.tray;
      if (Array.isArray(data.desktops)) state.desktops = data.desktops;
      if (typeof data.wallpaper === "string") state.wallpaper = data.wallpaper;
      render(false);
      if (!clockEl.hidden) paintClock(false);
      if (!trayEl.hidden) paintTray();
      if (!desksEl.hidden) paintDesks();
      break;
    case "popup":
      body.classList.toggle("v-bar", data.mode === "bar");
      body.classList.toggle("v-dock", data.mode !== "bar");
      body.classList.toggle("no-glass", data.glass === false);
      if (typeof data.maxHeight === "number") document.documentElement.style.setProperty("--max-h", data.maxHeight + "px");
      for (const el of panels) {
        el.hidden = el.dataset.panel !== data.panel;
        el.classList.remove("open");
      }
      panelOpen = false;
      if (data.panel === "quick") {
        page = "main";
        render(true);
      } else if (data.panel === "clock") {
        view = { y: new Date().getFullYear(), m: new Date().getMonth() };
        paintClock(true);
      } else if (data.panel === "tray") {
        paintTray();
      } else if (data.panel === "desks") {
        paintDesks();
      } else if (data.panel === "start") {
        powerOpen = false;
        railOpen = false;
        recMore = false;
        startView = localQuery.trim() ? "results" : "home";
        paintStart();
      } else {
        return;
      }
      reportSize();
      break;
    case "start-data":
      if (data.start) state.start = data.start;
      state.phone = data.phone ?? null;
      {
        const typing = document.activeElement && document.activeElement.classList && document.activeElement.classList.contains("js-q");
        if (!typing && typeof data.q === "string") localQuery = data.q;
      }
      if (!startEl.hidden) {
        paintStart();
        reportSize();
      }
      break;
    case "popup-shown": {
      const shown = panels.find((el) => el.dataset.panel === data.panel) || panel;
      panelOpen = true;
      void shown.offsetWidth;
      shown.classList.add("open");
      if (data.panel === "start" && !body.classList.contains("v-bar")) {
        startEl.querySelector(".js-q")?.focus();
      }
      break;
    }
    case "popup-hide":
      panelOpen = false;
      for (const el of panels) el.classList.remove("open");
      dragging = false;
      page = "main";
      powerOpen = false;
      railOpen = false;
      startView = "home";
      recMore = false;
      break;
  }
});

/* ---------- Календарь, скрытые значки, рабочие столы ---------- */

let view = { y: new Date().getFullYear(), m: new Date().getMonth() };
const DOW = ["Пн", "Вт", "Ср", "Чт", "Пт", "Сб", "Вс"];
const cap = (text) => text ? text.charAt(0).toUpperCase() + text.slice(1) : text;

function timeNow() {
  return new Date().toLocaleTimeString("ru-RU", { hour: "2-digit", minute: "2-digit" });
}

function notesHtml() {
  const pack = state.notifications || { ok: true, items: [] };
  const items = Array.isArray(pack.items) ? pack.items : [];
  if (!pack.ok) {
    return '<div class="note-empty">Список уведомлений недоступен</div>' +
      '<div class="note-fallback"><button class="link-btn btn-reset" type="button" data-act="notif-center">Все уведомления Windows</button></div>';
  }
  const rows = items.length
    ? items.map((item) =>
      '<div class="note-wrap">' +
      '<button class="note btn-reset" type="button" data-act="notif" data-id="' + esc(item.id) + '">' +
      '<span class="note-ico">' + esc((item.app || "?").trim().slice(0, 1).toUpperCase()) + '</span>' +
      '<span class="col"><span class="note-line"><span class="note-app ellipsis">' + esc(item.app) + '</span><span class="note-time">' + esc(item.time) + '</span></span>' +
      '<span class="note-title ellipsis">' + esc(item.title) + '</span>' +
      (item.text ? '<span class="note-text">' + esc(item.text) + '</span>' : '') +
      '</span></button>' +
      '<button class="note-x btn-reset" type="button" data-act="notif-x" data-id="' + esc(item.id) + '" aria-label="Закрыть">×</button>' +
      '</div>').join("")
    : '<div class="note-empty">Нет новых уведомлений</div>';
  return rows;
}

function calendarHtml() {
  const now = new Date();
  const first = new Date(view.y, view.m, 1);
  const lead = (first.getDay() + 6) % 7;
  const cells = [];
  const cursor = new Date(view.y, view.m, 1 - lead);
  for (let i = 0; i < 42; i++) {
    const other = cursor.getMonth() !== view.m;
    const today = cursor.getFullYear() === now.getFullYear() && cursor.getMonth() === now.getMonth() && cursor.getDate() === now.getDate();
    cells.push('<button class="day btn-reset' + (other ? " other" : "") + (today ? " today" : "") + '" type="button" tabindex="-1">' + cursor.getDate() + "</button>");
    cursor.setDate(cursor.getDate() + 1);
  }
  const month = cap(new Date(view.y, view.m, 1).toLocaleDateString("ru-RU", { month: "long", year: "numeric" }));
  const longDate = cap(now.toLocaleDateString("ru-RU", { weekday: "long", day: "numeric", month: "long" }));
  const bar = body.classList.contains("v-bar");
  const head = bar
    ? '<div class="cal-bigtime js-bigtime">' + timeNow() + '</div><div class="cal-subdate">' + esc(longDate) + "</div>"
    : '<div class="cal-date">' + esc(longDate) + "</div>";
  return head +
    '<div class="cal-month"><span>' + esc(month) + '</span><span>' +
    '<button class="cal-nav btn-reset" type="button" data-act="month" data-dir="-1" aria-label="Предыдущий месяц">' + svg(16, '<path d="M6 14l6-6 6 6"/>') + "</button>" +
    '<button class="cal-nav btn-reset" type="button" data-act="month" data-dir="1" aria-label="Следующий месяц">' + svg(16, '<path d="M6 10l6 6 6-6"/>') + "</button></span></div>" +
    '<div class="cal-grid">' + DOW.map((day) => '<span class="dow">' + day + "</span>").join("") + cells.join("") + "</div>";
}

function paintClock(full) {
  if (full || !clockEl.querySelector("[data-split]")) {
    clockEl.innerHTML =
      '<div class="card glass cal-notes" data-split>' +
      '<div class="cal-head"><span class="cal-title">Уведомления</span>' +
      '<button class="chip btn-reset" type="button" data-act="notif-clear">Очистить все</button></div>' +
      '<div class="note-list js-notes">' + notesHtml() + "</div></div>" +
      '<div class="card glass">' + calendarHtml() + "</div>";
    return;
  }
  const host = clockEl.querySelector(".js-notes");
  if (host) host.innerHTML = notesHtml();
  const big = clockEl.querySelector(".js-bigtime");
  if (big) big.textContent = timeNow();
}

function paintTray() {
  const icons = state.tray || [];
  const cells = icons.length
    ? icons.map((icon) => {
      const face = typeof icon.icon === "string" && icon.icon.startsWith("data:image")
        ? '<img alt="" src="' + icon.icon + '">'
        : '<span class="letter">' + esc((icon.name || "?").trim().slice(0, 1).toUpperCase()) + "</span>";
      return '<button class="tray-cell btn-reset" type="button" data-act="tray-item" data-id="' + esc(icon.id) + '" title="' + esc(icon.name) + '">' + face + "</button>";
    }).join("")
    : '<div class="tray-empty">Нет скрытых значков</div>';
  const modeLabel = body.classList.contains("v-bar") ? "Переключить на док" : "Переключить на полосу";
  const modeIcon = body.classList.contains("v-bar")
    ? '<rect x="3" y="8" width="6" height="8" rx="2"/><rect x="11" y="6" width="10" height="12" rx="3"/>'
    : '<rect x="3" y="10" width="18" height="4" rx="1"/>';
  trayEl.innerHTML =
    '<div class="tray-grid">' + cells + "</div>" +
    '<div class="tray-rule"></div>' +
    '<button class="tray-row btn-reset" type="button" data-act="tray-mode">' + svg(18, modeIcon) + "<span>" + modeLabel + "</span></button>" +
    '<button class="tray-row btn-reset" type="button" data-act="tray-exit">' + svg(18, P.back) + "<span>Вернуть панель Windows</span></button>";
}

function paintDesks() {
  const wall = state.wallpaper ? " style=\"background-image:url('" + state.wallpaper.replace(/'/g, "") + "')\"" : "";
  const desks = (state.desktops || []).map((desk) =>
    '<div class="desk-item"><button class="desk-shot btn-reset' + (desk.current ? " current" : "") + '" type="button" data-act="desk" data-index="' + desk.index + '"' + wall + '></button>' +
    '<span class="desk-name ellipsis">' + esc(desk.name) + "</span></div>"
  ).join("");
  desksEl.innerHTML =
    '<div class="desk-title">Рабочие столы</div><div class="desk-row">' + desks +
    '<div class="desk-item"><button class="desk-shot add btn-reset" type="button" data-act="desk-add" aria-label="Создать">' + svg(22, '<path d="M12 6v12M6 12h12"/>') + '</button><span class="desk-name">Создать</span></div></div>';
}

clockEl.addEventListener("click", (event) => {
  const el = event.target.closest && event.target.closest("[data-act]");
  if (!el) return;
  if (el.dataset.act === "month") {
    const dir = Number(el.dataset.dir) || 0;
    const next = new Date(view.y, view.m + dir, 1);
    view = { y: next.getFullYear(), m: next.getMonth() };
    paintClock(true);
    reportSize();
    return;
  }
  if (el.dataset.act === "notif-clear") post({ type: "notif-clear" });
  else if (el.dataset.act === "notif-center") post({ type: "notif-center" });
  else if (el.dataset.act === "notif-x") post({ type: "notif-dismiss", id: el.dataset.id });
  else if (el.dataset.act === "notif") post({ type: "notif-open", id: el.dataset.id });
});

trayEl.addEventListener("click", (event) => {
  const el = event.target.closest && event.target.closest("[data-act]");
  if (!el) return;
  if (el.dataset.act === "tray-item") post({ type: "tray", id: el.dataset.id });
  else if (el.dataset.act === "tray-mode") post({ type: "mode", value: body.classList.contains("v-bar") ? "dock" : "bar" });
  else if (el.dataset.act === "tray-exit") post({ type: "exit" });
});
trayEl.addEventListener("contextmenu", (event) => {
  const el = event.target.closest && event.target.closest("[data-act='tray-item']");
  if (!el) return;
  event.preventDefault();
  post({ type: "tray-context", id: el.dataset.id });
});
desksEl.addEventListener("contextmenu", (event) => {
  const el = event.target.closest && event.target.closest("[data-act='desk']");
  if (!el) return;
  event.preventDefault();
  post({ type: "desktop-close", index: Number(el.dataset.index) });
});

/* ---------- Пуск ---------- */

const S = {
  search: '<circle cx="11" cy="11" r="6.5"/><path d="M16 16l4.5 4.5"/>',
  phone: '<rect x="7" y="2.5" width="10" height="19" rx="2.2"/><path d="M11 18.5h2"/>',
  chev: '<path d="M9 6l6 6-6 6"/>',
  chevL: '<path d="M15 6l-6 6 6 6"/>',
  power: '<path d="M12 3.5v8"/><path d="M7 6.5a7.5 7.5 0 1 0 10 0"/>',
  sleep: '<path d="M19.5 14.5A8 8 0 1 1 9.5 4.5a6.5 6.5 0 0 0 10 10z"/>',
  restart: '<path d="M20 12a8 8 0 1 1-2.3-5.6"/><path d="M20 4v4.5h-4.5"/>',
  signout: '<path d="M14 4h5a1 1 0 0 1 1 1v14a1 1 0 0 1-1 1h-5"/><path d="M10 8l-4 4 4 4M6 12h10"/>',
  burger: '<path d="M4 7h16M4 12h16M4 17h16"/>',
  doc: '<path d="M6 3.5h8L18.5 8v12.5H6z"/><path d="M13.5 3.5V8.5h5"/>',
  photo: '<rect x="3.5" y="5" width="17" height="14" rx="2"/><circle cx="9" cy="10" r="1.6"/><path d="M20 16l-5-5-8 8"/>',
  msg: '<path d="M4 5h16v11H9l-5 4z"/>',
  call: '<path d="M5 4h4l2 5-2.5 1.5a11 11 0 0 0 5 5L15 13l5 2v4a2 2 0 0 1-2 2A16 16 0 0 1 3 6a2 2 0 0 1 2-2z"/>',
  bell: '<path d="M6 16.5V11a6 6 0 0 1 12 0v5.5l1.5 1.5h-15z"/><path d="M10 20.5a2 2 0 0 0 4 0"/>',
};

function featureIcon(title) {
  const text = String(title || "").toLowerCase();
  if (text.includes("сообщ")) return S.msg;
  if (text.includes("звон")) return S.call;
  if (text.includes("фото")) return S.photo;
  if (text.includes("уведом")) return S.bell;
  return S.phone;
}

function face(icon, size, label) {
  if (typeof icon === "string" && icon.startsWith("data:image")) {
    return '<img alt="" src="' + icon + '" width="' + size + '" height="' + size + '">';
  }
  const ch = esc(String(label || "?").trim().slice(0, 1).toUpperCase() || "?");
  return '<span class="glyph" style="width:' + size + "px;height:" + size + "px;font-size:" + Math.max(11, Math.round(size * 0.38)) + 'px">' + ch + "</span>";
}

function avatarHtml(user, sm) {
  const person = user || { name: "", initial: "?", avatar: null };
  const cls = sm ? "avatar sm" : "avatar";
  if (typeof person.avatar === "string" && person.avatar.startsWith("data:image")) {
    return '<span class="' + cls + '"><img alt="" src="' + person.avatar + '"></span>';
  }
  return '<span class="' + cls + '" style="background:var(--accent)">' + esc(person.initial || "?") + "</span>";
}

function startBox() {
  const bar = body.classList.contains("v-bar");
  const phone = !!(state.phone && state.phone.device && state.phone.visible);
  const maxH = parseFloat(getComputedStyle(document.documentElement).getPropertyValue("--max-h")) || 900;
  return {
    bar,
    phone,
    w: bar ? (phone ? 1000 : 660) : (phone ? 940 : 600),
    h: Math.min(bar ? 600 : 640, maxH),
  };
}

function searchRow() {
  const has = !!(state.phone && state.phone.device);
  const on = !!(state.phone && state.phone.visible);
  const btn = has
    ? '<button class="side-btn btn-reset" type="button" data-act="phone" aria-pressed="' + (on ? "true" : "false") +
      '" title="' + (on ? "Скрыть телефон" : "Показать телефон") + '" style="background:' +
      (on ? "rgba(255,255,255,.16)" : "rgba(255,255,255,.08)") + ";color:" + (on ? "var(--accent)" : "var(--text)") + '">' +
      svg(18, S.phone) + "</button>"
    : "";
  return '<div class="search-row"><label class="search-field">' + svg(16, S.search) +
    '<input class="js-q" type="text" placeholder="Поиск приложений, параметров и документов" aria-label="Поиск" autocomplete="off" spellcheck="false" value="' + esc(localQuery) + '">' +
    "</label>" + btn + "</div>";
}

function homeHtml() {
  const pins = state.start.pinned || [];
  const rec = state.start.recommended || [];
  const shown = recMore ? rec : rec.slice(0, 4);
  let html = '<div class="block"><div class="sec-row"><span class="sec-title">Закреплено</span>' +
    '<button class="chip btn-reset" type="button" data-act="all">Все приложения ' + svg(12, S.chev) + "</button></div>";
  if (pins.length) {
    html += '<div class="grid-apps">' + pins.map((pin) =>
      '<button class="app-cell btn-reset" type="button" data-act="launch" data-id="' + esc(pin.id) + '">' +
      face(pin.icon, 36, pin.label) + "<span class=\"ellipsis\">" + esc(pin.label) + "</span></button>"
    ).join("") + "</div>";
  }
  html += "</div>";
  if (rec.length) {
    html += '<div class="block"><div class="sec-row"><span class="sec-title">Рекомендуем</span>' +
      '<button class="chip btn-reset" type="button" data-act="more">Больше ' + svg(12, S.chev) + "</button></div>" +
      '<div class="rec">' + shown.map((item) =>
        '<button class="list-item btn-reset" type="button" data-act="' + (item.file ? "file" : "launch") + '" data-id="' + esc(item.id) + '">' +
        face(item.icon, 32, item.name) +
        '<span class="col"><span class="ellipsis">' + esc(item.name) + '</span><span class="faint" style="font-size:12px">' + esc(item.sub) + "</span></span></button>"
      ).join("") + "</div></div>";
  }
  return html;
}

function allHtml() {
  const groups = state.start.groups || [];
  const rows = groups.map((group) =>
    '<div class="app-letter">' + esc(group.letter) + "</div>" +
    (group.items || []).map((item) =>
      '<button class="list-item btn-reset" type="button" data-act="launch" data-id="' + esc(item.id) + '">' +
      face(item.icon, 28, item.label) + '<span class="ellipsis">' + esc(item.label) + "</span></button>"
    ).join("")
  ).join("");
  return '<div class="block" style="flex:1;min-height:0"><div class="sec-row"><span class="sec-title">Все приложения</span>' +
    '<button class="chip btn-reset" type="button" data-act="home">' + svg(12, S.chevL) + " Назад</button></div>" +
    '<div class="scroll" style="flex:1;min-height:0">' + rows + "</div></div>";
}

function resultsHtml(bar) {
  const rows = state.start.results || [];
  const itemClass = bar ? "w10-item" : "list-item";
  const tall = bar ? ' style="height:52px"' : "";
  let body;
  if (!rows.length) {
    body = '<span class="muted" style="padding:4px 2px 8px">Среди приложений ничего не нашлось</span>' +
      '<button class="' + itemClass + ' btn-reset" type="button" data-act="web"' + tall + ">" +
      svg(20, S.search) + "<span>Искать «" + esc(localQuery.trim()) + "» в интернете</span></button>";
  } else {
    body = rows.map((row) =>
      '<button class="' + itemClass + ' btn-reset" type="button" data-act="' + (row.file ? "file" : "launch") + '" data-id="' + esc(row.id) + '"' + tall + ">" +
      face(row.icon, 32, row.label) +
      '<span class="col"><span class="ellipsis">' + esc(row.label) + '</span><span class="faint" style="font-size:12px">' + esc(row.kind) + "</span></span></button>"
    ).join("");
  }
  return '<div class="block" style="flex:1;min-height:0;gap:8px"><span class="sec-title">Лучшее соответствие</span><div class="list scroll" style="flex:1;min-height:0">' + body + "</div></div>";
}

function winList() {
  const groups = state.start.groups || [];
  return '<div class="scroll" style="width:240px;flex:none;padding:6px 0 12px">' + groups.map((group) =>
    '<div class="w10-letter">' + esc(group.letter) + "</div>" +
    (group.items || []).map((item) =>
      '<button class="w10-item btn-reset" type="button" data-act="launch" data-id="' + esc(item.id) + '" data-pin="1">' +
      face(item.icon, 28, item.label) + "<span class=\"ellipsis\">" + esc(item.label) + "</span></button>"
    ).join("")
  ).join("") + "</div>";
}

function winTiles() {
  const groups = state.start.tiles || [];
  const head =
    '<div class="w10-edit-row"><button class="chip btn-reset" type="button" data-act="tile-edit">' +
    (tileEdit ? "Готово" : "Изменить") + "</button></div>";
  return '<div class="scroll" style="flex:1;min-width:0;padding:14px 16px 12px 8px">' + head + groups.map((group) =>
    '<div class="w10-group">' + esc(group.title) + '</div><div class="w10-tiles">' +
    (group.items || []).map((item) =>
      '<div class="w10-tile-wrap' + (tileEdit ? " editing" : "") + '">' +
      '<button class="w10-tile btn-reset" type="button" data-act="launch" data-id="' + esc(item.id) + '" data-unpin="1">' +
      face(item.icon, 32, item.label) + '<span class="ellipsis">' + esc(item.label) + "</span></button>" +
      (tileEdit
        ? '<button class="w10-unpin btn-reset" type="button" data-act="unpin" data-id="' + esc(item.id) + '" title="Открепить" aria-label="Открепить">×</button>'
        : "") +
      "</div>"
    ).join("") + "</div>"
  ).join("") +
    (tileEdit
      ? '<p class="w10-edit-hint">ПКМ по приложению слева — закрепить. × на плитке — открепить.</p>'
      : "") +
    "</div>";
}

function powerMenu(bar) {
  if (!powerOpen) return "";
  const item = (action, icon, label) =>
    '<button class="list-item btn-reset" type="button" data-act="power" data-action="' + action + '">' +
    (bar ? "" : svg(18, icon)) + "<span>" + label + "</span></button>";
  const place = bar ? ' style="left:4px;right:auto;bottom:52px"' : "";
  return '<div class="power-menu glass"' + place + ">" +
    item("sleep", S.sleep, "Спящий режим") +
    item("shutdown", S.power, "Завершение работы") +
    item("restart", S.restart, "Перезагрузка") +
    item("signout", S.signout, "Выход") +
    "</div>";
}

function railHtml() {
  const user = state.start.user || { name: "", initial: "?", avatar: null };
  const has = !!(state.phone && state.phone.device);
  const on = !!(state.phone && state.phone.visible);
  const phoneBtn = has
    ? '<button class="rail-btn btn-reset' + (on ? " on" : "") + '" type="button" data-act="phone" aria-pressed="' + (on ? "true" : "false") +
      '" title="' + (on ? "Скрыть телефон" : "Показать телефон") + '"><span class="rail-ic" style="color:' +
      (on ? "var(--accent)" : "inherit") + '">' + svg(18, S.phone) + "</span><span>Телефон</span></button>"
    : "";
  const row = (key, icon, label, extra) =>
    '<button class="rail-btn btn-reset" type="button" data-act="shell" data-key="' + key + '"><span class="rail-ic">' +
    (extra || svg(18, icon)) + "</span><span class=\"ellipsis\">" + label + "</span></button>";
  return '<div class="w10-rail"><div class="rail-panel' + (railOpen ? " open" : "") + '">' +
    '<button class="rail-btn btn-reset" type="button" data-act="rail" aria-expanded="' + (railOpen ? "true" : "false") + '" title="Развернуть">' +
    '<span class="rail-ic">' + svg(18, S.burger) + '</span><span class="rail-title">ПУСК</span></button>' +
    '<span style="flex:1"></span>' + phoneBtn +
    row("account", "", esc(user.name), avatarHtml(user, true)) +
    row("docs", S.doc, "Документы") +
    row("pics", S.photo, "Изображения") +
    row("settings", P.gear, "Параметры") +
    '<button class="rail-btn btn-reset" type="button" data-act="rail-power"><span class="rail-ic">' + svg(18, S.power) + "</span><span>Выключение</span></button>" +
    "</div></div>";
}

function footHtml() {
  const user = state.start.user || { name: "", initial: "?", avatar: null };
  return '<div class="start-foot"><button class="user-btn btn-reset" type="button" data-act="shell" data-key="account">' +
    avatarHtml(user, false) + '<span class="ellipsis">' + esc(user.name) + "</span></button>" +
    '<button class="icon-btn btn-reset" type="button" data-act="power-toggle" aria-label="Завершение работы" title="Завершение работы">' +
    svg(18, S.power) + "</button></div>";
}

function phoneHtml() {
  const phone = state.phone;
  if (!phone || !phone.visible || !phone.device) return "";
  const device = phone.device;
  const wall = device.wallpaper ? "background-image:url('" + String(device.wallpaper).replace(/'/g, "") + "')" : "";
  const glyphs = (device.glyphs || []).map((glyph) => '<img alt="" src="' + glyph + '" width="15" height="15">').join("");
  const features = (phone.features || []).map((feature) => {
    const icon = typeof feature.icon === "string" && feature.icon.startsWith("data:image")
      ? '<img alt="" src="' + feature.icon + '" width="17" height="17">'
      : svg(17, featureIcon(feature.title));
    const badge = feature.count > 0 ? '<span class="count">' + (feature.count > 99 ? "99+" : feature.count) + "</span>" : "";
    return '<button class="feat btn-reset" type="button" data-act="plink" data-launch="' + esc(feature.launch) + '">' +
      '<span class="feat-ic">' + icon + '</span><span class="ellipsis" style="flex:1;font-size:12.5px">' + esc(feature.title) + "</span>" + badge + "</button>";
  }).join("");
  const recent = (phone.recent || []).map((entry) => {
    const avatar = typeof entry.icon === "string" && entry.icon.startsWith("data:image")
      ? '<span class="person"><img alt="" src="' + entry.icon + '"></span>'
      : '<span class="person">' + esc(entry.initial || "?") + "</span>";
    return '<button class="list-item btn-reset" type="button" data-act="plink" data-launch="' + esc(entry.launch) + '">' + avatar +
      '<span class="col"><span class="ellipsis">' + esc(entry.title) + '</span><span class="' + (entry.missed ? "missed" : "faint") +
      '" style="font-size:12px">' + esc(entry.subtitle) + "</span></span></button>";
  }).join("");
  const open = device.launch
    ? '<button class="chip btn-reset" type="button" data-act="plink" data-launch="' + esc(device.launch) + '" style="align-self:flex-start">Открыть «Связь с телефоном»' + svg(12, P.external) + "</button>"
    : "";
  return '<div class="phone-col"><div class="phone-head"><span class="phone-mini" style="' + wall + '"></span><div class="col" style="gap:4px">' +
    '<span class="ellipsis" style="font-size:15px;font-weight:600">' + esc(device.name) + "</span>" +
    '<span class="muted" style="font-size:12px;display:flex;align-items:center;gap:6px"><span class="dot-ok"></span>' + esc(device.status) + "</span>" +
    (glyphs ? '<span class="phone-glyphs">' + glyphs + "</span>" : "") +
    "</div></div>" + open +
    '<div class="block" style="gap:8px"><span class="sec-title">Функции</span><div class="feat-grid">' + features + "</div></div>" +
    '<div class="block" style="flex:1;min-height:0;gap:6px"><span class="sec-title">' + esc(phone.recentHeading || "Недавние") + "</span>" +
    '<div class="list scroll" style="flex:1;min-height:0">' + recent + "</div></div></div>";
}

function paintStart() {
  const had = document.activeElement && document.activeElement.classList && document.activeElement.classList.contains("js-q") && startEl.contains(document.activeElement);
  const caret = had ? document.activeElement.selectionStart : null;
  const box = startBox();
  const needle = localQuery.trim().length > 0;
  let inner;
  if (box.bar) {
    const bodyHtml = needle
      ? '<div style="flex:1;display:flex;min-width:0;min-height:0;padding:16px">' + resultsHtml(true) + "</div>"
      : winList() + winTiles();
    inner = '<div class="start-main" style="width:660px">' + railHtml() + bodyHtml + powerMenu(true) + "</div>";
  } else {
    const view = needle ? "results" : startView;
    const content = view === "results" ? resultsHtml(false) : view === "all" ? allHtml() : homeHtml();
    const fill = view === "home" ? "start-fill home" : "start-fill";
    inner = '<div class="start-main" style="width:600px"><div class="start-body">' + searchRow() +
      '<div class="' + fill + '">' + content + "</div></div>" + footHtml() + powerMenu(false) + "</div>";
  }
  startEl.innerHTML = '<div class="start glass" style="width:' + box.w + "px;height:" + box.h + 'px">' + inner + phoneHtml() + "</div>";
  if (had) {
    const input = startEl.querySelector(".js-q");
    if (input) {
      input.focus();
      const pos = caret == null ? input.value.length : caret;
      try { input.setSelectionRange(pos, pos); } catch (err) { /* the field can be empty */ }
    }
  }
}

function togglePhone() {
  if (!state.phone || !state.phone.device) return;
  railOpen = false;
  const next = !state.phone.visible;
  state.phone.visible = next;
  post({ type: "phone-toggle", visible: next });
  paintStart();
  reportSize();
}

startEl.addEventListener("click", (event) => {
  if (railOpen && !event.target.closest?.(".rail-panel")) {
    closeRail();
  }
  const el = event.target.closest && event.target.closest("[data-act]");
  if (!el || !startEl.contains(el)) {
    if (powerOpen) {
      powerOpen = false;
      paintStart();
    }
    return;
  }
  const act = el.dataset.act;
  if (act === "rail") {
    railOpen = !railOpen;
    powerOpen = false;
    const panel = startEl.querySelector(".rail-panel");
    if (panel) {
      panel.classList.toggle("open", railOpen);
      const btn = panel.querySelector("[data-act='rail']");
      if (btn) btn.setAttribute("aria-expanded", railOpen ? "true" : "false");
    }
    const menu = startEl.querySelector(".power-menu");
    if (menu) menu.remove();
    return;
  }
  if (act === "all") { startView = "all"; powerOpen = false; paintStart(); return; }
  if (act === "home") { startView = "home"; paintStart(); return; }
  if (act === "more") { recMore = !recMore; paintStart(); return; }
  if (act === "tile-edit") { tileEdit = !tileEdit; paintStart(); return; }
  if (act === "unpin") { post({ type: "start-unpin", id: el.dataset.id }); return; }
  if (act === "pin") { post({ type: "start-pin", id: el.dataset.id }); return; }
  if (act === "phone") { togglePhone(); return; }
  if (act === "power-toggle" || act === "rail-power") {
    railOpen = false;
    powerOpen = !powerOpen;
    paintStart();
    return;
  }
  if (act === "power") post({ type: "start-power", action: el.dataset.action });
  else if (act === "launch") post({ type: "start-launch", id: el.dataset.id });
  else if (act === "file") post({ type: "start-file", id: el.dataset.id });
  else if (act === "web") post({ type: "start-launch", id: "web" });
  else if (act === "plink") post({ type: "phone-open", launch: el.dataset.launch || "" });
  else if (act === "shell") {
    railOpen = false;
    post({ type: "start-launch", id: "shell:" + (el.dataset.key || "") });
  }
});

startEl.addEventListener("contextmenu", (event) => {
  const el = event.target.closest && event.target.closest("[data-act='launch']");
  if (!el || !startEl.contains(el)) return;
  event.preventDefault();
  const id = el.dataset.id;
  if (!id) return;
  if (el.dataset.unpin === "1") {
    post({ type: "start-unpin", id });
    return;
  }
  if (el.dataset.pin === "1") {
    post({ type: "start-pin", id });
  }
});

startEl.addEventListener("input", (event) => {
  const input = event.target.closest && event.target.closest(".js-q");
  if (!input) return;
  localQuery = input.value;
  post({ type: "start-search", q: localQuery });
});

startEl.addEventListener("keydown", (event) => {
  if (event.key !== "Enter") return;
  const input = event.target.closest && event.target.closest(".js-q");
  if (!input) return;
  const first = (state.start.results || [])[0];
  if (first) post({ type: first.file ? "start-file" : "start-launch", id: first.id });
  else if (input.value.trim()) post({ type: "start-launch", id: "web" });
});

