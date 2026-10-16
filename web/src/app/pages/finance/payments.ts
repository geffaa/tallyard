import { Component, computed, effect, inject, input, OnInit, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { ArrowLeft, LucideAngularModule, Wallet } from 'lucide-angular';
import { Api, ApiError, Page, S } from '../../core/api';
import { Auth } from '../../core/auth';
import { I18n } from '../../core/i18n';
import { Toasts } from '../../core/toast';
import { Pager } from '../../shared/ui';

interface PaymentRow {
  id: string;
  number: string;
  receivedOn: string;
  method: string;
  amount: number;
  reference: string | null;
  customer: string;
  invoices: string[];
}

@Component({
  selector: 'app-payments',
  imports: [RouterLink, LucideAngularModule, Pager],
  template: `
    <div class="page">
      <div class="page-head">
        <div>
          <div class="eyebrow">{{ i18n.t('nav.finance') }}</div>
          <h1 class="h1 mt-1">{{ i18n.t('pay.title') }}</h1>
          <p class="mt-1 text-muted">{{ i18n.t('pay.subtitle') }}</p>
        </div>
        <a class="btn btn-primary" routerLink="/payments/new"><lucide-icon [img]="PayIcon" [size]="16" />{{ i18n.t('pay.new') }}</a>
      </div>
      <div class="card overflow-hidden">
        <div class="overflow-x-auto">
          <table class="tbl">
            <thead>
              <tr>
                <th>{{ i18n.t('stock.document') }}</th>
                <th>{{ i18n.t('pay.customer') }}</th>
                <th>{{ i18n.t('pay.date') }}</th>
                <th>{{ i18n.t('pay.method') }}</th>
                <th>{{ i18n.t('pay.applyTo') }}</th>
                <th class="text-right">{{ i18n.t('pay.amount') }}</th>
              </tr>
            </thead>
            <tbody>
              @for (p of data()?.items ?? []; track p.id) {
                <tr>
                  <td class="code">{{ p.number }}</td>
                  <td class="font-medium">{{ p.customer }}</td>
                  <td class="tabular-nums text-[13px]">{{ p.receivedOn }}</td>
                  <td class="text-[13px]">{{ i18n.t($any('pay.method.' + p.method)) }} @if (p.reference) { <span class="code text-faint">{{ p.reference }}</span> }</td>
                  <td class="code text-[12px]">{{ p.invoices.join(', ') }}</td>
                  <td class="num text-ok">{{ i18n.money(p.amount, auth.currency()) }}</td>
                </tr>
              } @empty {
                <tr><td colspan="6" class="!py-14 text-center text-muted">{{ i18n.t('pay.empty') }}</td></tr>
              }
            </tbody>
          </table>
        </div>
        <app-pager [(page)]="page" [size]="25" [total]="data()?.total ?? 0" />
      </div>
    </div>
  `,
})
export class PaymentsPage {
  i18n = inject(I18n);
  auth = inject(Auth);
  private api = inject(Api);
  PayIcon = Wallet;
  page = signal(1);
  data = signal<Page<PaymentRow> | null>(null);

  constructor() {
    effect(() => {
      const page = this.page();
      untracked(() => this.api.get<Page<PaymentRow>>('/payments', { page }).then((d) => this.data.set(d)));
    });
  }
}

/** Record money received: pick the customer, then apply it to their open invoices. */
@Component({
  selector: 'app-payment-new',
  imports: [FormsModule, RouterLink, LucideAngularModule],
  template: `
    <div class="page-narrow">
      <a routerLink="/payments" class="inline-flex items-center gap-1.5 text-[13px] text-muted hover:text-ink">
        <lucide-icon [img]="BackIcon" [size]="15" />{{ i18n.t('pay.title') }}
      </a>
      <div class="page-head mt-3"><h1 class="h1">{{ i18n.t('pay.new') }}</h1></div>
      @if (error()) { <div class="mb-4 rounded-lg bg-danger-soft px-4 py-3 text-[13.5px] text-danger">{{ error() }}</div> }

      <section class="card mb-5">
        <div class="grid sm:grid-cols-2 gap-4 p-5">
          <label class="field sm:col-span-2">
            <span class="label">{{ i18n.t('pay.customer') }}</span>
            <select class="input" [ngModel]="customerId()" (ngModelChange)="pickCustomer($event)">
              <option value="" disabled>—</option>
              @for (c of customers(); track c.id) { <option [value]="c.id">{{ c.name }}</option> }
            </select>
          </label>
          <label class="field">
            <span class="label">{{ i18n.t('pay.date') }}</span>
            <input class="input" type="date" [(ngModel)]="receivedOn" />
          </label>
          <label class="field">
            <span class="label">{{ i18n.t('pay.method') }}</span>
            <select class="input" [(ngModel)]="method">
              @for (m of methods; track m) { <option [value]="m">{{ i18n.t($any('pay.method.' + m)) }}</option> }
            </select>
          </label>
          <label class="field sm:col-span-2">
            <span class="label">{{ i18n.t('pay.reference') }} <span class="text-faint font-normal">· {{ i18n.t('common.optional') }}</span></span>
            <input class="input input-mono" [(ngModel)]="reference" />
          </label>
        </div>
      </section>

      @if (customerId()) {
        <section class="card">
          <div class="card-head">
            <h2 class="card-title">{{ i18n.t('pay.applyTo') }}</h2>
            <span class="text-[13px] text-muted">{{ i18n.t('pay.open', { amount: i18n.money(openTotal(), auth.currency()) }) }}</span>
          </div>
          <table class="tbl">
            <thead>
              <tr>
                <th>{{ i18n.t('inv.number') }}</th>
                <th>{{ i18n.t('inv.due') }}</th>
                <th class="text-right">{{ i18n.t('inv.balance') }}</th>
                <th class="w-40">{{ i18n.t('pay.allocate') }}</th>
              </tr>
            </thead>
            <tbody>
              @for (i of open(); track i.id) {
                <tr>
                  <td class="code">{{ i.number }}</td>
                  <td class="tabular-nums text-[13px]" [class.text-danger]="i.daysOverdue > 0">{{ i.dueDate }}</td>
                  <td class="num">{{ i18n.money(i.balance, auth.currency()) }}</td>
                  <td><input class="input num !text-left h-8" type="number" min="0" [max]="i.balance" step="0.01" [(ngModel)]="alloc[i.id]" (ngModelChange)="touch()" /></td>
                </tr>
              }
            </tbody>
          </table>
          <div class="flex items-center justify-between gap-3 px-5 py-4 border-t border-line bg-paper/60 rounded-b-xl">
            <span class="text-[13.5px]">{{ i18n.t('pay.amount') }}: <span class="num font-semibold text-[15px]">{{ i18n.money(amount(), auth.currency()) }}</span></span>
            <button class="btn btn-primary" (click)="submit()" [disabled]="busy() || amount() <= 0"><lucide-icon [img]="PayIcon" [size]="16" />{{ i18n.t('pay.new') }}</button>
          </div>
        </section>
      }
    </div>
  `,
})
export class PaymentNewPage implements OnInit {
  i18n = inject(I18n);
  auth = inject(Auth);
  private api = inject(Api);
  private router = inject(Router);
  private toasts = inject(Toasts);
  customer = input<string>();
  invoice = input<string>();
  BackIcon = ArrowLeft;
  PayIcon = Wallet;
  methods = ['BankTransfer', 'Cash', 'Card', 'Check'];

  customers = signal<S['PartnerDto'][]>([]);
  customerId = signal('');
  open = signal<S['InvoiceListItem'][]>([]);
  alloc: Record<string, number> = {};
  private version = signal(0);
  amount = computed(() => (this.version(), Math.round(Object.values(this.alloc).reduce((s, v) => s + (v || 0), 0) * 100) / 100));
  openTotal = computed(() => this.open().reduce((s, i) => s + i.balance, 0));
  receivedOn = new Date().toISOString().slice(0, 10);
  method = 'BankTransfer';
  reference = '';
  busy = signal(false);
  error = signal('');

  async ngOnInit() {
    const c = await this.api.get<Page<S['PartnerDto']>>('/customers', { pageSize: 200 });
    this.customers.set(c.items);
    if (this.customer()) await this.pickCustomer(this.customer()!);
  }

  touch() {
    this.version.update((v) => v + 1);
  }

  async pickCustomer(id: string) {
    this.customerId.set(id);
    const list = await this.api.get<Page<S['InvoiceListItem']>>('/invoices', { status: 'open', customerId: id, page: 1 });
    // Oldest due first, as the server would allocate.
    const open = [...list.items].sort((a, b) => a.dueDate.localeCompare(b.dueDate));
    this.open.set(open);
    const only = this.invoice();
    this.alloc = Object.fromEntries(open.map((i) => [i.id, only ? (i.id === only ? i.balance : 0) : i.balance]));
    this.touch();
  }

  async submit() {
    this.busy.set(true);
    this.error.set('');
    try {
      const allocations = Object.entries(this.alloc).filter(([, a]) => a > 0).map(([invoiceId, amount]) => ({ invoiceId, amount }));
      const res = await this.api.post<{ number: string }>('/payments', {
        customerId: this.customerId(), amount: this.amount(), receivedOn: this.receivedOn, method: this.method,
        reference: this.reference || null, allocations,
      } satisfies S['PaymentInput']);
      this.toasts.show(this.i18n.t('pay.recorded', { number: res.number }));
      this.router.navigate(['/payments']);
    } catch (e) {
      this.error.set(ApiError.from(e).message);
    } finally {
      this.busy.set(false);
    }
  }
}

@Component({
  selector: 'app-aging',
  imports: [RouterLink],
  template: `
    <div class="page">
      <div class="page-head">
        <div>
          <div class="eyebrow">{{ i18n.t('nav.finance') }}</div>
          <h1 class="h1 mt-1">{{ i18n.t('aging.title') }}</h1>
          <p class="mt-1 text-muted">{{ i18n.t('aging.subtitle') }}</p>
        </div>
      </div>

      <div class="grid grid-cols-2 sm:grid-cols-5 gap-3 mb-5">
        @for (b of buckets; track b.key) {
          <div class="card px-4 py-3">
            <div class="eyebrow">{{ i18n.t(b.label) }}</div>
            <div class="mt-1 text-[18px] font-semibold tabular-nums" [class]="b.tone">{{ i18n.money(sum(b.key), auth.currency()) }}</div>
            <div class="mt-2 h-1.5 rounded-full bg-sunken overflow-hidden"><div class="h-full" [class]="b.bar" [style.width.%]="share(b.key)"></div></div>
          </div>
        }
      </div>

      <div class="card overflow-hidden">
        <div class="overflow-x-auto">
          <table class="tbl">
            <thead>
              <tr>
                <th>{{ i18n.t('so.customer') }}</th>
                @for (b of buckets; track b.key) { <th class="text-right">{{ i18n.t(b.label) }}</th> }
                <th class="text-right">{{ i18n.t('aging.total') }}</th>
              </tr>
            </thead>
            <tbody>
              @for (r of rows(); track r.customerId) {
                <tr>
                  <td>
                    <a class="font-medium hover:underline" routerLink="/payments/new" [queryParams]="{ customer: r.customerId }">{{ r.customer }}</a>
                    @if (r.creditLimit > 0 && r.total > r.creditLimit) { <span class="badge badge-danger ml-2">{{ i18n.t('aging.overLimit') }}</span> }
                  </td>
                  @for (b of buckets; track b.key) {
                    <td class="num" [class]="r[b.key] > 0 ? b.tone : 'text-faint'">{{ r[b.key] > 0 ? i18n.money(r[b.key], auth.currency()) : '—' }}</td>
                  }
                  <td class="num font-semibold">{{ i18n.money(r.total, auth.currency()) }}</td>
                </tr>
              } @empty {
                <tr><td colspan="7" class="!py-14 text-center text-muted">{{ i18n.t('aging.empty') }}</td></tr>
              }
            </tbody>
          </table>
        </div>
      </div>
    </div>
  `,
})
export class AgingPage implements OnInit {
  i18n = inject(I18n);
  auth = inject(Auth);
  private api = inject(Api);
  rows = signal<S['AgingRow'][]>([]);
  buckets = [
    { key: 'current', label: 'aging.current', tone: '', bar: 'bg-ok' },
    { key: 'days1To30', label: 'aging.d30', tone: 'text-signal-ink', bar: 'bg-signal' },
    { key: 'days31To60', label: 'aging.d60', tone: 'text-signal-ink', bar: 'bg-signal' },
    { key: 'days61To90', label: 'aging.d90', tone: 'text-danger', bar: 'bg-danger' },
    { key: 'over90', label: 'aging.over90', tone: 'text-danger', bar: 'bg-danger' },
  ] as const;
  private total = computed(() => this.rows().reduce((s, r) => s + r.total, 0));

  async ngOnInit() {
    this.rows.set(await this.api.get<S['AgingRow'][]>('/receivables/aging'));
  }

  sum(key: (typeof this.buckets)[number]['key']) {
    return this.rows().reduce((s, r) => s + r[key], 0);
  }

  share(key: (typeof this.buckets)[number]['key']) {
    return this.total() ? (this.sum(key) / this.total()) * 100 : 0;
  }
}
