/**
 * Lightweight localization helper for LiveWallpaper UI.
 * Driven by ?lang=en / ?lang=ru query param or <html lang="..."> attribute.
 */

export function isEn(): boolean {
  if (typeof window === 'undefined') return false;
  const p = new URLSearchParams(window.location.search);
  const lang = p.get('lang');
  if (lang === 'en') return true;
  if (lang === 'ru') return false;
  return document.documentElement.lang?.toLowerCase().startsWith('en') ?? false;
}

export function t(ru: string, en: string): string {
  return isEn() ? en : ru;
}

export const tr = t;
