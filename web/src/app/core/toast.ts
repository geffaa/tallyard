import { Component, inject, Injectable, signal } from '@angular/core';

interface Toast {
  id: number;
  text: string;
  kind: 'ok' | 'error';
}

@Injectable({ providedIn: 'root' })
export class Toasts {
  readonly items = signal<Toast[]>([]);
  private next = 0;

  show(text: string, kind: Toast['kind'] = 'ok') {
    const toast = { id: this.next++, text, kind };
    this.items.update((list) => [...list, toast]);
    setTimeout(() => this.items.update((list) => list.filter((t) => t.id !== toast.id)), kind === 'error' ? 6000 : 2800);
  }
}

@Component({
  selector: 'app-toasts',
  template: `
    <div class="fixed bottom-5 right-5 z-50 flex flex-col gap-2" aria-live="polite">
      @for (t of toasts.items(); track t.id) {
        <div
          class="toast flex items-center gap-2.5 rounded-lg px-4 py-2.5 text-[13.5px] font-medium shadow-lg shadow-ink/10"
          [class]="t.kind === 'ok' ? 'bg-ink text-white' : 'bg-danger text-white'"
        >
          <span class="size-1.5 rounded-full" [class]="t.kind === 'ok' ? 'bg-signal' : 'bg-white'"></span>
          {{ t.text }}
        </div>
      }
    </div>
  `,
  styles: `
    .toast { animation: rise 180ms ease-out; }
    @keyframes rise { from { opacity: 0; transform: translateY(6px); } }
  `,
})
export class ToastsComponent {
  toasts = inject(Toasts);
}
