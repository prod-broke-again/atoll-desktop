import { spawn } from 'child_process';
import fs from 'fs';
import path from 'path';

const CHROME_PATH = fs.existsSync('C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe')
  ? 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe'
  : 'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe';

const PORT = 9222;

async function sleep(ms) {
  return new Promise((r) => setTimeout(r, ms));
}

async function getJson(url) {
  const res = await fetch(url);
  return await res.json();
}

async function isPortOpen(url) {
  try {
    const res = await fetch(url, { method: 'HEAD' });
    return res.ok || res.status === 404 || res.status === 200;
  } catch {
    return false;
  }
}

async function main() {
  const baseOutDir = path.resolve('assets', 'screenshots');
  const enDir = path.join(baseOutDir, 'en');
  const ruDir = path.join(baseOutDir, 'ru');
  fs.mkdirSync(enDir, { recursive: true });
  fs.mkdirSync(ruDir, { recursive: true });

  // 1. Ensure Vite is running
  let viteProc = null;
  const viteRunning = await isPortOpen('http://localhost:5173');
  if (!viteRunning) {
    console.log('Starting Vite dev server...');
    viteProc = spawn('npm', ['--prefix', 'src/LiveWallpaper.Web', 'run', 'dev'], {
      shell: true,
      stdio: 'pipe',
    });
    for (let i = 0; i < 40; i++) {
      if (await isPortOpen('http://localhost:5173')) {
        console.log('Vite dev server is ready.');
        break;
      }
      await sleep(250);
    }
  }

  // 2. Launch headless Chrome/Edge with remote debugging
  const chromeProc = spawn(CHROME_PATH, [
    `--remote-debugging-port=${PORT}`,
    '--headless=new',
    '--disable-gpu',
    '--no-first-run',
    '--window-size=1920,1080',
    '--user-data-dir=' + path.resolve('temp_chrome_profile'),
    'about:blank',
  ]);

  try {
    let version = null;
    for (let i = 0; i < 30; i++) {
      try {
        version = await getJson(`http://127.0.0.1:${PORT}/json/version`);
        if (version) break;
      } catch {}
      await sleep(200);
    }

    if (!version) throw new Error('Chrome remote debugging did not respond');

    const targets = await getJson(`http://127.0.0.1:${PORT}/json/list`);
    const pageTarget = targets.find((t) => t.type === 'page');
    if (!pageTarget || !pageTarget.webSocketDebuggerUrl) {
      throw new Error('Page target not found');
    }

    const ws = new WebSocket(pageTarget.webSocketDebuggerUrl);
    await new Promise((resolve) => (ws.onopen = resolve));

    let msgId = 1;
    function send(method, params = {}) {
      return new Promise((resolve, reject) => {
        const id = msgId++;
        const onMsg = (e) => {
          const data = JSON.parse(e.data);
          if (data.id === id) {
            ws.removeEventListener('message', onMsg);
            if (data.error) reject(data.error);
            else resolve(data.result);
          }
        };
        ws.addEventListener('message', onMsg);
        ws.send(JSON.stringify({ id, method, params }));
      });
    }

    await send('Page.enable');
    await send('Runtime.enable');

    await send('Emulation.setDeviceMetricsOverride', {
      width: 1920,
      height: 1080,
      deviceScaleFactor: 1,
      mobile: false,
    });

    async function capturePage(targetUrl, outPath, setupFn = null) {
      console.log(`Navigating to ${targetUrl}...`);
      await send('Page.navigate', { url: targetUrl });
      await sleep(2400); // Allow mock data, images, and layout animation to settle

      if (setupFn) {
        await send('Runtime.evaluate', { expression: setupFn });
        await sleep(500);
      }

      const res = await send('Page.captureScreenshot', { format: 'png' });
      const buffer = Buffer.from(res.data, 'base64');
      fs.writeFileSync(outPath, buffer);
      console.log(`Captured: ${outPath} (${(buffer.length / 1024).toFixed(1)} KB)`);
    }

    const layouts = ['fan', 'bento', 'hex', 'diagonal'];

    // Capture English Screenshots
    console.log('\n--- Capturing English Screenshots ---');
    for (const layout of layouts) {
      await capturePage(
        `http://localhost:5173/?lang=en&layout=${layout}`,
        path.join(enDir, `${layout}.png`),
      );
    }
    // English Launcher open
    await capturePage(
      `http://localhost:5173/?lang=en&layout=fan`,
      path.join(enDir, `launcher.png`),
      `
        const tile = document.querySelector('.folder-tile-widget');
        if (tile) tile.click();
      `,
    );

    // Capture Russian Screenshots
    console.log('\n--- Capturing Russian Screenshots ---');
    for (const layout of layouts) {
      await capturePage(
        `http://localhost:5173/?lang=ru&layout=${layout}`,
        path.join(ruDir, `${layout}.png`),
      );
    }
    // Russian Launcher open
    await capturePage(
      `http://localhost:5173/?lang=ru&layout=fan`,
      path.join(ruDir, `launcher.png`),
      `
        const tile = document.querySelector('.folder-tile-widget');
        if (tile) tile.click();
      `,
    );

    // Copy top-level legacy fallbacks
    fs.copyFileSync(path.join(enDir, 'fan.png'), path.join(baseOutDir, 'screenshot_1920_closed.png'));
    fs.copyFileSync(path.join(enDir, 'launcher.png'), path.join(baseOutDir, 'screenshot_1920_open_panel.png'));

    ws.close();
    console.log('\nAll screenshots captured successfully!');
  } finally {
    chromeProc.kill();
    if (viteProc) {
      console.log('Shutting down Vite dev server...');
      viteProc.kill();
    }
    try {
      fs.rmSync(path.resolve('temp_chrome_profile'), { recursive: true, force: true });
    } catch {}
  }
}

main().catch((err) => {
  console.error('Screenshot capture failed:', err);
  process.exit(1);
});
