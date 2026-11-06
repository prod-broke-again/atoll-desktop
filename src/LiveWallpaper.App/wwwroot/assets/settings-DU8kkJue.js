import{n as e,r as t,t as n}from"./icons-qcR0FQTN.js";var r=e=>window.chrome?.webview?.postMessage(e),i=e=>e.replace(/[&<>"']/g,e=>({"&":`&amp;`,"<":`&lt;`,">":`&gt;`,'"':`&quot;`,"'":`&#39;`})[e]),a=/^[A-Za-z0-9_-]{11}$/;function o(e){let t=e.trim();if(!t)return null;if(a.test(t))return t;try{let e=new URL(t.includes(`://`)?t:`https://${t}`),n=e.hostname.replace(/^www\./i,``).toLowerCase();if(n===`youtu.be`){let t=e.pathname.split(`/`).filter(Boolean)[0];return t&&a.test(t)?t:null}if(n.includes(`youtube`)){let t=e.searchParams.get(`v`);if(t&&a.test(t))return t;let n=e.pathname.split(`/`).filter(Boolean);for(let e=0;e<n.length-1;e++)if([`embed`,`shorts`,`live`,`v`].includes(n[e]))return a.test(n[e+1])?n[e+1]:null}}catch{}return null}function s(e){if(e.type===`youtube`){let t=o(e.value);return t?`https://i.ytimg.com/vi/${t}/hqdefault.jpg`:null}return e.previewUrl?e.previewUrl:e.type===`image`&&/^https?:\/\//i.test(e.value)?e.value:null}function c(e){if(e.type===`youtube`){let t=o(e.value);return t?`YouTube · ${t}`:`YouTube`}let t=e.value.replace(/\\/g,`/`).split(`/`).pop()||e.value;return t.length>42?t.slice(0,39)+`…`:t}function l(e){return e<60?`${e} с`:`${Math.round(e/60)} мин`}var u=[{id:`wallpaper`,label:`Обои`,iconHtml:e(n.wallpaper,18)},{id:`collage`,label:`Коллаж`,iconHtml:e(n.collage,18)},{id:`widgets`,label:`Виджеты`,iconHtml:e(n.widgets,18)},{id:`atmosphere`,label:`Атмосфера`,iconHtml:e(n.atmosphere,18)},{id:`system`,label:`Система`,iconHtml:e(n.system,18)}],d=[{key:`fan`,title:`Веер`,svg:`<rect x="12" y="8" width="14" height="24" rx="2" transform="rotate(-14 19 20)"/><rect x="38" y="8" width="14" height="24" rx="2" transform="rotate(14 45 20)"/><rect x="25" y="6" width="14" height="26" rx="2"/>`},{key:`bento`,title:`Бенто`,svg:`<rect x="4" y="4" width="24" height="18" rx="2"/><rect x="31" y="4" width="13" height="18" rx="2"/><rect x="47" y="4" width="13" height="8" rx="2"/><rect x="47" y="14" width="13" height="8" rx="2"/><rect x="4" y="25" width="15" height="11" rx="2"/><rect x="22" y="25" width="22" height="11" rx="2"/><rect x="47" y="25" width="13" height="11" rx="2"/>`},{key:`masonry`,title:`Плитка`,svg:`<rect x="4" y="2" width="12" height="16" rx="2"/><rect x="4" y="21" width="12" height="17" rx="2"/><rect x="19" y="6" width="12" height="12" rx="2"/><rect x="19" y="21" width="12" height="17" rx="2"/><rect x="34" y="2" width="12" height="20" rx="2"/><rect x="34" y="25" width="12" height="13" rx="2"/><rect x="49" y="8" width="11" height="15" rx="2"/><rect x="49" y="26" width="11" height="12" rx="2"/>`},{key:`polaroid`,title:`Полароиды`,svg:`<rect x="6" y="8" width="16" height="20" rx="1.5" transform="rotate(-8 14 18)"/><rect x="24" y="10" width="16" height="20" rx="1.5" transform="rotate(5 32 20)"/><rect x="42" y="8" width="16" height="20" rx="1.5" transform="rotate(-4 50 18)"/>`},{key:`hex`,title:`Соты`,svg:`<path d="M14 6l7 4v8l-7 4-7-4v-8z"/><path d="M29 6l7 4v8l-7 4-7-4v-8z"/><path d="M44 6l7 4v8l-7 4-7-4v-8z"/><path d="M21.5 19l7 4v8l-7 4-7-4v-8z"/><path d="M36.5 19l7 4v8l-7 4-7-4v-8z"/><path d="M51.5 19l7 4v8l-7 4-7-4v-8z"/>`},{key:`diagonal`,title:`Диагональ`,svg:`<path d="M8 4h10l-6 32H2z"/><path d="M22 4h10l-6 32H16z"/><path d="M36 4h10l-6 32H30z"/><path d="M50 4h10l-6 32H44z"/>`},{key:`film`,title:`Плёнка`,svg:`<rect x="2" y="7" width="60" height="11" rx="1.5" transform="rotate(-8 32 12)"/><rect x="2" y="22" width="60" height="11" rx="1.5" transform="rotate(-8 32 28)"/>`}],f=class{s;saved=``;active=`wallpaper`;pluginEdits={};wpFocus=0;root=document.getElementById(`settings`);constructor(){window.chrome?.webview?.addEventListener(`message`,e=>this.onHost(e.data)),r({type:`settings_get`}),window.chrome?.webview||this.onHost({type:`settings`,data:p})}onHost(e){switch(e.type){case`settings`:this.s=e.data,this.normalize(),this.initPluginEdits(),this.saved=this.serialize(),this.render();break;case`settings_saved`:this.saved=this.serialize(),this.updateDirty(!0);break;case`folder_picked`:this.s.collage.pinsDirectory=e.path,this.s.pinsCount=e.count,this.q(`#pinsPath`).textContent=e.path,this.q(`#pinsCount`).textContent=this.countText(e.count),this.updateDirty();break;case`wallpaper_file_picked`:this.s.wallpaper.sources.push({type:e.mediaType,value:e.path,durationSeconds:0,previewUrl:e.previewUrl??null}),this.wpFocus=this.s.wallpaper.sources.length-1,this.renderWallpaperStage(),this.updateDirty();break;case`geocode_result`:{let t=this.q(`#geoStatus`);e.ok&&e.lat!=null&&e.lon!=null?(this.s.weather.latitude=e.lat,this.s.weather.longitude=e.lon,t.textContent=`Найдено: ${e.name} · ${e.lat.toFixed(2)}, ${e.lon.toFixed(2)}`,t.className=`status ok`,this.updateDirty()):(t.textContent=`Город не найден`,t.className=`status err`),this.q(`#geoBtn`).disabled=!1;break}case`token_status`:this.s.tokenExists=e.exists,this.refreshPluginSecret(`timeweb`,`token`,e.exists);break;case`weeek_token_status`:this.s.weeekTokenExists=e.exists,this.refreshPluginSecret(`today`,`weeekToken`,e.exists);break;case`calendar_ics_status`:this.s.calendarIcsExists=e.exists;break;case`diagnostics_result`:{let t=this.q(`#diag`);t.hidden=!1,t.textContent=e.text,this.q(`#diagBtn`).disabled=!1,this.q(`#diagBtn`).textContent=`Проверить подключение`;break}case`error`:console.warn(e.message)}}normalize(){this.s.wallpaper||(this.s.wallpaper={enabled:!0,youtubeUrl:``,soundEnabled:!1,rotationSeconds:300,shuffle:!1,sources:[]}),this.s.wallpaper.enabled??(this.s.wallpaper.enabled=!0),this.s.wallpaper.sources||(this.s.wallpaper.sources=[]),this.s.collage.enabled??(this.s.collage.enabled=!1),this.s.plugins||(this.s.plugins=[]),this.s.server?.sites||(this.s.server={...this.s.server,sites:[]})}initPluginEdits(){this.pluginEdits={};for(let e of this.s.plugins){let t={};for(let n of e.fields)n.kind===`password`?t[n.id]=``:t[n.id]=n.value??(n.kind!==`toggle`&&``);this.pluginEdits[e.pluginId]=t}}serialize(){return JSON.stringify({...this.s,plugins:this.pluginEdits,server:{sites:this.s.server.sites}})}q(e){return this.root.querySelector(e)}countText(e){let t=e%10,n=e%100;return`${e} ${t===1&&n!==11?`картинка`:t>=2&&t<=4&&(n<12||n>14)?`картинки`:`картинок`}`}toggle(e,t){return`<label class="toggle"><input type="checkbox" id="${e}" ${t?`checked`:``}/><span></span></label>`}row(e,t,n){return`<div class="row"><div class="row-text"><div class="row-title">${e}</div>${t?`<div class="row-sub">${t}</div>`:``}</div>${n}</div>`}pct(e,t){return Math.round(Math.min(1,Math.max(.05,e==null||Number.isNaN(e)?t:e))*100)}navBtn(e,t,n,r){return`<button type="button" class="nav-btn ${r?`active`:``}" data-nav="${e}"><span class="nav-ico">${n}</span><span class="nav-label">${t}</span></button>`}render(){let t=this.s,r=t.plugins.map(t=>this.navBtn(`plugin-${t.pluginId}`,t.displayName,e(n.plugin,18),this.active===`plugin-${t.pluginId}`)).join(``);this.root.innerHTML=`
    <div class="shell">
      <header class="titlebar">
        <div class="titlebar-drag" id="titleDrag">
          <img class="titlebar-mark" src="https://app.livewallpaper/brand-mark.svg" alt="" width="18" height="18"/>
          <span>LiveWallpaper</span>
          <span class="titlebar-chip">настройки</span>
        </div>
        <div class="win-controls">
          <button type="button" class="win-btn" id="winMin" title="Свернуть">&#8211;</button>
          <button type="button" class="win-btn" id="winMax" title="Развернуть">&#9633;</button>
          <button type="button" class="win-btn close" id="winClose" title="Закрыть">&#10005;</button>
        </div>
      </header>
      <div class="app">
        <aside class="side">
          <div class="brand">
            <img src="https://app.livewallpaper/brand-mark.svg" alt="" width="32" height="32"/>
            <div><b>LiveWallpaper</b><small>Пульт рабочего стола</small></div>
          </div>
          <nav class="nav" aria-label="Разделы">
            <div class="nav-group-label">Разделы</div>
            ${u.map(e=>this.navBtn(e.id,e.label,e.iconHtml,this.active===e.id)).join(``)}
            ${t.plugins.length?`<div class="nav-group-label">Плагины</div>${r}`:``}
          </nav>
          <div class="side-foot">
            <div class="hotkey"><kbd>Win</kbd><kbd>Alt</kbd><kbd>W</kbd><span>коллаж</span></div>
            <div class="hotkey"><kbd>Win</kbd><kbd>Alt</kbd><kbd>P</kbd><span>пауза</span></div>
            <div class="hotkey"><kbd>Win</kbd><kbd>Alt</kbd><kbd>H</kbd><span>приватность</span></div>
          </div>
        </aside>
        <main class="main">
          <div class="panels">
            ${this.panelWallpaper()}
            ${this.panelCollage()}
            ${this.panelWidgets()}
            ${this.panelAtmosphere()}
            ${this.panelSystem()}
            ${t.plugins.map(e=>this.panelPlugin(e)).join(``)}
          </div>
          <footer class="foot">
            <span class="dirty" id="dirty"><i></i><span>Изменений нет</span></span>
            <button class="btn ghost" id="close">Закрыть</button>
            <button class="btn primary" id="save">Сохранить</button>
          </footer>
        </main>
      </div>
    </div>`,this.showPanel(this.active),this.renderWallpaperStage(),this.renderSites(),this.bind(),this.updateDirty()}panel(e,t,n,r,i){let a=i?`<span class="panel-ico">${i}</span>`:``;return`<section class="panel ${e===`wallpaper`?`panel-wide`:``} ${this.active===e?`active`:``}" id="panel-${e}" data-panel="${e}"><header class="panel-head">${a}<div><h2>${t}</h2><p class="lead">${n}</p></div></header>${r}</section>`}panelWallpaper(){let r=this.s.wallpaper,i=r.enabled===!1?` is-disabled`:``;return this.panel(`wallpaper`,`Обои`,`Свой плейлист фото/видео/YouTube. Выключено — фон как у Windows.`,`
      <div class="card">${this.row(`Свои обои`,`Выключено — текущие обои Windows 11`,this.toggle(`wpEnabled`,r.enabled!==!1))}</div>
      <div class="wp-body${i}">
      <div class="wp-stage" id="wpStage"></div>
      <div class="wp-toolbar">
        <div class="wp-add">
          <input class="input grow" id="wpYtInput" placeholder="Ссылка YouTube" spellcheck="false"/>
          <button class="btn" id="wpAddYt" type="button">${t(`plus`,16)} Добавить</button>
        </div>
        <div class="wp-add-files">
          <button class="btn" id="wpAddImage" type="button">${t(`wallpaper`,16)} Фото</button>
          <button class="btn" id="wpAddVideo" type="button">${t(`monitor`,16)} Видео</button>
        </div>
      </div>
      <div class="card wp-cycle">
        <div class="row col">
          <div class="row-text">
            <div class="row-title">Интервал смены</div>
            <div class="row-sub">Если у кадра своё время = 0, берётся это значение</div>
          </div>
          <div class="range">
            <input type="range" id="wpRotation" min="15" max="3600" step="15" value="${r.rotationSeconds}"/>
            <output id="wpRotationOut">${l(r.rotationSeconds)}</output>
          </div>
        </div>
        ${this.row(`Перемешивать`,`Новый порядок после полного цикла`,this.toggle(`wpShuffle`,r.shuffle))}
        ${this.row(`Звук`,`YouTube и локальное видео`,this.toggle(`wpSound`,r.soundEnabled))}
      </div>
      </div>`,e(n.wallpaper,22))}panelCollage(){let r=this.s.collage,a=r.enabled?``:` is-disabled`;return this.panel(`collage`,`Коллаж`,`Пины поверх обоев (своих или Windows).`,`
      <div class="card">${this.row(`Коллаж на рабочем столе`,`Рисуется поверх обоев, не вместо них`,this.toggle(`collageEnabled`,r.enabled))}</div>
      <div class="card collage-body${a}">
        <div class="row">
          <div class="row-text"><div class="row-title">Папка с картинками</div><div class="row-sub path" id="pinsPath">${i(r.pinsDirectory||`не выбрана`)}</div></div>
          <span class="status" id="pinsCount">${this.countText(this.s.pinsCount)}</span>
          <button class="btn" id="pickFolder" type="button">${t(`folderOpen`,16)} Выбрать…</button>
        </div>
      </div>
      <div class="card collage-body${a}">
        <div class="row col">
          <div class="row-text"><div class="row-title">Раскладка</div></div>
          <div class="layouts ${r.shuffleStyle?`disabled`:``}" id="layouts">
            ${d.map(e=>`<button type="button" class="layout ${e.key===r.style?`active`:``}" data-layout="${e.key}"><svg class="layout-diagram" viewBox="0 0 64 40" fill="none" stroke="currentColor" stroke-width="1.4" stroke-linecap="round" stroke-linejoin="round">${e.svg}</svg>${e.title}</button>`).join(``)}
          </div>
        </div>
        ${this.row(`Случайная раскладка`,`Менять раскладку вместе с картинками`,this.toggle(`shuffle`,r.shuffleStyle))}
      </div>
      <div class="card collage-body${a}">
        <div class="row col">
          <div class="row-text"><div class="row-title">Смена картинок</div></div>
          <div class="range"><input type="range" id="interval" min="1" max="60" value="${r.intervalMinutes}"/><output id="intervalOut">${r.intervalMinutes} мин</output></div>
        </div>
        ${this.row(`Гармония цветов`,`Подбирать картинки близких оттенков`,this.toggle(`harmony`,r.enableColorHarmony))}
      </div>`,e(n.collage,22))}panelWidgets(){let t=this.s;return this.panel(`widgets`,`Виджеты`,`Часы, погода и лаунчер на рабочем столе.`,`
      <div class="card">
        ${this.row(`Часы`,`Только время и дата`,this.toggle(`clock`,t.widgets.showClock))}
        <div class="row col">
          <div class="row-text"><div class="row-title">Расположение часов</div></div>
          <div class="inline" style="gap:8px; margin-top:8px">
            <div class="seg" id="clockPosV">${[[`top`,`Сверху`],[`bottom`,`Снизу`]].map(([e,n])=>`<button type="button" data-cposv="${e}" class="${t.widgets.clockPosition?.startsWith(e)?`active`:``}">${n}</button>`).join(``)}</div>
            <div class="seg" id="clockPosH">${[[`left`,`Слева`],[`center`,`По центру`],[`right`,`Справа`]].map(([e,n])=>`<button type="button" data-cposh="${e}" class="${t.widgets.clockPosition?.endsWith(e)?`active`:``}">${n}</button>`).join(``)}</div>
          </div>
        </div>
        <div class="row">
          <div class="row-text"><div class="row-title">Шрифт часов</div></div>
          <select class="input" id="clockFont" style="width:200px">
            ${[[`default`,`По умолчанию (Segoe)`],[`inter`,`Inter`],[`manrope`,`Manrope`],[`mono`,`JetBrains Mono`],[`serif`,`Playfair Display`]].map(([e,n])=>`<option value="${e}" ${t.widgets.clockFont===e?`selected`:``}>${n}</option>`).join(``)}
          </select>
        </div>
        ${this.row(`Погода`,``,this.toggle(`weather`,t.weather.enabled))}
        <div class="row">
          <div class="row-text"><div class="row-title">Город</div><div class="status" id="geoStatus">${t.weather.latitude||t.weather.longitude?`${t.weather.latitude.toFixed(2)}, ${t.weather.longitude.toFixed(2)}`:``}</div></div>
          <input class="input" id="city" value="${i(t.weather.city)}" style="width:200px" placeholder="Например, Тбилиси"/>
          <button class="btn" id="geoBtn" type="button">Найти</button>
        </div>
      </div>
      <div class="card">
        ${this.row(`Папки-лаунчер`,`Папки и файлы с рабочего стола Windows`,this.toggle(`launcher`,t.launcher.enabled))}
        ${this.row(`Скрывать значки Windows`,`Прячет иконки Explorer; лаунчер показывает свои плитки`,this.toggle(`hideIcons`,t.launcher.hideDesktopIcons))}
      </div>`,e(n.widgets,22))}panelAtmosphere(){let t=this.s;return this.panel(`atmosphere`,`Атмосфера`,`Режимы, сигналы и единая палитра сетапа.`,`
      <div class="card">
        <div class="row col">
          <div class="row-text"><div class="row-title">Режим работы</div><div class="row-sub">Авто по приложениям и времени или фиксированный.</div></div>
          <div class="seg" id="modeSeg" style="margin-top:6px">
            <button type="button" data-mode="auto" class="${t.context.mode===`auto`?`active`:``}">Авто</button>
            <button type="button" data-mode="work" class="${t.context.mode===`work`?`active`:``}">Рабочий</button>
            <button type="button" data-mode="game" class="${t.context.mode===`game`?`active`:``}">Игровой</button>
            <button type="button" data-mode="night" class="${t.context.mode===`night`?`active`:``}">Ночной</button>
          </div>
        </div>
      </div>
      <div class="card">
        ${this.row(`Красные края при сбое`,`Пульс по периметру при падении сайта или сервера`,this.toggle(`atmProd`,t.atmosphere.enableProdAlert))}
        ${this.row(`Тёплый фон перед созвоном`,`За 15 мин до встречи`,this.toggle(`atmCall`,t.atmosphere.enableUpcomingCall))}
        ${this.row(`Янтарная точка при низком балансе`,`Timeweb Cloud`,this.toggle(`atmBal`,t.atmosphere.enableLowBalanceBeacon))}
      </div>
      <div class="card">
        <div class="row col">
          <div class="inline" style="gap:8px; flex-wrap:wrap">
            <button class="btn" id="testProdBtn" type="button">Красные края</button>
            <button class="btn" id="testCallBtn" type="button">Тёплый фон</button>
            <button class="btn" id="testBalBtn" type="button">Янтарная точка</button>
          </div>
        </div>
      </div>
      <div class="card">
        ${this.row(`Акцент Windows`,`Окна и панель задач`,this.toggle(`palWin`,t.setupPalette?.syncWindowsAccent??!0))}
        ${this.row(`Тема Windows Terminal`,``,this.toggle(`palTerm`,t.setupPalette?.syncWindowsTerminal??!0))}
        ${this.row(`Redragon (HID)`,``,this.toggle(`palRedragon`,t.setupPalette?.syncRedragonHid??!0))}
        <div class="row col">
          <div class="row-text"><div class="row-title">Яркость клавиатуры</div></div>
          <div class="range"><input type="range" id="kbBright" min="5" max="100" value="${this.pct(t.setupPalette?.keyboardIntensity,.85)}"/><output id="kbBrightOut">${this.pct(t.setupPalette?.keyboardIntensity,.85)}%</output></div>
        </div>
        <div class="row col">
          <div class="row-text"><div class="row-title">Яркость ночью</div></div>
          <div class="range"><input type="range" id="kbNight" min="5" max="100" value="${this.pct(t.setupPalette?.nightKeyboardIntensity,.4)}"/><output id="kbNightOut">${this.pct(t.setupPalette?.nightKeyboardIntensity,.4)}%</output></div>
        </div>
        ${this.row(`OpenRGB`,``,this.toggle(`palRgb`,t.setupPalette?.syncOpenRgb??!1))}
        ${this.row(`Razer Chroma`,``,this.toggle(`palRazer`,t.setupPalette?.syncRazerChroma??!1))}
        ${this.row(`Windows — не чаще`,``,`<div class="seg" id="palInterval">${[10,20,60].map(e=>`<button type="button" data-palint="${e}" class="${(t.setupPalette?.desktopMinIntervalMinutes??20)===e?`active`:``}">${e} мин</button>`).join(``)}</div>`)}
        <div class="row">
          <div class="row-text"><div class="row-title">Текущий акцент</div><div class="row-sub" id="accentHex">${i(t.setupPalette?.currentAccentHex||`#6366F1`)}</div></div>
          <div class="palette-swatch" id="accentSwatch" style="background:${i(t.setupPalette?.currentAccentHex||`#6366F1`)}"></div>
          <button class="btn" id="applyPaletteBtn" type="button">Применить сейчас</button>
        </div>
      </div>`,e(n.atmosphere,22))}panelSystem(){let t=this.s;return this.panel(`system`,`Система`,`Производительность, панель и запуск.`,`
      <div class="card">
        ${this.row(`Пауза в играх`,`При полноэкранных приложениях`,this.toggle(`pauseFs`,t.performance.autoPauseFullscreen))}
        ${this.row(`Пауза от батареи`,``,this.toggle(`pauseBat`,t.performance.autoPauseBattery))}
        ${this.row(`FPS в простое`,``,`<div class="seg" id="fps">${[30,45,60].map(e=>`<button type="button" data-fps="${e}" class="${t.performance.fpsIdle===e?`active`:``}">${e}</button>`).join(``)}</div>`)}
        ${this.row(`Своя панель задач (Бета)`,`Экспериментальная замена панели Windows`,this.toggle(`taskbar`,t.taskbar?.enabled??!1))}
        ${this.row(`Автозапуск`,`Вместе с Windows`,this.toggle(`autostart`,t.autostart))}
        <div class="row">
          <div class="row-text"><div class="row-title">Обновление</div><div class="row-sub">Пересобрать обои и перезапустить</div></div>
          <button class="btn" id="updateBtn" type="button">Обновить</button>
        </div>
      </div>`,e(n.system,22))}panelPlugin(r){let i=`plugin-${r.pluginId}`,a=r.fields.map(e=>this.renderPluginField(r.pluginId,e)).join(``),o=r.pluginId===`server`?`<div class="card"><div class="row col"><div class="row-text"><div class="row-title">Сайты</div><div class="row-sub">Проверка доступности и SSL</div></div><div class="sites" id="sites"></div><button class="btn" id="addSite" type="button">${t(`plus`,16)} Добавить сайт</button></div>
           <div class="row col"><button class="btn" id="diagBtn" type="button">Проверить подключение</button><pre class="diag" id="diag" hidden></pre></div></div>`:``,s=r.pluginId===`system`?n.system:r.pluginId===`server`?n.gauge:(r.pluginId,n.plugin);return this.panel(i,r.displayName,r.description||``,`<div class="card">${a}</div>${o}`,e(s,22))}renderPluginField(e,t){let n=this.pluginEdits[e]?.[t.id];if(t.kind===`toggle`)return this.row(t.label,t.hint??``,`<label class="toggle"><input type="checkbox" data-pf="${e}" data-fid="${t.id}" ${n?`checked`:``}/><span></span></label>`);if(t.kind===`password`){let n=t.secretExists?`<span class="status ok">Сохранён</span>`:`<span class="status">Не задан</span>`;return`<div class="row col"><div class="row-text"><div class="row-title">${i(t.label)}</div>${t.hint?`<div class="row-sub">${i(t.hint)}</div>`:``}${n}</div>
        <div class="inline"><input class="input" type="password" data-pf="${e}" data-fid="${t.id}" placeholder="новое значение" autocomplete="off" style="width:220px"/>
        <button type="button" class="btn ghost danger" data-pf-del="${e}" data-fid="${t.id}">Удалить</button></div></div>`}let r=t.kind===`number`?`number`:`text`;return`<div class="row"><div class="row-text"><div class="row-title">${i(t.label)}</div>${t.hint?`<div class="row-sub">${i(t.hint)}</div>`:``}</div>
      <input class="input" type="${r}" data-pf="${e}" data-fid="${t.id}" value="${i(String(n??``))}" style="width:260px"/></div>`}renderWallpaperStage(){let e=this.q(`#wpStage`);if(!e)return;let n=this.s.wallpaper.sources;if(n.length===0){this.wpFocus=0,e.innerHTML=`
        <div class="wp-preview is-empty">
          <div class="wp-preview-frame">
            <div class="wp-preview-empty">
              ${t(`monitor`,36,`wp-preview-empty-ico`)}
              <span class="wp-preview-empty-title">Нет кадра</span>
              <span class="wp-preview-empty-sub">Добавьте фото, видео или ссылку YouTube — оно станет фоном стола</span>
            </div>
            <div class="wp-timer" aria-hidden="true"><i style="width:0%"></i></div>
          </div>
        </div>
        <div class="wp-rail empty-rail">Плейлист пуст</div>`;return}this.wpFocus>=n.length&&(this.wpFocus=n.length-1),this.wpFocus<0&&(this.wpFocus=0);let r=n[this.wpFocus],a=r.durationSeconds>0?r.durationSeconds:this.s.wallpaper.rotationSeconds,o=s(r),u={youtube:`YouTube`,video:`Видео`,image:`Фото`}[r.type]??r.type;e.innerHTML=`
      <div class="wp-preview">
        <div class="wp-preview-frame">
          ${o?`<img class="wp-preview-img" src="${i(o)}" alt="" decoding="async"/>`:`<div class="wp-preview-fallback" data-kind="${i(r.type)}"><span>${i(u)}</span></div>`}
          <div class="wp-preview-meta">
            <span class="wp-preview-kind">${i(u)}</span>
            <span class="wp-preview-name" title="${i(r.value)}">${i(c(r))}</span>
            <span class="wp-preview-dwell">${l(a)}</span>
          </div>
          <div class="wp-timer" title="Интервал этого кадра"><i style="width:${Math.min(100,a/Math.max(this.s.wallpaper.rotationSeconds,1)*100)}%"></i></div>
        </div>
        <div class="wp-preview-actions">
          <label class="wp-dwell-field">
            <span>Своё время, с</span>
            <input class="input wp-dur" type="number" min="0" max="86400" data-wp-dur="${this.wpFocus}" value="${r.durationSeconds||0}"/>
          </label>
          <button type="button" class="btn ghost" data-wp-up="${this.wpFocus}" ${this.wpFocus<=0?`disabled`:``}>${t(`chevronUp`,16)} Выше</button>
          <button type="button" class="btn ghost" data-wp-down="${this.wpFocus}" ${this.wpFocus>=n.length-1?`disabled`:``}>${t(`chevronDown`,16)} Ниже</button>
          <button type="button" class="btn ghost danger" data-wp-del="${this.wpFocus}">${t(`trash`,16)} Убрать</button>
        </div>
      </div>
      <div class="wp-rail" id="wpRail" role="list">
        ${n.map((e,t)=>{let n=s(e),r={youtube:`YouTube`,video:`Видео`,image:`Фото`}[e.type]??e.type;return`<button type="button" class="wp-thumb ${t===this.wpFocus?`is-active`:``}" data-wp-focus="${t}" role="listitem" title="${i(c(e))}">
              ${n?`<img src="${i(n)}" alt="" decoding="async" loading="lazy"/>`:`<span class="wp-thumb-fallback">${i(r.slice(0,1))}</span>`}
              <span class="wp-thumb-cap">${i(r)}</span>
            </button>`}).join(``)}
      </div>`}renderSites(){let e=this.q(`#sites`);e&&(e.innerHTML=this.s.server.sites.length?this.s.server.sites.map((e,t)=>`<div class="site">
          <input class="input" data-site-name="${t}" value="${i(e.name)}" placeholder="Название"/>
          <input class="input" data-site-url="${t}" value="${i(e.url)}" placeholder="https://"/>
          <button class="btn ghost danger" data-site-del="${t}" type="button">✕</button></div>`).join(``):`<div class="empty">Сайтов нет — добавь хотя бы прод.</div>`)}showPanel(e){this.active=e,this.root.querySelectorAll(`.panel`).forEach(t=>t.classList.toggle(`active`,t.dataset.panel===e)),this.root.querySelectorAll(`[data-nav]`).forEach(t=>t.classList.toggle(`active`,t.dataset.nav===e))}refreshPluginSecret(e,t,n){let r=this.s.plugins.find(t=>t.pluginId===e)?.fields.find(e=>e.id===t);r&&(r.secretExists=n),this.active===`plugin-${e}`&&this.render()}bind(){let e=this.s,t=(e,t)=>this.q(`#${e}`)?.addEventListener(`change`,e=>{t(e.target.checked),this.updateDirty()});this.q(`#titleDrag`)?.addEventListener(`mousedown`,e=>{e.target.closest(`.win-controls`)||r({type:`settings_drag`})}),this.q(`#winMin`)?.addEventListener(`click`,()=>r({type:`settings_minimize`})),this.q(`#winMax`)?.addEventListener(`click`,()=>r({type:`settings_maximize`})),this.q(`#winClose`)?.addEventListener(`click`,()=>r({type:`settings_close`})),this.q(`#close`)?.addEventListener(`click`,()=>r({type:`settings_close`})),this.root.querySelectorAll(`[data-nav]`).forEach(e=>e.addEventListener(`click`,()=>{this.showPanel(e.dataset.nav),e.dataset.nav===`plugin-server`&&(this.renderSites(),this.bindSites())})),t(`collageEnabled`,t=>{e.collage.enabled=t,this.root.querySelectorAll(`.collage-body`).forEach(e=>e.classList.toggle(`is-disabled`,!t)),this.updateDirty()}),this.q(`#pickFolder`)?.addEventListener(`click`,()=>r({type:`pick_folder`})),this.root.querySelectorAll(`[data-layout]`).forEach(t=>t.addEventListener(`click`,()=>{e.collage.style=t.dataset.layout,e.collage.shuffleStyle=!1,this.q(`#shuffle`).checked=!1,this.q(`#layouts`)?.classList.remove(`disabled`),this.root.querySelectorAll(`[data-layout]`).forEach(e=>e.classList.toggle(`active`,e===t)),this.updateDirty()})),t(`shuffle`,t=>{e.collage.shuffleStyle=t,this.q(`#layouts`)?.classList.toggle(`disabled`,t)});let n=this.q(`#interval`);n?.addEventListener(`input`,()=>{e.collage.intervalMinutes=Number(n.value),this.q(`#intervalOut`).textContent=`${n.value} мин`,this.updateDirty()}),t(`harmony`,t=>e.collage.enableColorHarmony=t),this.q(`#wpAddYt`)?.addEventListener(`click`,()=>{let t=this.q(`#wpYtInput`),n=t.value.trim();n&&(e.wallpaper.sources.push({type:`youtube`,value:n,durationSeconds:0}),t.value=``,this.wpFocus=e.wallpaper.sources.length-1,this.renderWallpaperStage(),this.bindWallpaperList(),this.updateDirty())}),this.q(`#wpYtInput`)?.addEventListener(`keydown`,e=>{e.key===`Enter`&&this.q(`#wpAddYt`)?.click()}),this.q(`#wpAddVideo`)?.addEventListener(`click`,()=>r({type:`pick_wallpaper_video`})),this.q(`#wpAddImage`)?.addEventListener(`click`,()=>r({type:`pick_wallpaper_image`})),t(`wpEnabled`,t=>{e.wallpaper.enabled=t,this.render(),this.bind(),this.updateDirty()}),t(`wpShuffle`,t=>e.wallpaper.shuffle=t),t(`wpSound`,t=>e.wallpaper.soundEnabled=t);let i=this.q(`#wpRotation`);i?.addEventListener(`input`,()=>{e.wallpaper.rotationSeconds=Number(i.value),this.q(`#wpRotationOut`).textContent=l(Number(i.value)),this.renderWallpaperStage(),this.bindWallpaperList(),this.updateDirty()}),this.bindWallpaperList(),t(`clock`,t=>e.widgets.showClock=t),e.widgets.clockPosition||=`top-center`;let a=(t,n)=>{e.widgets.clockPosition=`${t}-${n}`,this.updateDirty()};this.root.querySelectorAll(`[data-cposv]`).forEach(t=>t.addEventListener(`click`,()=>{let n=e.widgets.clockPosition.split(`-`)[1]||`center`;this.root.querySelectorAll(`[data-cposv]`).forEach(e=>e.classList.toggle(`active`,e===t)),a(t.dataset.cposv,n)})),this.root.querySelectorAll(`[data-cposh]`).forEach(t=>t.addEventListener(`click`,()=>{let n=e.widgets.clockPosition.split(`-`)[0]||`top`;this.root.querySelectorAll(`[data-cposh]`).forEach(e=>e.classList.toggle(`active`,e===t)),a(n,t.dataset.cposh)})),this.q(`#clockFont`)?.addEventListener(`change`,t=>{e.widgets.clockFont=t.target.value,this.updateDirty()}),t(`weather`,t=>e.weather.enabled=t),this.q(`#city`)?.addEventListener(`input`,t=>{e.weather.city=t.target.value,this.updateDirty()}),this.q(`#geoBtn`)?.addEventListener(`click`,()=>{this.q(`#geoBtn`).disabled=!0,this.q(`#geoStatus`).textContent=`Поиск…`,r({type:`geocode`,city:e.weather.city})}),t(`launcher`,t=>e.launcher.enabled=t),t(`hideIcons`,t=>e.launcher.hideDesktopIcons=t),this.root.querySelectorAll(`[data-mode]`).forEach(t=>t.addEventListener(`click`,()=>{e.context.mode=t.dataset.mode,this.root.querySelectorAll(`[data-mode]`).forEach(e=>e.classList.toggle(`active`,e===t)),this.updateDirty(),r({type:`set_mode`,mode:e.context.mode})})),t(`atmProd`,t=>e.atmosphere.enableProdAlert=t),t(`atmCall`,t=>e.atmosphere.enableUpcomingCall=t),t(`atmBal`,t=>e.atmosphere.enableLowBalanceBeacon=t);let o=!1;this.q(`#testProdBtn`)?.addEventListener(`click`,()=>{o=!o,this.q(`#testProdBtn`).classList.toggle(`test-active-prod`,o),r({type:`test_signal`,signal:`prod`,active:o})});let s=!1;this.q(`#testCallBtn`)?.addEventListener(`click`,()=>{s=!s,this.q(`#testCallBtn`).classList.toggle(`test-active-call`,s),r({type:`test_signal`,signal:`call`,active:s})});let c=!1;this.q(`#testBalBtn`)?.addEventListener(`click`,()=>{c=!c,this.q(`#testBalBtn`).classList.toggle(`test-active-bal`,c),r({type:`test_signal`,signal:`balance`,active:c})}),e.setupPalette||={enabled:!0,syncWindowsAccent:!0,syncWindowsTerminal:!0,syncRedragonHid:!0,syncOpenRgb:!1,syncRazerChroma:!1,currentAccentHex:`#6366F1`},t(`palWin`,t=>e.setupPalette.syncWindowsAccent=t),t(`palTerm`,t=>e.setupPalette.syncWindowsTerminal=t),t(`palRedragon`,t=>e.setupPalette.syncRedragonHid=t),t(`palRgb`,t=>e.setupPalette.syncOpenRgb=t),t(`palRazer`,t=>e.setupPalette.syncRazerChroma=t),this.root.querySelectorAll(`[data-palint]`).forEach(t=>t.addEventListener(`click`,()=>{e.setupPalette.desktopMinIntervalMinutes=Number(t.dataset.palint),this.root.querySelectorAll(`[data-palint]`).forEach(e=>e.classList.toggle(`active`,e===t)),this.updateDirty()}));let u=(t,n)=>{let r=this.q(`#`+t);r?.addEventListener(`input`,()=>{e.setupPalette[n]=Number(r.value)/100,this.q(`#`+t+`Out`).textContent=`${r.value}%`,this.updateDirty()})};u(`kbBright`,`keyboardIntensity`),u(`kbNight`,`nightKeyboardIntensity`),this.q(`#applyPaletteBtn`)?.addEventListener(`click`,()=>r({type:`apply_palette`})),t(`pauseFs`,t=>e.performance.autoPauseFullscreen=t),t(`pauseBat`,t=>e.performance.autoPauseBattery=t),this.root.querySelectorAll(`[data-fps]`).forEach(t=>t.addEventListener(`click`,()=>{e.performance.fpsIdle=Number(t.dataset.fps),this.root.querySelectorAll(`[data-fps]`).forEach(e=>e.classList.toggle(`active`,e===t)),this.updateDirty()})),t(`taskbar`,t=>e.taskbar.enabled=t),this.q(`#updateBtn`)?.addEventListener(`click`,()=>{r({type:`update_app`})}),t(`autostart`,t=>e.autostart=t),this.root.querySelectorAll(`[data-pf]`).forEach(e=>{let t=e.dataset.pf,n=e.dataset.fid;e.addEventListener(`input`,()=>{let r=e;this.pluginEdits[t][n]=r.type===`checkbox`?r.checked:r.type===`number`?Number(r.value):r.value,this.updateDirty()}),e.addEventListener(`change`,()=>{let r=e;r.type===`checkbox`&&(this.pluginEdits[t][n]=r.checked),this.updateDirty()})}),this.root.querySelectorAll(`[data-pf-del]`).forEach(e=>e.addEventListener(`click`,()=>{let t=e.dataset.pfDel,n=e.dataset.fid;t===`timeweb`&&n===`token`?r({type:`token_delete`}):t===`today`&&n===`weeekToken`?r({type:`weeek_token_delete`}):t===`today`&&n===`googleCalendarIcsUrl`&&r({type:`calendar_ics_delete`})})),this.bindSites(),this.q(`#diagBtn`)?.addEventListener(`click`,()=>{this.q(`#diagBtn`).disabled=!0,this.q(`#diagBtn`).textContent=`Проверяю…`,r({type:`diagnostics`})}),this.q(`#save`)?.addEventListener(`click`,()=>{r({type:`settings_save`,data:{collage:e.collage,weather:e.weather,performance:e.performance,launcher:e.launcher,server:{sites:e.server.sites.filter(e=>e.name.trim()&&/^https?:\/\/.+/.test(e.url.trim()))},widgets:e.widgets,context:e.context,atmosphere:e.atmosphere,setupPalette:e.setupPalette,taskbar:e.taskbar,wallpaper:e.wallpaper,autostart:e.autostart,plugins:this.pluginEdits}})})}bindWallpaperList(){let e=this.q(`#wpStage`);e&&(e.querySelectorAll(`[data-wp-focus]`).forEach(e=>e.addEventListener(`click`,()=>{this.wpFocus=+e.dataset.wpFocus,this.renderWallpaperStage(),this.bindWallpaperList()})),e.querySelectorAll(`[data-wp-dur]`).forEach(e=>e.addEventListener(`change`,()=>{let t=+e.dataset.wpDur;this.s.wallpaper.sources[t].durationSeconds=Math.max(0,Number(e.value)||0),this.renderWallpaperStage(),this.bindWallpaperList(),this.updateDirty()})),e.querySelectorAll(`[data-wp-del]`).forEach(e=>e.addEventListener(`click`,()=>{let t=+e.dataset.wpDel;this.s.wallpaper.sources.splice(t,1),this.wpFocus>=this.s.wallpaper.sources.length&&(this.wpFocus=Math.max(0,this.s.wallpaper.sources.length-1)),this.renderWallpaperStage(),this.bindWallpaperList(),this.updateDirty()})),e.querySelectorAll(`[data-wp-up]`).forEach(e=>e.addEventListener(`click`,()=>{let t=+e.dataset.wpUp;if(t<=0)return;let n=this.s.wallpaper.sources;[n[t-1],n[t]]=[n[t],n[t-1]],this.wpFocus=t-1,this.renderWallpaperStage(),this.bindWallpaperList(),this.updateDirty()})),e.querySelectorAll(`[data-wp-down]`).forEach(e=>e.addEventListener(`click`,()=>{let t=+e.dataset.wpDown,n=this.s.wallpaper.sources;t>=n.length-1||([n[t],n[t+1]]=[n[t+1],n[t]],this.wpFocus=t+1,this.renderWallpaperStage(),this.bindWallpaperList(),this.updateDirty())})))}bindSites(){let e=this.q(`#sites`);e&&(e.addEventListener(`input`,e=>{let t=e.target;t.dataset.siteName&&(this.s.server.sites[+t.dataset.siteName].name=t.value),t.dataset.siteUrl&&(this.s.server.sites[+t.dataset.siteUrl].url=t.value),this.updateDirty()}),e.addEventListener(`click`,e=>{let t=e.target.closest(`[data-site-del]`);t&&(this.s.server.sites.splice(+t.dataset.siteDel,1),this.renderSites(),this.updateDirty())}),this.q(`#addSite`)?.addEventListener(`click`,()=>{this.s.server.sites.push({name:``,url:`https://`}),this.renderSites(),this.updateDirty()}))}updateDirty(e=!1){let t=this.q(`#dirty`);if(!t)return;let n=this.serialize()!==this.saved;t.className=`dirty ${n?`on`:e?`saved`:``}`,t.querySelector(`span`).textContent=n?`Есть несохранённые изменения`:e?`Сохранено`:`Изменений нет`,this.q(`#save`).disabled=!n}},p={collage:{enabled:!0,pinsDirectory:`C:\\Users\\Username\\Pictures\\Pins`,style:`fan`,shuffleStyle:!1,intervalMinutes:5,enableColorHarmony:!0},pinsCount:0,weather:{enabled:!0,city:`Москва`,latitude:55.7558,longitude:37.6173},performance:{autoPauseFullscreen:!0,autoPauseBattery:!0,fpsIdle:30},launcher:{enabled:!0,hideDesktopIcons:!0},server:{enabled:!0,sshAlias:`myserver`,timewebEnabled:!0,sites:[{name:`api.example.com`,url:`https://api.example.com`}]},widgets:{showClock:!0,clockPosition:`top-center`,clockFont:`default`},context:{mode:`auto`},atmosphere:{enabled:!0,enableProdAlert:!0,enableUpcomingCall:!0,enableLowBalanceBeacon:!0},setupPalette:{enabled:!0,syncWindowsAccent:!0,syncWindowsTerminal:!0,syncRedragonHid:!0,syncOpenRgb:!1,syncRazerChroma:!1,currentAccentHex:`#6366F1`,keyboardIntensity:.85,nightKeyboardIntensity:.4},taskbar:{enabled:!1},wallpaper:{enabled:!0,youtubeUrl:``,soundEnabled:!1,rotationSeconds:300,shuffle:!1,sources:[]},plugins:[{pluginId:`telegram`,displayName:`Telegram`,description:``,fields:[{id:`enabled`,label:`Включить`,kind:`toggle`,value:!0}]}],autostart:!0,tokenExists:!0,weeekTokenExists:!1,calendarIcsExists:!1};new f;