import { computed, Injectable, signal } from '@angular/core';
import { en, id, MessageKey } from './messages';

export type Lang = 'en' | 'id';
const tables: Record<Lang, Record<MessageKey, string>> = { en, id };
const STORAGE_KEY = 'tallyard.lang';

/**
 * Runtime language switch (Angular's built-in i18n compiles one build per language, which
 * can't switch in place). Text, numbers and dates all follow `lang`.
 */
@Injectable({ providedIn: 'root' })
export class I18n {
  readonly lang = signal<Lang>(initial());
  readonly locale = computed(() => (this.lang() === 'id' ? 'id-ID' : 'en-US'));

  setLang(lang: Lang) {
    this.lang.set(lang);
    document.documentElement.lang = lang;
    try {
      localStorage.setItem(STORAGE_KEY, lang);
    } catch {
      /* storage unavailable: the choice lasts for this page only */
    }
  }

  t(key: MessageKey, vars?: Record<string, string | number>): string {
    let text = tables[this.lang()][key];
    for (const [k, v] of Object.entries(vars ?? {})) text = text.replaceAll(`{${k}}`, String(v));
    return text;
  }

  number(n: number, digits = 0): string {
    return new Intl.NumberFormat(this.locale(), { maximumFractionDigits: digits }).format(n);
  }

  money(n: number, currency: string): string {
    return new Intl.NumberFormat(this.locale(), { style: 'currency', currency }).format(n);
  }

  dateTime(iso: string): string {
    return new Intl.DateTimeFormat(this.locale(), { dateStyle: 'medium', timeStyle: 'short' }).format(new Date(iso));
  }

  relative(iso: string): string {
    const seconds = (new Date(iso).getTime() - Date.now()) / 1000;
    const rtf = new Intl.RelativeTimeFormat(this.locale(), { numeric: 'auto' });
    const steps: [Intl.RelativeTimeFormatUnit, number][] = [['second', 60], ['minute', 60], ['hour', 24], ['day', 7]];
    let value = seconds;
    for (const [unit, size] of steps) {
      if (Math.abs(value) < size) return rtf.format(Math.round(value), unit);
      value /= size;
    }
    return this.dateTime(iso);
  }
}

function initial(): Lang {
  try {
    const saved = localStorage.getItem(STORAGE_KEY);
    if (saved === 'en' || saved === 'id') return saved;
  } catch {
    /* ignore */
  }
  return 'en';
}
