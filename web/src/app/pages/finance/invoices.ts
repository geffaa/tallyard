import { Component, effect, inject, input, OnInit, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { ArrowLeft, Download, LucideAngularModule, ReceiptText, Wallet } from 'lucide-angular';
import { Api, latest, ApiError, Page, S } from '../../core/api';
import { Auth } from '../../core/auth';
import { I18n } from '../../core/i18n';
import { MessageKey } from '../../core/messages';
import { Toasts } from '../../core/toast';
import { Pager } from '../../shared/ui';

export const INV_BADGE: Record<string, string> = { Open: 'badge-brand', PartiallyPaid: 'badge-signal', Paid: 'badge-ok', Void: 'badge-muted' };

@Component({
  selector: 'app-invoices',
  imports: [RouterLink, LucideAngularModule, Pager],
  template: `
    <div class="page">
      <div class="page-head">
        <div>
          <div class="eyebrow">{{ i18n.t('nav.finance') }}</div>
          <h1 class="h1 mt-1">{{ i18n.t('inv.title') }}</h1>
          <p class="mt-1 text-muted">{{ i18n.t('inv.subtitle') }}</p>
        </div>
        @if (auth.can('ManageReceivables')) {
          <a class="btn" routerLink="/invoices/to-invoice"><lucide-icon [img]="ReceiptIcon" [size]="16" />{{ i18n.t('nav.toInvoice') }}</a>
        }
      </div>
      <div class="card overflow-hidden">
        <div class="flex p-3 border-b border-line">
          <div class="flex flex-wrap rounded-lg border border-line-strong p-0.5 bg-sunken/50">
            @for (t of tabs; track t.value) {
              <button class="h-8 px-3 rounded-md text-[13px] font-medium" [class]="tab() === t.value ? 'bg-surface text-ink shadow-sm' : 'text-muted hover:text-ink'"
                      (click)="tab.set(t.value); page.set(1)">{{ i18n.t(t.label) }}</button>
            }
          </div>
        </div>
        <div class="overflow-x-auto">
          <table class="tbl">
            <thead>
              <tr>
                <th>{{ i18n.t('inv.number') }}</th>
                <th>{{ i18n.t('so.customer') }}</th>
                <th>{{ i18n.t('inv.issued') }}</th>
                <th>{{ i18n.t('inv.due') }}</th>
                <th>{{ i18n.t('common.status') }}</th>
                <th class="text-right">{{ i18n.t('po.total') }}</th>
                <th class="text-right">{{ i18n.t('inv.balance') }}</th>
              </tr>
            </thead>
            <tbody>
              @for (i of data()?.items ?? []; track i.id) {
                <tr class="clickable" (click)="router.navigate(['/invoices', i.id])">
                  <td class="code">{{ i.number }}</td>
                  <td class="font-medium">{{ i.customer }}</td>
                  <td class="tabular-nums text-[13px]">{{ i.issueDate }}</td>
                  <td class="tabular-nums text-[13px] whitespace-nowrap">
                    {{ i.dueDate }}
                    @if (i.daysOverdue > 0) { <span class="badge ml-1.5" [class]="i.daysOverdue > 30 ? 'badge-danger' : 'badge-signal'">{{ i18n.t('inv.overdueDays', { n: i.daysOverdue }) }}</span> }
                  </td>
                  <td><span class="badge" [class]="badge[i.status]">{{ i18n.t($any('inv.status.' + i.status)) }}</span></td>
                  <td class="num">{{ i18n.money(i.total, auth.currency()) }}</td>
                  <td class="num font-medium" [class.text-faint]="i.balance === 0">{{ i18n.money(i.balance, auth.currency()) }}</td>
                </tr>
              } @empty {
                <tr><td colspan="7" class="!py-14 text-center text-muted">{{ i18n.t('inv.empty') }}</td></tr>
              }
            </tbody>
          </table>
        </div>
        <app-pager [(page)]="page" [size]="25" [total]="data()?.total ?? 0" />
      </div>
    </div>
  `,
})
export class InvoicesPage {
  i18n = inject(I18n);
  auth = inject(Auth);
  router = inject(Router);
  private api = inject(Api);
  ReceiptIcon = ReceiptText;
  badge = INV_BADGE;
  tabs: { value: string; label: MessageKey }[] = [
    { value: 'open', label: 'inv.tab.open' },
    { value: 'overdue', label: 'inv.tab.overdue' },
    { value: 'paid', label: 'inv.tab.paid' },
    { value: '', label: 'inv.tab.all' },
  ];
  tab = signal('open');
  page = signal(1);
  data = signal<Page<S['InvoiceListItem']> | null>(null);

  private fetch = latest((q: object) => this.api.get<Page<S['InvoiceListItem']>>('/invoices', q as never), (d) => this.data.set(d));

  constructor() {
    effect(() => {
      const q = { status: this.tab(), page: this.page() };
      untracked(() => this.fetch(q));
    });
  }
}

@Component({
  selector: 'app-invoice',
  imports: [RouterLink, LucideAngularModule],
  template: `
    <div class="page-narrow">
      <a routerLink="/invoices" class="inline-flex items-center gap-1.5 text-[13px] text-muted hover:text-ink">
        <lucide-icon [img]="BackIcon" [size]="15" />{{ i18n.t('inv.title') }}
      </a>
      @if (inv(); as i) {
        <div class="page-head mt-3">
          <div>
            <h1 class="h1 font-mono">{{ i.number }}</h1>
            <p class="mt-1.5 flex flex-wrap items-center gap-2 text-[13px] text-muted">
              <span class="badge" [class]="badge[i.status]">{{ i18n.t($any('inv.status.' + i.status)) }}</span>
              <span class="font-medium text-ink">{{ i.customer }}</span> ·
              <a class="code hover:underline" [routerLink]="['/sales', i.salesOrderId]">{{ i.salesOrder }}</a> · <span class="code">{{ i.shipment }}</span>
            </p>
          </div>
          <div class="flex gap-2">
            <a class="btn" [href]="'/api/invoices/' + i.id + '/pdf'" target="_blank"><lucide-icon [img]="DownloadIcon" [size]="15" />{{ i18n.t('po.pdf') }}</a>
            @if (i.balance > 0 && auth.can('ManageReceivables')) {
              <a class="btn btn-primary" routerLink="/payments/new" [queryParams]="{ customer: i.customerId, invoice: i.id }">
                <lucide-icon [img]="PayIcon" [size]="15" />{{ i18n.t('inv.pay') }}
              </a>
            }
          </div>
        </div>

        <div class="grid grid-cols-2 sm:grid-cols-4 gap-3 mb-5">
          @for (f of [
            { k: 'inv.issued', v: i.issueDate }, { k: 'inv.due', v: i.dueDate },
            { k: 'po.total', v: i18n.money(i.total, auth.currency()) }, { k: 'inv.balance', v: i18n.money(i.balance, auth.currency()) }
          ]; track f.k) {
            <div class="card px-4 py-3"><div class="eyebrow">{{ i18n.t($any(f.k)) }}</div><div class="mt-1 text-[16px] font-semibold tabular-nums">{{ f.v }}</div></div>
          }
        </div>

        <section class="card overflow-hidden mb-5">
          <table class="tbl">
            <thead><tr><th>{{ i18n.t('po.lines') }}</th><th class="text-right">{{ i18n.t('po.qty') }}</th><th class="text-right">{{ i18n.t('po.price') }}</th><th class="text-right">{{ i18n.t('po.amount') }}</th></tr></thead>
            <tbody>
              @for (l of i.lines; track $index) {
                <tr>
                  <td>{{ l.description }} @if (l.discountPercent) { <span class="text-ok text-[12px]">−{{ l.discountPercent }}%</span> }</td>
                  <td class="num whitespace-nowrap">{{ i18n.number(l.quantity, 4) }} <span class="text-faint font-sans text-[12px]">{{ l.unit }}</span></td>
                  <td class="num">{{ i18n.money(l.unitPrice, auth.currency()) }}</td>
                  <td class="num">{{ i18n.money(l.net, auth.currency()) }}</td>
                </tr>
              }
            </tbody>
          </table>
          <div class="flex justify-end border-t border-line px-5 py-4">
            <dl class="grid grid-cols-[auto_auto] gap-x-8 gap-y-1 text-[13.5px]">
              <dt class="text-muted">{{ i18n.t('po.subtotal') }}</dt><dd class="num">{{ i18n.money(i.subtotal, auth.currency()) }}</dd>
              <dt class="text-muted">{{ i18n.t('po.taxTotal') }}</dt><dd class="num">{{ i18n.money(i.taxTotal, auth.currency()) }}</dd>
              <dt class="font-semibold pt-1 border-t border-line">{{ i18n.t('po.total') }}</dt><dd class="num font-semibold pt-1 border-t border-line">{{ i18n.money(i.total, auth.currency()) }}</dd>
              <dt class="text-muted">{{ i18n.t('inv.paid') }}</dt><dd class="num text-ok">{{ i18n.money(i.amountPaid, auth.currency()) }}</dd>
            </dl>
          </div>
        </section>

        <section class="card">
          <div class="card-head"><h2 class="card-title">{{ i18n.t('inv.payments') }}</h2></div>
          <ul class="divide-y divide-line">
            @for (p of i.payments; track p.number) {
              <li class="flex items-center justify-between gap-3 px-5 py-3 text-[13.5px]">
                <span class="code">{{ p.number }}</span>
                <span class="text-muted">{{ p.receivedOn }} · {{ i18n.t($any('pay.method.' + p.method)) }}</span>
                <span class="num text-ok">{{ i18n.money(p.amount, auth.currency()) }}</span>
              </li>
            } @empty {
              <li class="px-5 py-4 text-muted text-[13px]">{{ i18n.t('inv.noPayments') }}</li>
            }
          </ul>
        </section>
      }
    </div>
  `,
})
export class InvoicePage {
  i18n = inject(I18n);
  auth = inject(Auth);
  private api = inject(Api);
  id = input.required<string>();
  BackIcon = ArrowLeft;
  DownloadIcon = Download;
  PayIcon = Wallet;
  badge = INV_BADGE;
  inv = signal<S['InvoiceDto'] | null>(null);

  constructor() {
    effect(() => {
      const id = this.id();
      untracked(() => this.api.get<S['InvoiceDto']>(`/invoices/${id}`).then((i) => this.inv.set(i)));
    });
  }
}

/** Shipments not billed yet: invoice them one by one or all at once. */
@Component({
  selector: 'app-to-invoice',
  imports: [FormsModule, RouterLink, LucideAngularModule],
  template: `
    <div class="page">
      <div class="page-head">
        <div>
          <div class="eyebrow">{{ i18n.t('nav.finance') }}</div>
          <h1 class="h1 mt-1">{{ i18n.t('toinv.title') }}</h1>
          <p class="mt-1 text-muted">{{ i18n.t('toinv.subtitle') }}</p>
        </div>
        <div class="flex items-end gap-2">
          <label class="field">
            <span class="label">{{ i18n.t('toinv.date') }}</span>
            <input class="input" type="date" [(ngModel)]="issueDate" />
          </label>
          <button class="btn btn-primary" (click)="all()" [disabled]="busy() || rows().length === 0">{{ i18n.t('toinv.all', { n: rows().length }) }}</button>
        </div>
      </div>
      <div class="card overflow-hidden">
        <table class="tbl">
          <thead>
            <tr>
              <th>{{ i18n.t('so.deliveryNote') }}</th>
              <th>{{ i18n.t('so.customer') }}</th>
              <th>{{ i18n.t('nav.salesOrders') }}</th>
              <th>{{ i18n.t('so.shipFrom') }}</th>
              <th>{{ i18n.t('ship.title') }}</th>
              <th class="w-36"></th>
            </tr>
          </thead>
          <tbody>
            @for (s of rows(); track s.id) {
              <tr>
                <td class="code">{{ s.number }}</td>
                <td class="font-medium">{{ s.customer }}</td>
                <td><a class="code hover:underline" [routerLink]="['/sales', s.salesOrderId]">{{ s.salesOrder }}</a></td>
                <td class="code">{{ s.warehouse }}</td>
                <td class="text-[13px] text-muted">{{ i18n.relative(s.shippedAt) }}</td>
                <td><button class="btn btn-sm" (click)="one(s.id)" [disabled]="busy()">{{ i18n.t('toinv.create') }}</button></td>
              </tr>
            } @empty {
              <tr><td colspan="6" class="!py-14 text-center text-muted">{{ i18n.t('toinv.empty') }}</td></tr>
            }
          </tbody>
        </table>
      </div>
    </div>
  `,
})
export class ToInvoicePage implements OnInit {
  i18n = inject(I18n);
  private api = inject(Api);
  private toasts = inject(Toasts);
  rows = signal<S['ShipmentListItem'][]>([]);
  busy = signal(false);
  issueDate = new Date().toISOString().slice(0, 10);

  async ngOnInit() {
    const page = await this.api.get<Page<S['ShipmentListItem']>>('/shipments', { uninvoiced: true, page: 1 });
    this.rows.set(page.items);
  }

  async one(id: string) {
    this.busy.set(true);
    try {
      const inv = await this.api.post<{ number: string }>(`/shipments/${id}/invoice`, { issueDate: this.issueDate });
      this.toasts.show(this.i18n.t('toinv.created', { number: inv.number }));
      await this.ngOnInit();
    } catch (e) {
      this.toasts.show(ApiError.from(e).message, 'error');
    } finally {
      this.busy.set(false);
    }
  }

  async all() {
    this.busy.set(true);
    try {
      for (const s of this.rows()) await this.api.post(`/shipments/${s.id}/invoice`, { issueDate: this.issueDate });
      await this.ngOnInit();
    } catch (e) {
      this.toasts.show(ApiError.from(e).message, 'error');
    } finally {
      this.busy.set(false);
    }
  }
}
