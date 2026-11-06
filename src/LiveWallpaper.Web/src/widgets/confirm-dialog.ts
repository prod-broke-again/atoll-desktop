/** Host MessageBox on wallpaper; window.confirm fallback for Vite dev. */

type Pending = { resolve: (ok: boolean) => void };
const pending = new Map<string, Pending>();
let seq = 0;

export function askConfirm(message: string, title = 'Подтверждение'): Promise<boolean> {
  if (window.chrome?.webview?.postMessage) {
    const requestId = `c${++seq}_${Date.now()}`;
    return new Promise((resolve) => {
      pending.set(requestId, { resolve });
      window.chrome!.webview!.postMessage({ type: 'confirm', requestId, message, title });
    });
  }
  return Promise.resolve(window.confirm(message));
}

export function handleConfirmResult(message: {
  type: 'confirm_result';
  requestId: string;
  confirmed: boolean;
}): void {
  const p = pending.get(message.requestId);
  if (!p) return;
  pending.delete(message.requestId);
  p.resolve(!!message.confirmed);
}
