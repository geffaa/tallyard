import { NgTemplateOutlet } from '@angular/common';
import { Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { FormArray, FormBuilder, FormsModule, NonNullableFormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { map } from 'rxjs';
import {
  ArrowLeft, Ban, CircleAlert, FileText, LucideAngularModule, PackageOpen, Pencil, Plus, ReceiptText, RefreshCw, Trash2, Truck, Unlock,
} from 'lucide-angular';
import { Api, ApiError, Page, S } from '../../core/api';
import { Auth } from '../../core/auth';
import { I18n } from '../../core/i18n';
import { Toasts } from '../../core/toast';
import { ProductPicker } from '../../shared/product-picker';
import { fieldError, Sheet } from '../../shared/ui';
import { SO_BADGE } from './list';

const lineGroup = (fb: NonNullableFormBuilder) =>
  fb.group({
    productId: ['', Validators.required],
    quantity: [1, [Validators.required, Validators.min(0.0001)]],
    unit: [''],
    unitPrice: [0, [Validators.required, Validators.min(0)]],
    discountPercent: [0, [Validators.min(0), Validators.max(100)]],
    taxRate: [0, [Validators.min(0), Validators.max(100)]],
  });
type LineGroup = ReturnType<typeof lineGroup>;

interface LineProduct {
  sku: string;
  name: string;
  baseUnit: string;
  salePrice: number;
  units: { name: string; factor: number }[];
  available: Record<string, number | undefined>;
}

@Component({
  selector: 'app-so',
  imports: [ReactiveFormsModule, FormsModule, RouterLink, NgTemplateOutlet, LucideAngularModule, ProductPicker, Sheet],
  template: `
    <div class="page !max-w-[1160px]">
      <a routerLink="/sales" class="inline-flex items-center gap-1.5 text-[13px] text-muted hover:text-ink">
        <lucide-icon [img]="I.back" [size]="15" />{{ i18n.t('so.title') }}
      </a>

      @if (editing()) {
        <form [formGroup]="form" (ngSubmit)="save(false)">
          <div class="page-head mt-3">
            <h1 class="h1">{{ so()?.number ?? i18n.t('so.newTitle') }}</h1>
            <div class="flex gap-2">
              @if (so()) { <button type="button" class="btn" (click)="editing.set(false)">{{ i18n.t('common.cancel') }}</button> }
              <button class="btn" [disabled]="busy()">{{ i18n.t('so.save') }}</button>
              <button type="button" class="btn btn-primary" [disabled]="busy()" (click)="save(true)">{{ i18n.t('so.confirm') }}</button>
            </div>
          </div>
          @if (error()) { <div class="mb-4 rounded-lg bg-danger-soft px-4 py-3 text-[13.5px] text-danger">{{ error() }}</div> }

          <div class="grid gap-5 lg:grid-cols-[minmax(0,1fr)_300px] mb-5">
            <section class="card">
              <div class="grid sm:grid-cols-2 gap-4 p-5">
                <label class="field">
                  <span class="label">{{ i18n.t('so.customer') }}</span>
                  <select class="input" formControlName="customerId" [class.invalid]="err('customerId')">
                    <option value="" disabled>—</option>
                    @for (c of customers(); track c.id) { <option [value]="c.id">{{ c.name }}</option> }
                  </select>
                </label>
                <label class="field">
                  <span class="label">{{ i18n.t('so.shipFrom') }}</span>
                  <select class="input" formControlName="warehouseId">
                    @for (w of warehouses(); track w.id) { <option [value]="w.id">{{ w.code }} · {{ w.name }}</option> }
                  </select>
                </label>
                <label class="field">
                  <span class="label">{{ i18n.t('so.reference') }} <span class="text-faint font-normal">· {{ i18n.t('common.optional') }}</span></span>
                  <input class="input input-mono" formControlName="customerReference" />
                </label>
                <label class="field">
                  <span class="label">{{ i18n.t('so.requested') }} <span class="text-faint font-normal">· {{ i18n.t('common.optional') }}</span></span>
                  <input class="input" type="date" formControlName="requestedOn" />
                </label>
              </div>
            </section>
            @if (credit(); as c) { <ng-container *ngTemplateOutlet="creditCard; context: { $implicit: c }" /> }
          </div>

          <section class="card">
            <div class="card-head">
              <h2 class="card-title">{{ i18n.t('po.lines') }}</h2>
              <button type="button" class="btn btn-sm" (click)="addLine()"><lucide-icon [img]="I.plus" [size]="14" />{{ i18n.t('po.addLine') }}</button>
            </div>
            <div class="overflow-x-auto">
              <table class="tbl" formArrayName="lines">
                <thead>
                  <tr>
                    <th class="min-w-[260px]">{{ i18n.t('po.lines') }}</th>
                    <th class="w-24">{{ i18n.t('po.qty') }}</th>
                    <th class="w-32">{{ i18n.t('po.unit') }}</th>
                    <th class="w-28">{{ i18n.t('po.price') }}</th>
                    <th class="w-20">{{ i18n.t('so.discount') }}</th>
                    <th class="text-right w-28">{{ i18n.t('po.amount') }}</th>
                    <th class="w-10"></th>
                  </tr>
                </thead>
                <tbody>
                  @for (line of lines.controls; track line; let i = $index) {
                    @let v = value().lines[i];
                    @let p = products()[v?.productId ?? ''];
                    <tr [formGroupName]="i" class="align-top">
                      <td>
                        <app-product-picker [placeholder]="i18n.t('adjust.pickProduct')" [label]="p ? p.sku + ' · ' + p.name : ''"
                                            [invalid]="!!err('lines.' + i + '.productId')" (picked)="pick(i, $event.id)" />
                        @if (p) {
                          @let free = p.available[value().warehouseId] ?? 0;
                          @let need = (v?.quantity ?? 0) * factor(p, v?.unit);
                          <span class="hint" [class.!text-danger]="need > free">{{ i18n.t('stock.available') }}: {{ i18n.number(free) }} {{ p.baseUnit }}</span>
                        }
                      </td>
                      <td><input class="input num !text-left" type="number" min="0" step="any" formControlName="quantity" /></td>
                      <td>
                        <select class="input" formControlName="unit" (change)="unitChanged(i)">
                          @if (p) {
                            <option [value]="p.baseUnit">{{ p.baseUnit }}</option>
                            @for (u of p.units; track u.name) { <option [value]="u.name">{{ u.name }} ({{ u.factor }})</option> }
                          }
                        </select>
                      </td>
                      <td><input class="input num !text-left" type="number" min="0" step="0.01" formControlName="unitPrice" /></td>
                      <td><input class="input num !text-left" type="number" min="0" max="100" step="0.5" formControlName="discountPercent" /></td>
                      <td class="num !pt-4">{{ i18n.money(net(v), auth.currency()) }}</td>
                      <td><button type="button" class="btn btn-ghost btn-sm h-9" (click)="lines.removeAt(i)"><lucide-icon [img]="I.trash" [size]="15" /></button></td>
                    </tr>
                  }
                </tbody>
              </table>
            </div>
            <div class="flex justify-end border-t border-line px-5 py-4">
              <dl class="grid grid-cols-[auto_auto] gap-x-8 gap-y-1 text-[13.5px]">
                <dt class="text-muted">{{ i18n.t('po.subtotal') }}</dt><dd class="num">{{ i18n.money(totals().net, auth.currency()) }}</dd>
                <dt class="text-muted">{{ i18n.t('po.taxTotal') }}</dt><dd class="num">{{ i18n.money(totals().tax, auth.currency()) }}</dd>
                <dt class="font-semibold pt-1 border-t border-line">{{ i18n.t('po.total') }}</dt>
                <dd class="num font-semibold text-[15px] pt-1 border-t border-line">{{ i18n.money(totals().net + totals().tax, auth.currency()) }}</dd>
              </dl>
            </div>
          </section>
        </form>
      } @else if (so(); as o) {
        <div class="page-head mt-3">
          <div>
            <h1 class="h1 font-mono">{{ o.number }}</h1>
            <p class="mt-1.5 flex flex-wrap items-center gap-2 text-[13px] text-muted">
              <span class="badge" [class]="badge[o.status]">{{ i18n.t($any('so.status.' + o.status)) }}</span>
              <span class="font-medium text-ink">{{ o.customer }}</span>
              @if (o.customerReference) { · <span class="code">{{ o.customerReference }}</span> }
              · <span class="code">{{ o.warehouse }}</span> · {{ o.orderDate }}
            </p>
          </div>
          <div class="flex flex-wrap gap-2">
            @if (has('cancel')) { <button class="btn btn-ghost btn-danger" (click)="act('cancel')" [disabled]="busy()"><lucide-icon [img]="I.ban" [size]="15" />{{ i18n.t('so.cancel') }}</button> }
            @if (has('close')) { <button class="btn" (click)="act('close')" [disabled]="busy()">{{ i18n.t('so.close') }}</button> }
            @if (has('edit')) { <button class="btn" (click)="editing.set(true)"><lucide-icon [img]="I.edit" [size]="15" />{{ i18n.t('so.edit') }}</button> }
            @if (has('reserve')) { <button class="btn" (click)="act('reserve')" [disabled]="busy()"><lucide-icon [img]="I.refresh" [size]="15" />{{ i18n.t('so.reserve') }}</button> }
            @if (has('confirm')) { <button class="btn btn-primary" (click)="confirmOrder()" [disabled]="busy()">{{ i18n.t('so.confirm') }}</button> }
            @if (has('release')) { <button class="btn btn-primary" (click)="act('release')" [disabled]="busy()"><lucide-icon [img]="I.unlock" [size]="15" />{{ i18n.t('so.release') }}</button> }
            @if (has('ship')) { <button class="btn btn-primary" (click)="openShip()"><lucide-icon [img]="I.truck" [size]="15" />{{ i18n.t('so.ship') }}</button> }
          </div>
        </div>

        @if (o.status === 'OnHold' && o.holdReason) {
          <div class="mb-5 flex items-start gap-3 rounded-lg bg-danger-soft px-4 py-3 text-[13.5px] text-danger">
            <lucide-icon [img]="I.alert" [size]="17" class="mt-0.5 shrink-0" />
            <div><div class="font-semibold">{{ i18n.t('so.held') }}</div>{{ i18n.t($any('so.hold.' + o.holdReason)) }}</div>
          </div>
        }
        @if (o.releaseNote) {
          <div class="mb-5 rounded-lg bg-ok-soft px-4 py-3 text-[13.5px] text-ok">{{ i18n.t('so.releasedBy', { who: o.releasedBy ?? '', note: o.releaseNote }) }}</div>
        }

        <div class="grid gap-5 lg:grid-cols-[minmax(0,1fr)_300px]">
          <div class="flex flex-col gap-5 min-w-0">
            <section class="card overflow-hidden">
              <div class="overflow-x-auto">
                <table class="tbl">
                  <thead>
                    <tr>
                      <th>{{ i18n.t('products.sku') }}</th>
                      <th>{{ i18n.t('common.name') }}</th>
                      <th class="text-right">{{ i18n.t('po.qty') }}</th>
                      @if (o.total != null) { <th class="text-right">{{ i18n.t('po.amount') }}</th> }
                      <th class="text-right">{{ i18n.t('ship.reserved') }}</th>
                      <th class="text-right">{{ i18n.t('so.shipped') }}</th>
                      <th class="text-right">{{ i18n.t('so.backordered') }}</th>
                    </tr>
                  </thead>
                  <tbody>
                    @for (l of o.lines; track l.id) {
                      <tr>
                        <td class="whitespace-nowrap"><a class="code hover:underline" [routerLink]="['/stock', l.productId]">{{ l.sku }}</a></td>
                        <td>{{ l.name }}</td>
                        <td class="num whitespace-nowrap">{{ i18n.number(l.quantity, 4) }} <span class="text-faint font-sans text-[12px]">{{ l.unit }}</span></td>
                        @if (o.total != null) {
                          <td class="num">
                            {{ i18n.money(l.net ?? 0, auth.currency()) }}
                            @if (l.discountPercent) { <div class="text-[11.5px] text-ok">−{{ l.discountPercent }}%</div> }
                          </td>
                        }
                        <td class="num" [class.text-brand-ink]="l.reserved > 0">{{ l.reserved ? i18n.number(l.reserved, 4) : '—' }}</td>
                        <td class="num" [class.text-ok]="l.shipped > 0">{{ l.shipped ? i18n.number(l.shipped, 4) : '—' }}</td>
                        <td class="num">
                          @if (l.backordered > 0 && o.status !== 'Draft' && o.status !== 'OnHold' && o.status !== 'Cancelled') {
                            <span class="badge badge-signal">{{ i18n.number(l.backordered, 4) }}</span>
                          } @else { — }
                        </td>
                      </tr>
                    }
                  </tbody>
                </table>
              </div>
              @if (o.total != null) {
                <div class="flex justify-end border-t border-line px-5 py-4">
                  <dl class="grid grid-cols-[auto_auto] gap-x-8 gap-y-1 text-[13.5px]">
                    <dt class="text-muted">{{ i18n.t('po.subtotal') }}</dt><dd class="num">{{ i18n.money(o.subtotal ?? 0, auth.currency()) }}</dd>
                    <dt class="text-muted">{{ i18n.t('po.taxTotal') }}</dt><dd class="num">{{ i18n.money(o.taxTotal ?? 0, auth.currency()) }}</dd>
                    <dt class="font-semibold pt-1 border-t border-line">{{ i18n.t('po.total') }}</dt>
                    <dd class="num font-semibold text-[15px] pt-1 border-t border-line">{{ i18n.money(o.total, auth.currency()) }}</dd>
                  </dl>
                </div>
              }
            </section>

            <section class="card">
              <div class="card-head"><h2 class="card-title">{{ i18n.t('so.shipments') }}</h2></div>
              <ul class="divide-y divide-line">
                @for (s of o.shipments; track s.id) {
                  <li class="flex flex-wrap items-center gap-x-4 gap-y-2 px-5 py-3 text-[13.5px]">
                    <span class="code">{{ s.number }}</span>
                    <span class="text-muted">{{ s.shippedBy }} · {{ i18n.relative(s.shippedAt) }}</span>
                    <span class="ml-auto flex items-center gap-2">
                      <a class="btn btn-sm" [href]="'/api/shipments/' + s.id + '/pdf'" target="_blank"><lucide-icon [img]="I.file" [size]="14" />{{ i18n.t('so.deliveryNote') }}</a>
                      @if (s.invoiceNumber) {
                        @if (auth.can('ViewSalesPrices')) { <a class="badge badge-ok" [routerLink]="['/invoices', s.invoiceId]">{{ s.invoiceNumber }}</a> }
                      } @else if (auth.can('ManageReceivables')) {
                        <button class="btn btn-sm btn-primary" (click)="invoice(s.id)" [disabled]="busy()"><lucide-icon [img]="I.receipt" [size]="14" />{{ i18n.t('so.invoiceIt') }}</button>
                      }
                    </span>
                  </li>
                } @empty {
                  <li class="px-5 py-4 text-muted text-[13px]">{{ i18n.t('so.noShipments') }}</li>
                }
              </ul>
            </section>
          </div>
          <aside class="flex flex-col gap-5">
            @if (o.credit; as c) { <ng-container *ngTemplateOutlet="creditCard; context: { $implicit: c }" /> }
            @if (o.note) { <section class="card p-5 text-[13.5px]"><div class="label mb-1">{{ i18n.t('so.note') }}</div>{{ o.note }}</section> }
          </aside>
        </div>
      }
    </div>

    <ng-template #creditCard let-c>
      <section class="card">
        <div class="card-head"><h2 class="card-title">{{ i18n.t('so.credit') }}</h2></div>
        <dl class="grid grid-cols-[1fr_auto] gap-x-4 gap-y-1.5 p-5 text-[13px]">
          <dt class="text-muted">{{ i18n.t('so.credit.limit') }}</dt>
          <dd class="num">{{ c.limit ? i18n.money(c.limit, auth.currency()) : i18n.t('so.cashOnly') }}</dd>
          <dt class="text-muted">{{ i18n.t('so.credit.invoices') }}</dt><dd class="num">{{ i18n.money(c.openInvoices, auth.currency()) }}</dd>
          <dt class="text-muted">{{ i18n.t('so.credit.orders') }}</dt><dd class="num">{{ i18n.money(c.openOrders, auth.currency()) }}</dd>
          <dt class="text-muted">{{ i18n.t('so.credit.this') }}</dt><dd class="num">{{ i18n.money(c.thisOrder, auth.currency()) }}</dd>
          <dt class="font-semibold pt-1.5 border-t border-line">{{ i18n.t('so.credit.exposure') }}</dt>
          <dd class="num font-semibold pt-1.5 border-t border-line" [class.text-danger]="c.exposure > c.limit">{{ i18n.money(c.exposure, auth.currency()) }}</dd>
        </dl>
        <div class="px-5 pb-4 -mt-1">
          <div class="h-2 rounded-full bg-sunken overflow-hidden">
            <div class="h-full" [class]="c.exposure > c.limit ? 'bg-danger' : 'bg-ok'" [style.width.%]="c.limit ? Math.min(100, c.exposure / c.limit * 100) : 100"></div>
          </div>
          <p class="mt-2 text-[12.5px]" [class]="c.exposure > c.limit || c.overdueInvoices ? 'text-danger' : 'text-ok'">
            @if (c.overdueInvoices) { {{ i18n.t('so.credit.overdue', { n: c.overdueInvoices }) }} }
            @else if (c.exposure > c.limit) { {{ i18n.t('so.credit.over', { amount: i18n.money(c.exposure - c.limit, auth.currency()) }) }} }
            @else { {{ i18n.t('so.credit.ok') }} }
          </p>
        </div>
      </section>
    </ng-template>

    @if (shipping()) {
      <app-sheet [title]="i18n.t('ship.title')" (closed)="shipping.set(false)">
        <p class="hint mb-4">{{ i18n.t('ship.hint') }}</p>
        <div class="flex flex-col gap-3">
          @for (p of pickList(); track p.lineId) {
            <div class="rounded-lg border border-line p-3">
              <div class="flex items-start justify-between gap-3">
                <div class="min-w-0">
                  <div class="code">{{ p.sku }}</div>
                  <div class="text-[13.5px] truncate">{{ p.name }}</div>
                  <div class="hint">{{ i18n.t('ship.reserved') }}: {{ i18n.number(p.reserved, 4) }} {{ p.unit }}</div>
                </div>
                <div class="flex items-center gap-2 shrink-0">
                  <input class="input num !text-left w-24" type="number" min="0" step="any" [max]="p.reserved" [(ngModel)]="shipQty[p.lineId]" />
                  <span class="text-[12.5px] text-muted">{{ p.unit }}</span>
                </div>
              </div>
              @if (p.batches[0]?.batch) {
                <div class="mt-2 pt-2 border-t border-line text-[12px] text-muted">
                  {{ i18n.t('ship.pick') }}:
                  @for (b of p.batches; track b.batch) { <span class="code ml-1.5">{{ b.batch }}</span> <span class="text-faint">({{ i18n.number(b.quantityBase) }}, {{ b.expiresOn }})</span> }
                </div>
              }
            </div>
          }
          <div class="grid grid-cols-2 gap-3 mt-2">
            <label class="field"><span class="label">{{ i18n.t('ship.carrier') }}</span><input class="input" [(ngModel)]="carrier" /></label>
            <label class="field"><span class="label">{{ i18n.t('ship.tracking') }}</span><input class="input input-mono" [(ngModel)]="tracking" /></label>
          </div>
        </div>
        <ng-container actions>
          <button class="btn" (click)="shipping.set(false)">{{ i18n.t('common.cancel') }}</button>
          <button class="btn btn-primary" (click)="ship()" [disabled]="busy()"><lucide-icon [img]="I.truck" [size]="15" />{{ i18n.t('ship.submit') }}</button>
        </ng-container>
      </app-sheet>
    }
  `,
})
export class SoPage {
  i18n = inject(I18n);
  auth = inject(Auth);
  private api = inject(Api);
  private router = inject(Router);
  private toasts = inject(Toasts);
  private fb = inject(FormBuilder).nonNullable;
  Math = Math;

  id = input<string>();
  I = {
    back: ArrowLeft, plus: Plus, trash: Trash2, edit: Pencil, ban: Ban, refresh: RefreshCw, unlock: Unlock, truck: Truck,
    alert: CircleAlert, file: FileText, receipt: ReceiptText, open: PackageOpen,
  };
  badge = SO_BADGE;

  so = signal<S['SoDto'] | null>(null);
  editing = signal(false);
  busy = signal(false);
  error = signal('');
  customers = signal<S['PartnerDto'][]>([]);
  warehouses = signal<S['WarehouseDto'][]>([]);
  products = signal<Record<string, LineProduct>>({});
  credit = signal<S['CreditDto'] | null>(null);
  shipping = signal(false);
  pickList = signal<S['PickLine'][]>([]);
  shipQty: Record<string, number> = {};
  carrier = '';
  tracking = '';
  private shipKey = crypto.randomUUID();

  form = this.fb.group({
    customerId: ['', Validators.required],
    warehouseId: ['', Validators.required],
    customerReference: [''],
    requestedOn: [''],
    note: [''],
    lines: this.fb.array<LineGroup>([]),
  });
  get lines() {
    return this.form.controls.lines as FormArray<LineGroup>;
  }
  value = toSignal(this.form.valueChanges.pipe(map(() => this.form.getRawValue())), { initialValue: this.form.getRawValue() });
  totals = computed(() =>
    this.value().lines.reduce((t, l) => {
      const n = this.net(l);
      return { net: t.net + n, tax: t.tax + Math.round(n * (l.taxRate || 0)) / 100 };
    }, { net: 0, tax: 0 }),
  );

  constructor() {
    Promise.all([
      this.api.get<Page<S['PartnerDto']>>('/customers', { pageSize: 200 }),
      this.api.get<S['WarehouseDto'][]>('/warehouses'),
    ]).then(([c, w]) => {
      this.customers.set(c.items.filter((x) => x.isActive));
      this.warehouses.set(w.filter((x) => x.isActive));
      if (!this.form.controls.warehouseId.value) this.form.controls.warehouseId.setValue(this.warehouses()[0]?.id ?? '');
    });
    effect(() => {
      const id = this.id();
      untracked(() => (id ? this.load(id) : (this.editing.set(true), this.addLine())));
    });
    // Live credit preview while composing an order.
    effect(() => {
      const customerId = this.value().customerId;
      const amount = this.totals().net + this.totals().tax;
      if (!customerId || !this.editing() || !this.auth.can('ViewSalesPrices')) return;
      untracked(() => this.api.get<S['CreditDto']>(`/sales-orders/credit/${customerId}`, { amount }).then((c) => this.credit.set(c)));
    });
  }

  has(a: string) {
    return this.so()?.actions.includes(a) ?? false;
  }

  net(l: ReturnType<LineGroup['getRawValue']> | undefined) {
    return l ? Math.round((l.quantity || 0) * (l.unitPrice || 0) * (1 - (l.discountPercent || 0) / 100) * 100) / 100 : 0;
  }

  factor(p: LineProduct, unit: string | undefined) {
    return p.units.find((u) => u.name === unit)?.factor ?? 1;
  }

  private async load(id: string) {
    const o = await this.api.get<S['SoDto']>(`/sales-orders/${id}`);
    this.so.set(o);
    this.editing.set(false);
    this.lines.clear();
    await Promise.all(o.lines.map((l) => this.loadProduct(l.productId)));
    for (const l of o.lines) {
      const g = lineGroup(this.fb);
      g.patchValue({ productId: l.productId, quantity: l.quantity, unit: l.unit, unitPrice: l.unitPrice ?? 0, discountPercent: l.discountPercent ?? 0, taxRate: l.taxRate ?? 0 });
      this.lines.push(g);
    }
    this.form.patchValue({ customerId: o.customerId, warehouseId: o.warehouseId, customerReference: o.customerReference ?? '', requestedOn: o.requestedOn ?? '', note: o.note ?? '' });
  }

  private async loadProduct(productId: string) {
    const [d, s] = await Promise.all([
      this.api.get<S['ProductDto']>(`/products/${productId}`),
      this.api.get<S['ProductStock']>(`/stock/${productId}`),
    ]);
    const info: LineProduct = {
      sku: d.sku, name: d.name, baseUnit: d.baseUnit, salePrice: d.salePrice, units: d.units,
      available: Object.fromEntries(s.warehouses.map((w) => [w.warehouseId, w.available])),
    };
    this.products.update((m) => ({ ...m, [productId]: info }));
    return { d, info };
  }

  addLine() {
    this.lines.push(lineGroup(this.fb));
  }

  async pick(i: number, productId: string) {
    const { d } = await this.loadProduct(productId);
    this.lines.at(i).patchValue({ productId, unit: d.baseUnit, unitPrice: d.salePrice, taxRate: d.taxRate });
  }

  /** Changing the unit reprices the line from the product's base price. */
  unitChanged(i: number) {
    const v = this.lines.at(i).getRawValue();
    const p = this.products()[v.productId];
    if (p) this.lines.at(i).controls.unitPrice.setValue(Math.round(p.salePrice * this.factor(p, v.unit) * 100) / 100);
  }

  err(path: string) {
    return fieldError(this.form.get(path), this.i18n);
  }

  async save(confirmAfter: boolean) {
    this.form.markAllAsTouched();
    if (this.form.invalid || this.lines.length === 0) {
      this.error.set(this.i18n.t('form.fix'));
      return;
    }
    const v = this.form.getRawValue();
    const body: S['SoInput'] = {
      customerId: v.customerId, warehouseId: v.warehouseId, requestedOn: v.requestedOn || null,
      customerReference: v.customerReference || null, note: v.note || null, version: this.so()?.version ?? null,
      lines: v.lines.map((l) => ({ productId: l.productId, quantity: l.quantity, unit: l.unit || null, unitPrice: l.unitPrice, discountPercent: l.discountPercent, taxRate: l.taxRate })),
    };
    this.busy.set(true);
    this.error.set('');
    try {
      const existing = this.so();
      const saved = existing
        ? await this.api.put<S['SoDto']>(`/sales-orders/${existing.id}`, body)
        : await this.api.post<S['SoDto']>('/sales-orders', body);
      this.so.set(saved);
      this.editing.set(false);
      if (confirmAfter) await this.confirmOrder();
      else this.toasts.show(this.i18n.t('so.saved'));
      if (!existing) this.router.navigate(['/sales', saved.id], { replaceUrl: true });
    } catch (e) {
      this.error.set(ApiError.from(e).message);
    } finally {
      this.busy.set(false);
    }
  }

  /** Confirm; when stock is short, offer to back-order the rest instead of failing. */
  async confirmOrder() {
    const id = this.so()!.id;
    const go = (allowBackorder: boolean) => this.api.post<S['SoDto']>(`/sales-orders/${id}/confirm`, { allowBackorder });
    this.busy.set(true);
    try {
      let result: S['SoDto'];
      try {
        result = await go(false);
      } catch (e) {
        const err = ApiError.from(e);
        if (err.code !== 'insufficient_stock' || !confirm(`${err.message}\n\n${this.i18n.t('so.short')}`)) throw err;
        result = await go(true);
      }
      this.so.set(result);
      this.toasts.show(this.i18n.t(result.status === 'OnHold' ? 'so.held' : 'so.confirmed'), result.status === 'OnHold' ? 'error' : 'ok');
    } catch (e) {
      this.toasts.show(ApiError.from(e).message, 'error');
    } finally {
      this.busy.set(false);
    }
  }

  async act(action: 'release' | 'reserve' | 'cancel' | 'close') {
    const o = this.so()!;
    let body: object = {};
    if (action === 'release') {
      const note = prompt(this.i18n.t('so.releasePrompt'));
      if (!note?.trim()) return;
      body = { note };
    } else if (action === 'cancel' && !confirm(this.i18n.t('so.cancelConfirm'))) return;
    else if (action === 'close' && !confirm(this.i18n.t('so.closeConfirm'))) return;
    this.busy.set(true);
    try {
      this.so.set(await this.api.post<S['SoDto']>(`/sales-orders/${o.id}/${action}`, body));
      const toast = { release: 'so.released', reserve: 'so.reservedToast', cancel: 'so.cancelled', close: 'so.closed' } as const;
      this.toasts.show(this.i18n.t(toast[action]));
    } catch (e) {
      this.toasts.show(ApiError.from(e).message, 'error');
    } finally {
      this.busy.set(false);
    }
  }

  async openShip() {
    const list = await this.api.get<S['PickLine'][]>(`/sales-orders/${this.so()!.id}/pick-list`);
    this.pickList.set(list);
    this.shipQty = Object.fromEntries(list.map((p) => [p.lineId, p.reserved]));
    this.shipKey = crypto.randomUUID();
    this.shipping.set(true);
  }

  async ship() {
    const o = this.so()!;
    this.busy.set(true);
    try {
      const res = await this.api.post<{ id: string; number: string }>(`/sales-orders/${o.id}/ship`, {
        lines: Object.entries(this.shipQty).filter(([, q]) => q > 0).map(([lineId, quantity]) => ({ lineId, quantity })),
        carrier: this.carrier || null, trackingNumber: this.tracking || null, idempotencyKey: this.shipKey,
      } satisfies S['ShipInput']);
      this.toasts.show(this.i18n.t('ship.posted', { number: res.number }));
      this.shipping.set(false);
      window.open(`/api/shipments/${res.id}/pdf`, '_blank');
      await this.load(o.id);
    } catch (e) {
      this.toasts.show(ApiError.from(e).message, 'error');
    } finally {
      this.busy.set(false);
    }
  }

  async invoice(shipmentId: string) {
    this.busy.set(true);
    try {
      const inv = await this.api.post<{ id: string; number: string }>(`/shipments/${shipmentId}/invoice`, { issueDate: null });
      this.toasts.show(this.i18n.t('so.invoiced', { number: inv.number }));
      await this.load(this.so()!.id);
    } catch (e) {
      this.toasts.show(ApiError.from(e).message, 'error');
    } finally {
      this.busy.set(false);
    }
  }
}
