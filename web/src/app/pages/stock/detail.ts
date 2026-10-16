import { Component, computed, DestroyRef, effect, inject, input, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { ArrowLeft, ArrowLeftRight, LucideAngularModule, Pencil, SlidersHorizontal } from 'lucide-angular';
import { Api, Page, S } from '../../core/api';
import { Auth } from '../../core/auth';
import { I18n } from '../../core/i18n';
import { StockLive } from '../../core/stock-live';
import { Pager } from '../../shared/ui';

@Component({
  selector: 'app-stock-detail',
  imports: [FormsModule, RouterLink, LucideAngularModule, Pager],
  template: `
    <div class="page">
      <a routerLink="/stock" class="inline-flex items-center gap-1.5 text-[13px] text-muted hover:text-ink">
        <lucide-icon [img]="BackIcon" [size]="15" />{{ i18n.t('stock.title') }}
      </a>
      @if (stock(); as s) {
        <div class="page-head mt-3">
          <div class="min-w-0">
            <h1 class="h1">{{ s.name }}</h1>
            <p class="mt-1 flex items-center gap-2">
              <span class="code">{{ s.sku }}</span>
              @if (s.trackBatches) { <span class="badge badge-signal">{{ i18n.t('products.tracked') }}</span> }
              <a [routerLink]="['/products', s.productId]" class="text-[13px] text-muted hover:text-ink inline-flex items-center gap-1">
                <lucide-icon [img]="EditIcon" [size]="13" />{{ i18n.t('common.edit') }}
              </a>
            </p>
          </div>
          <div class="flex gap-2">
            @if (auth.can('TransferStock')) {
              <a class="btn" routerLink="/transfers/new" [queryParams]="{ product: s.productId }"><lucide-icon [img]="TransferIcon" [size]="16" />{{ i18n.t('stock.transfer') }}</a>
            }
            @if (auth.can('AdjustStock')) {
              <a class="btn btn-primary" routerLink="/stock/adjust" [queryParams]="{ product: s.productId }"><lucide-icon [img]="AdjustIcon" [size]="16" />{{ i18n.t('stock.adjust') }}</a>
            }
          </div>
        </div>

        <div class="grid gap-3 sm:grid-cols-2 xl:grid-cols-4 mb-5">
          @for (w of s.warehouses; track w.warehouseId) {
            <button
              class="card p-4 text-left transition-colors hover:border-line-strong"
              [class.ring-2]="warehouse() === w.warehouseId"
              [class.ring-brand]="warehouse() === w.warehouseId"
              (click)="warehouse.set(warehouse() === w.warehouseId ? '' : w.warehouseId); page.set(1)"
            >
              <div class="flex items-baseline justify-between gap-2">
                <span class="font-mono text-[13px] font-semibold text-brand-ink">{{ w.code }}</span>
                <span class="text-[12px] text-muted truncate">{{ w.name }}</span>
              </div>
              <div class="mt-3 flex items-baseline gap-1.5">
                <span class="text-[26px] font-semibold tabular-nums tracking-tight" [class.text-danger]="w.onHand === 0">{{ i18n.number(w.onHand) }}</span>
                <span class="text-muted text-[13px]">{{ s.baseUnit }}</span>
              </div>
              <dl class="mt-2 grid grid-cols-2 gap-x-3 text-[12.5px]">
                <dt class="text-muted">{{ i18n.t('stock.available') }}</dt>
                <dd class="text-right tabular-nums">{{ i18n.number(w.available) }}</dd>
                <dt class="text-muted">{{ i18n.t('stock.reservedLabel') }}</dt>
                <dd class="text-right tabular-nums" [class.text-signal-ink]="w.reserved > 0">{{ i18n.number(w.reserved) }}</dd>
                @if (w.avgCost != null) {
                  <dt class="text-muted">{{ i18n.t('stock.avgCost') }}</dt>
                  <dd class="text-right tabular-nums">{{ i18n.money(w.avgCost, auth.currency()) }}</dd>
                }
              </dl>
            </button>
          }
        </div>

        @if (s.trackBatches) {
          <section class="card mb-5 overflow-hidden">
            <div class="card-head"><h2 class="card-title">{{ i18n.t('stock.batches') }}</h2></div>
            <div class="overflow-x-auto">
              <table class="tbl">
                <thead>
                  <tr>
                    <th>{{ i18n.t('stock.batch') }}</th>
                    <th>{{ i18n.t('adjust.warehouse') }}</th>
                    <th>{{ i18n.t('stock.expires') }}</th>
                    <th class="text-right">{{ i18n.t('stock.onHand') }}</th>
                  </tr>
                </thead>
                <tbody>
                  @for (b of batches(); track b.batchId + b.warehouseId) {
                    <tr>
                      <td class="code">{{ b.number }}</td>
                      <td class="code">{{ code(b.warehouseId) }}</td>
                      <td>
                        @if (b.expiresOn) {
                          <span class="tabular-nums">{{ b.expiresOn }}</span>
                          @let d = daysLeft(b.expiresOn);
                          @if (d < 0) { <span class="badge badge-danger ml-2">{{ i18n.t('stock.expired') }}</span> }
                          @else if (d <= 30) { <span class="badge badge-signal ml-2">{{ i18n.t('stock.expiresIn', { n: d }) }}</span> }
                        } @else { <span class="text-faint">—</span> }
                      </td>
                      <td class="num">{{ i18n.number(b.onHand) }}</td>
                    </tr>
                  } @empty {
                    <tr><td colspan="4" class="!py-8 text-center text-muted">{{ i18n.t('stock.noBatches') }}</td></tr>
                  }
                </tbody>
              </table>
            </div>
          </section>
        }

        <section class="card overflow-hidden">
          <div class="card-head flex-wrap">
            <div>
              <h2 class="card-title">{{ i18n.t('stock.card') }}</h2>
              <p class="hint mt-0.5">{{ i18n.t('stock.cardHint') }}</p>
            </div>
            <select class="input w-auto" [ngModel]="warehouse()" (ngModelChange)="warehouse.set($event); page.set(1)">
              <option value="">{{ i18n.t('stock.allWarehouses') }}</option>
              @for (w of s.warehouses; track w.warehouseId) { <option [value]="w.warehouseId">{{ w.code }} · {{ w.name }}</option> }
            </select>
          </div>
          <div class="overflow-x-auto">
            <table class="tbl">
              <thead>
                <tr>
                  <th>{{ i18n.t('stock.date') }}</th>
                  <th>{{ i18n.t('stock.document') }}</th>
                  <th>{{ i18n.t('adjust.warehouse') }}</th>
                  @if (s.trackBatches) { <th>{{ i18n.t('stock.batch') }}</th> }
                  <th class="text-right">{{ i18n.t('stock.change') }}</th>
                  <th class="text-right">{{ i18n.t('stock.balance') }}</th>
                  @if (auth.can('ViewCosts')) { <th class="text-right hidden lg:table-cell">{{ i18n.t('adjust.cost') }}</th> }
                  <th class="hidden md:table-cell">{{ i18n.t('stock.by') }}</th>
                </tr>
              </thead>
              <tbody>
                @for (m of movements()?.items ?? []; track m.id) {
                  <tr>
                    <td class="whitespace-nowrap text-[13px]" [title]="i18n.dateTime(m.createdAt)">{{ i18n.relative(m.createdAt) }}</td>
                    <td class="whitespace-nowrap">
                      @if (m.sourceType === 'Transfer' && m.sourceId) {
                        <a class="code hover:underline" [routerLink]="['/transfers', m.sourceId]">{{ m.number }}</a>
                      } @else {
                        <span class="code">{{ m.number }}</span>
                      }
                      <div class="text-[12px] text-muted">
                        {{ i18n.t($any('kind.' + m.kind)) }}@if (m.reason) { · {{ i18n.t($any('reason.' + m.reason)) }} }
                      </div>
                    </td>
                    <td class="code">{{ m.warehouse }}</td>
                    @if (s.trackBatches) { <td class="code">{{ m.batch ?? '—' }}</td> }
                    <td class="num font-medium" [class]="m.quantity > 0 ? 'text-ok' : 'text-danger'">
                      {{ m.quantity > 0 ? '+' : '' }}{{ i18n.number(m.quantity, 4) }}
                    </td>
                    <td class="num">{{ i18n.number(m.balanceAfter, 4) }}</td>
                    @if (auth.can('ViewCosts')) {
                      <td class="num hidden lg:table-cell text-ink-2">{{ m.unitCost != null ? i18n.money(m.unitCost, auth.currency()) : '' }}</td>
                    }
                    <td class="hidden md:table-cell text-[13px] text-ink-2">{{ m.user ?? i18n.t('audit.system') }}</td>
                  </tr>
                } @empty {
                  <tr><td colspan="8" class="!py-10 text-center text-muted">{{ i18n.t('stock.noMovements') }}</td></tr>
                }
              </tbody>
            </table>
          </div>
          <app-pager [(page)]="page" [size]="50" [total]="movements()?.total ?? 0" />
        </section>
      }
    </div>
  `,
})
export class StockDetailPage {
  i18n = inject(I18n);
  auth = inject(Auth);
  private api = inject(Api);
  private live = inject(StockLive);

  id = input.required<string>();
  BackIcon = ArrowLeft;
  EditIcon = Pencil;
  AdjustIcon = SlidersHorizontal;
  TransferIcon = ArrowLeftRight;

  stock = signal<S['ProductStock'] | null>(null);
  movements = signal<Page<S['MovementDto']> | null>(null);
  warehouse = signal('');
  page = signal(1);
  batches = computed(() => {
    const w = this.warehouse();
    return (this.stock()?.batches ?? []).filter((b) => !w || b.warehouseId === w);
  });

  constructor() {
    effect(() => {
      const id = this.id();
      untracked(() => this.loadStock(id));
    });
    effect(() => {
      const query = { id: this.id(), warehouseId: this.warehouse(), page: this.page() };
      untracked(() => this.loadMovements(query.id, query.warehouseId, query.page));
    });
    const sub = this.live.changes.subscribe((changes) => {
      if (changes.some((c) => c.productId === this.id())) {
        this.loadStock(this.id());
        this.loadMovements(this.id(), this.warehouse(), this.page());
      }
    });
    inject(DestroyRef).onDestroy(() => sub.unsubscribe());
  }

  code(warehouseId: string) {
    return this.stock()?.warehouses.find((w) => w.warehouseId === warehouseId)?.code ?? '';
  }

  /** Whole days until the expiry date (negative once expired). */
  daysLeft(date: string): number {
    return Math.ceil((new Date(date + 'T00:00:00').getTime() - Date.now()) / 86_400_000);
  }

  private async loadStock(id: string) {
    this.stock.set(await this.api.get<S['ProductStock']>(`/stock/${id}`));
  }

  private async loadMovements(id: string, warehouseId: string, page: number) {
    this.movements.set(await this.api.get<Page<S['MovementDto']>>(`/stock/${id}/movements`, { warehouseId, page }));
  }
}
