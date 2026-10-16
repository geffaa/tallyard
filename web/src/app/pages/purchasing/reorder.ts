import { Component, computed, inject, OnInit, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { LucideAngularModule, ShoppingCart } from 'lucide-angular';
import { Api, ApiError, S } from '../../core/api';
import { Auth } from '../../core/auth';
import { I18n } from '../../core/i18n';
import { Toasts } from '../../core/toast';

type Suggestion = S['ReorderSuggestion'] & { picked: boolean; quantity: number };

@Component({
  selector: 'app-reorder',
  imports: [FormsModule, RouterLink, LucideAngularModule],
  template: `
    <div class="page">
      <div class="page-head">
        <div>
          <div class="eyebrow">{{ i18n.t('nav.purchasing') }}</div>
          <h1 class="h1 mt-1">{{ i18n.t('reorder.title') }}</h1>
          <p class="mt-1 text-muted max-w-[640px]">{{ i18n.t('reorder.subtitle') }}</p>
        </div>
        @if (auth.can('EditPurchaseOrders')) {
          <button class="btn btn-primary" [disabled]="busy() || orderCount() === 0" (click)="create()">
            <lucide-icon [img]="CartIcon" [size]="16" />{{ i18n.t('reorder.create', { n: orderCount() }) }}
          </button>
        }
      </div>

      @for (g of groups(); track g.key) {
        <section class="card overflow-hidden mb-5">
          <div class="card-head">
            <label class="flex items-center gap-3 cursor-pointer">
              <input type="checkbox" class="check" [checked]="allPicked(g.items)" (change)="toggleGroup(g.items, $any($event.target).checked)" />
              <span>
                <span class="card-title">{{ g.supplier }}</span>
                <span class="text-muted text-[13px]"> → <span class="code">{{ g.warehouse }}</span> · {{ i18n.t('reorder.lines', { n: g.items.length }) }}</span>
              </span>
            </label>
            @if (g.total != null) { <span class="num font-medium">{{ i18n.money(g.total, auth.currency()) }}</span> }
          </div>
          <div class="overflow-x-auto">
            <table class="tbl">
              <thead>
                <tr>
                  <th class="w-10"></th>
                  <th>{{ i18n.t('products.sku') }}</th>
                  <th>{{ i18n.t('common.name') }}</th>
                  <th class="text-right">{{ i18n.t('reorder.available') }}</th>
                  <th class="text-right">{{ i18n.t('reorder.incoming') }}</th>
                  <th class="text-right">{{ i18n.t('reorder.point') }}</th>
                  <th class="w-40">{{ i18n.t('reorder.suggest') }}</th>
                </tr>
              </thead>
              <tbody>
                @for (s of g.items; track s.productId + s.warehouseId) {
                  <tr [class.opacity-50]="!s.picked">
                    <td><input type="checkbox" class="check" [(ngModel)]="s.picked" (ngModelChange)="touch()" /></td>
                    <td><a class="code hover:underline" [routerLink]="['/stock', s.productId]">{{ s.sku }}</a></td>
                    <td>{{ s.name }}</td>
                    <td class="num" [class.text-danger]="s.onHand - s.reserved <= 0">{{ i18n.number(s.onHand - s.reserved) }}</td>
                    <td class="num text-ink-2">{{ s.incoming ? i18n.number(s.incoming) : '—' }}</td>
                    <td class="num text-ink-2">{{ i18n.number(s.reorderPoint) }}</td>
                    <td>
                      <div class="flex items-center gap-2">
                        <input class="input num !text-left h-8 w-20" type="number" min="1" [(ngModel)]="s.quantity" (ngModelChange)="touch()" />
                        <span class="text-[12.5px] text-muted whitespace-nowrap">{{ s.unit }}</span>
                      </div>
                    </td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
        </section>
      } @empty {
        <div class="card px-5 py-14 text-center text-muted">{{ i18n.t('reorder.empty') }}</div>
      }
    </div>
  `,
})
export class ReorderPage implements OnInit {
  i18n = inject(I18n);
  auth = inject(Auth);
  private api = inject(Api);
  private router = inject(Router);
  private toasts = inject(Toasts);
  CartIcon = ShoppingCart;
  items = signal<Suggestion[]>([]);
  busy = signal(false);
  private version = signal(0);

  groups = computed(() => {
    this.version();
    const map = new Map<string, { key: string; supplier: string; warehouse: string; items: Suggestion[]; total: number | null }>();
    for (const s of this.items()) {
      const key = s.supplierId + s.warehouseId;
      const g = map.get(key) ?? { key, supplier: s.supplier, warehouse: s.warehouse, items: [], total: s.unitPrice == null ? null : 0 };
      g.items.push(s);
      if (g.total != null && s.picked) g.total += s.quantity * (s.unitPrice ?? 0);
      map.set(key, g);
    }
    return [...map.values()];
  });
  orderCount = computed(() => this.groups().filter((g) => g.items.some((i) => i.picked)).length);

  async ngOnInit() {
    const list = await this.api.get<S['ReorderSuggestion'][]>('/reorder/suggestions');
    this.items.set(list.map((s) => ({ ...s, picked: true, quantity: s.suggestedQuantity })));
  }

  touch() {
    this.version.update((v) => v + 1);
  }

  allPicked(items: Suggestion[]) {
    return items.every((i) => i.picked);
  }

  toggleGroup(items: Suggestion[], on: boolean) {
    items.forEach((i) => (i.picked = on));
    this.touch();
  }

  async create() {
    const picks = this.items().filter((s) => s.picked && s.quantity > 0)
      .map((s) => ({ productId: s.productId, warehouseId: s.warehouseId, quantity: s.quantity }));
    this.busy.set(true);
    try {
      const orders = await this.api.post<{ id: string; number: string }[]>('/reorder/create-orders', picks);
      this.toasts.show(this.i18n.t('reorder.created', { n: orders.length }));
      this.router.navigate(orders.length === 1 ? ['/purchasing', orders[0].id] : ['/purchasing']);
    } catch (e) {
      this.toasts.show(ApiError.from(e).message, 'error');
    } finally {
      this.busy.set(false);
    }
  }
}
