/** Name prompt — host TopMost dialog on wallpaper (WorkerW can't take focus); in-page fallback for Vite dev. */

type PromptOpts = {
  title: string;
  label?: string;
  initial?: string;
  confirmLabel?: string;
  cancelLabel?: string;
};

type Pending = {
  resolve: (value: string | null) => void;
};

const pending = new Map<string, Pending>();
let seq = 0;

function hasHost(): boolean {
  return !!window.chrome?.webview?.postMessage;
}

export function askName(opts: PromptOpts): Promise<string | null> {
  // Drop any leftover in-page dialog from a previous session (stuck under the desktop layer).
  document.querySelector('.lw-name-dialog-root')?.remove();

  if (hasHost()) {
    return askViaHost(opts);
  }
  return askInPage(opts);
}

export function handlePromptNameResult(message: {
  type: 'prompt_name_result';
  requestId: string;
  value: string | null;
}): void {
  const p = pending.get(message.requestId);
  if (!p) return;
  pending.delete(message.requestId);
  p.resolve(message.value ?? null);
}

function askViaHost(opts: PromptOpts): Promise<string | null> {
  const requestId = `n${++seq}_${Date.now()}`;
  return new Promise((resolve) => {
    pending.set(requestId, { resolve });
    window.chrome!.webview!.postMessage({
      type: 'prompt_name',
      requestId,
      title: opts.title,
      label: opts.label ?? '',
      initial: opts.initial ?? '',
      confirmLabel: opts.confirmLabel ?? 'ОК',
      cancelLabel: opts.cancelLabel ?? 'Отмена',
    });
  });
}

function askInPage(opts: PromptOpts): Promise<string | null> {
  return new Promise((resolve) => {
    const prev = document.querySelector('.lw-name-dialog-root');
    prev?.remove();

    const root = document.createElement('div');
    root.className = 'lw-name-dialog-root';
    root.innerHTML = `
      <div class="lw-name-dialog-backdrop" data-act="cancel"></div>
      <div class="lw-name-dialog" role="dialog" aria-modal="true" aria-labelledby="lw-name-title">
        <h3 id="lw-name-title" class="lw-name-dialog-title">${esc(opts.title)}</h3>
        ${opts.label ? `<label class="lw-name-dialog-label" for="lw-name-input">${esc(opts.label)}</label>` : ''}
        <input id="lw-name-input" class="lw-name-dialog-input" type="text" autocomplete="off" spellcheck="false" />
        <div class="lw-name-dialog-actions">
          <button type="button" class="lw-name-btn ghost" data-act="cancel">${esc(opts.cancelLabel ?? 'Отмена')}</button>
          <button type="button" class="lw-name-btn primary" data-act="ok">${esc(opts.confirmLabel ?? 'ОК')}</button>
        </div>
      </div>`;

    const input = root.querySelector<HTMLInputElement>('#lw-name-input')!;
    input.value = opts.initial ?? '';

    const finish = (value: string | null) => {
      root.remove();
      resolve(value);
    };

    root.addEventListener('click', (e) => {
      const act = (e.target as HTMLElement).closest<HTMLElement>('[data-act]')?.dataset.act;
      if (act === 'cancel') finish(null);
      if (act === 'ok') {
        const v = input.value.trim();
        finish(v.length ? v : null);
      }
    });

    input.addEventListener('keydown', (e) => {
      if (e.key === 'Enter') {
        e.preventDefault();
        const v = input.value.trim();
        finish(v.length ? v : null);
      } else if (e.key === 'Escape') {
        e.preventDefault();
        finish(null);
      }
    });

    document.body.appendChild(root);
    requestAnimationFrame(() => {
      input.focus();
      input.select();
    });
  });
}

function esc(s: string): string {
  return s.replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]!);
}
