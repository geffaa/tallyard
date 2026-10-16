import { Component, ElementRef, inject, input, output, signal, viewChild } from '@angular/core';
import { LucideAngularModule, Search } from 'lucide-angular';
import { Api, Page, S } from '../core/api';

/** Type to search products (name, SKU or a scanned barcode); arrow keys and Enter to pick. */
@Component({
  selector: 'app-product-picker',
  imports: [LucideAngularModule],
  template: `
    <div class="relative">
      <span class="pointer-events-none absolute inset-y-0 left-3 flex items-center text-faint"><lucide-icon [img]="SearchIcon" [size]="15" /></span>
      <input
        #box
        class="input pl-9"
        [class.invalid]="invalid()"
        [placeholder]="placeholder()"
        [value]="label()"
        (input)="search(box.value)"
        (focus)="open.set(results().length > 0)"
        (blur)="close()"
        (keydown)="key($event)"
        role="combobox"
        [attr.aria-expanded]="open()"
        autocomplete="off"
      />
      @if (open()) {
        <ul class="absolute z-30 mt-1 w-full max-h-72 overflow-y-auto rounded-lg border border-line bg-surface py-1 shadow-lg shadow-ink/10" role="listbox">
          @for (p of results(); track p.id; let i = $index) {
            <li
              role="option"
              class="flex items-baseline gap-2.5 px-3 py-2 cursor-pointer"
              [class.bg-brand-soft]="i === active()"
              (mousedown)="$event.preventDefault(); pick(p)"
              (mouseenter)="active.set(i)"
            >
              <span class="code shrink-0">{{ p.sku }}</span>
              <span class="truncate text-[13.5px]">{{ p.name }}</span>
            </li>
          }
        </ul>
      }
    </div>
  `,
})
export class ProductPicker {
  private api = inject(Api);
  placeholder = input('');
  invalid = input(false);
  label = input('');
  picked = output<S['ProductListItem']>();

  SearchIcon = Search;
  box = viewChild.required<ElementRef<HTMLInputElement>>('box');
  results = signal<S['ProductListItem'][]>([]);
  open = signal(false);
  active = signal(0);
  private timer?: ReturnType<typeof setTimeout>;
  private request = 0;

  search(q: string) {
    clearTimeout(this.timer);
    if (!q.trim()) {
      this.results.set([]);
      this.open.set(false);
      return;
    }
    this.timer = setTimeout(async () => {
      const id = ++this.request;
      const page = await this.api.get<Page<S['ProductListItem']>>('/products', { q, pageSize: 8, active: true });
      if (id !== this.request) return;
      this.results.set(page.items);
      this.active.set(0);
      this.open.set(page.items.length > 0);
      // A scanned barcode or exact SKU matches one product: take it without a click.
      if (page.items.length === 1 && (page.items[0].sku === q.trim().toUpperCase() || /^\d{8,14}$/.test(q.trim()))) this.pick(page.items[0]);
    }, 160);
  }

  key(e: KeyboardEvent) {
    const n = this.results().length;
    if (!this.open() || n === 0) return;
    if (e.key === 'ArrowDown') this.active.set((this.active() + 1) % n);
    else if (e.key === 'ArrowUp') this.active.set((this.active() - 1 + n) % n);
    else if (e.key === 'Enter') this.pick(this.results()[this.active()]);
    else if (e.key === 'Escape') this.open.set(false);
    else return;
    e.preventDefault();
  }

  pick(p: S['ProductListItem']) {
    this.picked.emit(p);
    this.open.set(false);
    this.results.set([]);
    this.box().nativeElement.value = `${p.sku} · ${p.name}`;
  }

  close() {
    setTimeout(() => this.open.set(false), 120);
  }
}
