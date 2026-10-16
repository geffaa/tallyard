import { Component, effect, inject, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { LucideAngularModule, Plus, Search } from 'lucide-angular';
import { Api, latest, Page, S } from '../../core/api';
import { Auth } from '../../core/auth';
import { I18n } from '../../core/i18n';
import { MessageKey } from '../../core/messages';
import { Pager } from '../../shared/ui';

export const SO_BADGE: Record<string, string> = {
  Draft: 'badge-muted',
  OnHold: 'badge-danger',
  Confirmed: 'badge-brand',
  PartiallyShipped: 'badge-signal',
  Shipped: 'badge-ok',
  Closed: 'badge-muted',
  Cancelled: 'badge-muted',
};

@Component({
  selector: 'app-so-list',
  imports: [FormsModule, RouterLink, LucideAngularModule, Pager],
  template: `
    <div class="page">
      <div class="page-head">
        <div>
          <div class="eyebrow">{{ i18n.t('nav.sales') }}</div>
          <h1 class="h1 mt-1">{{ i18n.t('so.title') }}</h1>
          <p class="mt-1 text-muted">{{ i18n.t('so.subtitle') }}</p>
        </div>
        @if (auth.can('EditSalesOrders')) {
          <a class="btn btn-primary" routerLink="/sales/new"><lucide-icon [img]="PlusIcon" [size]="16" />{{ i18n.t('so.new') }}</a>
        }
      </div>
      <div class="card overflow-hidden">
        <div class="flex flex-wrap items-center gap-2.5 p-3 border-b border-line">
          <div class="flex flex-wrap rounded-lg border border-line-strong p-0.5 bg-sunken/50">
            @for (t of tabs; track t.value) {
              <button class="h-8 px-3 rounded-md text-[13px] font-medium" [class]="tab() === t.value ? 'bg-surface text-ink shadow-sm' : 'text-muted hover:text-ink'"
                      (click)="tab.set(t.value); page.set(1)">{{ i18n.t(t.label) }}</button>
            }
          </div>
          <label class="relative flex-1 min-w-[220px]">
            <span class="pointer-events-none absolute inset-y-0 left-3 flex items-center text-faint"><lucide-icon [img]="SearchIcon" [size]="16" /></span>
            <input class="input pl-9" [placeholder]="i18n.t('so.search')" [ngModel]="q()" (ngModelChange)="search($event)" />
          </label>
        </div>
        <div class="overflow-x-auto">
          <table class="tbl">
            <thead>
              <tr>
                <th>{{ i18n.t('po.number') }}</th>
                <th>{{ i18n.t('so.customer') }}</th>
                <th>{{ i18n.t('so.shipFrom') }}</th>
                <th>{{ i18n.t('common.status') }}</th>
                <th class="hidden md:table-cell">{{ i18n.t('po.orderDate') }}</th>
                <th class="hidden lg:table-cell w-40">{{ i18n.t('so.shipped') }}</th>
                @if (auth.can('ViewSalesPrices')) { <th class="text-right">{{ i18n.t('po.total') }}</th> }
              </tr>
            </thead>
            <tbody>
              @for (o of data()?.items ?? []; track o.id) {
                <tr class="clickable" (click)="router.navigate(['/sales', o.id])">
                  <td class="code whitespace-nowrap">{{ o.number }}</td>
                  <td class="font-medium">{{ o.customer }}</td>
                  <td class="code">{{ o.warehouse }}</td>
                  <td class="whitespace-nowrap">
                    <span class="badge" [class]="badge[o.status]">{{ i18n.t($any('so.status.' + o.status)) }}</span>
                    @if (o.backordered) { <span class="badge badge-signal ml-1">{{ i18n.t('so.backorderBadge') }}</span> }
                  </td>
                  <td class="hidden md:table-cell tabular-nums text-[13px]">{{ o.orderDate }}</td>
                  <td class="hidden lg:table-cell">
                    @if (o.shippedPercent > 0) {
                      <div class="flex items-center gap-2">
                        <div class="h-1.5 flex-1 rounded-full bg-sunken overflow-hidden"><div class="h-full bg-ok" [style.width.%]="o.shippedPercent"></div></div>
                        <span class="text-[12px] text-muted tabular-nums w-9 text-right">{{ o.shippedPercent }}%</span>
                      </div>
                    }
                  </td>
                  @if (auth.can('ViewSalesPrices')) { <td class="num">{{ o.total != null ? i18n.money(o.total, auth.currency()) : '' }}</td> }
                </tr>
              } @empty {
                <tr><td colspan="7" class="!py-14 text-center text-muted">{{ i18n.t('so.empty') }}</td></tr>
              }
            </tbody>
          </table>
        </div>
        <app-pager [(page)]="page" [size]="25" [total]="data()?.total ?? 0" />
      </div>
    </div>
  `,
})
export class SoListPage {
  i18n = inject(I18n);
  auth = inject(Auth);
  router = inject(Router);
  private api = inject(Api);
  PlusIcon = Plus;
  SearchIcon = Search;
  badge = SO_BADGE;
  tabs: { value: string; label: MessageKey }[] = [
    { value: 'open', label: 'so.tab.open' },
    { value: 'hold', label: 'so.tab.hold' },
    { value: 'draft', label: 'so.tab.draft' },
    { value: 'done', label: 'so.tab.done' },
    { value: '', label: 'so.tab.all' },
  ];
  tab = signal('open');
  q = signal('');
  page = signal(1);
  data = signal<Page<S['SoListItem']> | null>(null);
  private timer?: ReturnType<typeof setTimeout>;

  private fetch = latest((q: object) => this.api.get<Page<S['SoListItem']>>('/sales-orders', q as never), (d) => this.data.set(d));

  constructor() {
    // Finance lands on what is waiting for them.
    if (this.auth.can('ReleaseCreditHolds') && !this.auth.can('EditSalesOrders')) this.tab.set('hold');
    effect(() => {
      const query = { status: this.tab(), q: this.q(), page: this.page() };
      untracked(() => this.fetch(query));
    });
  }

  search(v: string) {
    clearTimeout(this.timer);
    this.timer = setTimeout(() => (this.q.set(v), this.page.set(1)), 200);
  }
}
