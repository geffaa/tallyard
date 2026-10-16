import { Component, computed, DestroyRef, effect, inject, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { ArrowLeftRight, LucideAngularModule, Search, SlidersHorizontal } from 'lucide-angular';
import { Api, latest, Page, S } from '../../core/api';
import { Auth } from '../../core/auth';
import { I18n } from '../../core/i18n';
import { StockLive } from '../../core/stock-live';
import { Pager } from '../../shared/ui';

type Row = S['StockRow'];
type Filter = 'all' | 'in' | 'out';

@Component({
  selector: 'app-stock',
  imports: [FormsModule, RouterLink, LucideAngularModule, Pager],
  template: `
    <div class="page">
      <div class="page-head">
        <div>
          <div class="eyebrow flex items-center gap-2">
            {{ i18n.t('nav.inventory') }}
            <span class="inline-flex items-center gap-1.5 normal-case tracking-normal font-medium" [title]="live.connected() ? '' : i18n.t('stock.offline')">
              <span class="size-1.5 rounded-full" [class]="live.connected() ? 'bg-ok live-dot' : 'bg-faint'"></span>
              <span [class]="live.connected() ? 'text-ok' : 'text-faint'">{{ i18n.t('stock.live') }}</span>
            </span>
          </div>
          <h1 class="h1 mt-1">{{ i18n.t('stock.title') }}</h1>
          <p class="mt-1 text-muted">{{ i18n.t('stock.subtitle', { n: warehouses().length }) }}</p>
        </div>
        <div class="flex gap-2">
          @if (auth.can('TransferStock')) {
            <a class="btn" routerLink="/transfers/new"><lucide-icon [img]="TransferIcon" [size]="16" />{{ i18n.t('stock.transfer') }}</a>
          }
          @if (auth.can('AdjustStock')) {
            <a class="btn btn-primary" routerLink="/stock/adjust"><lucide-icon [img]="AdjustIcon" [size]="16" />{{ i18n.t('stock.adjust') }}</a>
          }
        </div>
      </div>

      <div class="card overflow-hidden">
        <div class="flex flex-wrap items-center gap-2.5 p-3 border-b border-line">
          <label class="relative flex-1 min-w-[220px]">
            <span class="pointer-events-none absolute inset-y-0 left-3 flex items-center text-faint"><lucide-icon [img]="SearchIcon" [size]="16" /></span>
            <input class="input pl-9" [placeholder]="i18n.t('stock.search')" [ngModel]="q()" (ngModelChange)="search($event)" />
          </label>
          <div class="flex rounded-lg border border-line-strong p-0.5 bg-sunken/50" role="tablist">
            @for (f of filters; track f) {
              <button
                role="tab"
                class="h-8 px-3 rounded-md text-[13px] font-medium transition-colors"
                [class]="filter() === f ? 'bg-surface text-ink shadow-sm' : 'text-muted hover:text-ink'"
                (click)="filter.set(f); page.set(1)"
              >
                {{ i18n.t($any('stock.filter.' + f)) }}
              </button>
            }
          </div>
        </div>

        <div class="overflow-x-auto">
          <table class="tbl">
            <thead>
              <tr>
                <th>{{ i18n.t('products.sku') }}</th>
                <th>{{ i18n.t('common.name') }}</th>
                @for (w of warehouses(); track w.id) {
                  <th class="text-right" [title]="w.name">{{ w.code }}</th>
                }
                <th class="text-right">{{ i18n.t('stock.total') }}</th>
                @if (auth.can('ViewCosts')) {
                  <th class="text-right hidden lg:table-cell">{{ i18n.t('stock.value') }}</th>
                }
              </tr>
            </thead>
            <tbody>
              @for (r of rows(); track r.productId) {
                <tr class="clickable" (click)="router.navigate(['/stock', r.productId])">
                  <td class="code whitespace-nowrap">{{ r.sku }}</td>
                  <td class="min-w-[220px]">
                    <span class="font-medium">{{ r.name }}</span>
                    @if (r.trackBatches) { <span class="badge badge-signal ml-1.5">{{ i18n.t('products.tracked') }}</span> }
                  </td>
                  @for (w of warehouses(); track w.id) {
                    @let c = cell(r, w.id);
                    <td class="num whitespace-nowrap" [class.flash]="flashing().has(r.productId + w.id)">
                      @if (c && c.onHand > 0) {
                        <span>{{ i18n.number(c.onHand) }}</span>
                        @if (c.reserved > 0) {
                          <div class="text-[11px] text-signal-ink">{{ i18n.t('stock.reserved', { n: i18n.number(c.reserved) }) }}</div>
                        }
                      } @else {
                        <span class="inline-block min-w-6 rounded bg-danger-soft px-1.5 text-danger text-[12px]">0</span>
                      }
                    </td>
                  }
                  <td class="num font-medium whitespace-nowrap">{{ i18n.number(r.onHand) }} <span class="text-faint font-sans text-[12px]">{{ r.baseUnit }}</span></td>
                  @if (auth.can('ViewCosts')) {
                    <td class="num hidden lg:table-cell text-ink-2">{{ r.value != null ? i18n.money(r.value, auth.currency()) : '' }}</td>
                  }
                </tr>
              } @empty {
                <tr><td [attr.colspan]="4 + warehouses().length" class="!py-14 text-center text-muted">{{ i18n.t('stock.empty') }}</td></tr>
              }
            </tbody>
          </table>
        </div>
        <app-pager [(page)]="page" [size]="size" [total]="total()" />
      </div>
    </div>
  `,
  styles: `
    .flash { animation: flash 1.6s ease-out; }
    @keyframes flash { 0% { background: var(--color-signal-soft); } 100% { background: transparent; } }
    .live-dot { box-shadow: 0 0 0 3px color-mix(in srgb, var(--color-ok) 20%, transparent); }
  `,
})
export class StockPage {
  i18n = inject(I18n);
  auth = inject(Auth);
  live = inject(StockLive);
  router = inject(Router);
  private api = inject(Api);

  SearchIcon = Search;
  AdjustIcon = SlidersHorizontal;
  TransferIcon = ArrowLeftRight;
  filters: Filter[] = ['all', 'in', 'out'];
  size = 50;

  q = signal('');
  filter = signal<Filter>('all');
  page = signal(1);
  rows = signal<Row[]>([]);
  total = signal(0);
  warehouses = signal<S['WarehouseDto'][]>([]);
  flashing = signal(new Set<string>());
  private cells = computed(() => {
    const map = new Map<string, S['StockCell']>();
    for (const r of this.rows()) for (const c of r.cells) map.set(r.productId + c.warehouseId, c);
    return map;
  });
  private timer?: ReturnType<typeof setTimeout>;

  private fetch = latest((q: object) => this.api.get<Page<Row>>('/stock', q as never), (res) => (this.rows.set(res.items), this.total.set(res.total)));

  constructor() {
    this.api.get<S['WarehouseDto'][]>('/warehouses').then((w) => this.warehouses.set(w.filter((x) => x.isActive)));
    effect(() => {
      const query = { q: this.q(), filter: this.filter(), page: this.page(), pageSize: this.size };
      untracked(() => this.fetch(query));
    });

    // Patch rows in place when stock changes anywhere in the company.
    const sub = this.live.changes.subscribe((changes) => {
      const shown = new Set(this.rows().map((r) => r.productId));
      const relevant = changes.filter((c) => shown.has(c.productId) && this.warehouses().some((w) => w.id === c.warehouseId));
      if (!relevant.length) return;
      this.rows.update((rows) =>
        rows.map((r) => {
          const mine = relevant.filter((c) => c.productId === r.productId);
          if (!mine.length) return r;
          const cells = [...r.cells.filter((c) => !mine.some((m) => m.warehouseId === c.warehouseId)),
            ...mine.map((m) => ({ warehouseId: m.warehouseId, onHand: m.onHand, reserved: m.reserved }))];
          return { ...r, cells, onHand: cells.reduce((s, c) => s + c.onHand, 0), reserved: cells.reduce((s, c) => s + c.reserved, 0) };
        }),
      );
      const keys = relevant.map((c) => c.productId + c.warehouseId);
      this.flashing.set(new Set(keys));
      setTimeout(() => this.flashing.set(new Set()), 1700);
    });
    inject(DestroyRef).onDestroy(() => sub.unsubscribe());
  }

  cell(r: Row, warehouseId: string) {
    return this.cells().get(r.productId + warehouseId);
  }

  search(v: string) {
    clearTimeout(this.timer);
    this.timer = setTimeout(() => (this.q.set(v), this.page.set(1)), 200);
  }
}
