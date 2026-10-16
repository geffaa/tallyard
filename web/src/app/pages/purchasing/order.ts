import { Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { FormArray, FormBuilder, NonNullableFormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { map } from 'rxjs';
import {
  ArrowLeft, Ban, Check, CircleCheck, Clock, Download, FileCheck, LucideAngularModule, PackageCheck, Pencil, Plus, Send, Trash2, X,
} from 'lucide-angular';
import { Api, ApiError, Page, S } from '../../core/api';
import { Auth } from '../../core/auth';
import { I18n } from '../../core/i18n';
import { Toasts } from '../../core/toast';
import { ProductPicker } from '../../shared/product-picker';
import { fieldError } from '../../shared/ui';
import { PO_BADGE } from './list';

const lineGroup = (fb: NonNullableFormBuilder) =>
  fb.group({
    productId: ['', Validators.required],
    quantity: [1, [Validators.required, Validators.min(0.0001)]],
    unit: [''],
    unitPrice: [0, [Validators.required, Validators.min(0)]],
    taxRate: [0, [Validators.min(0), Validators.max(100)]],
  });
type LineGroup = ReturnType<typeof lineGroup>;

interface LineProduct {
  sku: string;
  name: string;
  baseUnit: string;
  units: { name: string; factor: number }[];
}

@Component({
  selector: 'app-po',
  imports: [ReactiveFormsModule, RouterLink, LucideAngularModule, ProductPicker],
  template: `
    <div class="page !max-w-[1120px]">
      <a routerLink="/purchasing" class="inline-flex items-center gap-1.5 text-[13px] text-muted hover:text-ink">
        <lucide-icon [img]="I.back" [size]="15" />{{ i18n.t('po.title') }}
      </a>

      @if (editing()) {
        <!-- Editor (new order or draft) -->
        <form [formGroup]="form" (ngSubmit)="save(false)">
          <div class="page-head mt-3">
            <div>
              <h1 class="h1">{{ po()?.number ?? i18n.t('po.newTitle') }}</h1>
              @if (po(); as p) { <p class="mt-1"><span class="badge" [class]="badge[p.status]">{{ i18n.t($any('po.status.' + p.status)) }}</span></p> }
            </div>
            <div class="flex gap-2">
              @if (po()) { <button type="button" class="btn" (click)="editing.set(false)">{{ i18n.t('common.cancel') }}</button> }
              <button class="btn" [disabled]="busy()">{{ i18n.t('po.saveDraft') }}</button>
              <button type="button" class="btn btn-primary" [disabled]="busy()" (click)="save(true)">
                <lucide-icon [img]="I.send" [size]="15" />{{ i18n.t('po.submit') }}
              </button>
            </div>
          </div>
          @if (po()?.rejectionReason; as reason) {
            <div class="mb-4 rounded-lg bg-signal-soft px-4 py-3 text-[13.5px] text-signal-ink">{{ i18n.t('po.rejectedBanner', { who: po()!.rejectedBy ?? '', reason }) }}</div>
          }
          @if (error()) { <div class="mb-4 rounded-lg bg-danger-soft px-4 py-3 text-[13.5px] text-danger">{{ error() }}</div> }

          <section class="card mb-5">
            <div class="grid sm:grid-cols-3 gap-4 p-5">
              <label class="field">
                <span class="label">{{ i18n.t('po.supplier') }}</span>
                <select class="input" formControlName="supplierId" [class.invalid]="err('supplierId')">
                  <option value="" disabled>{{ i18n.t('po.pickSupplier') }}</option>
                  @for (s of suppliers(); track s.id) { <option [value]="s.id">{{ s.name }}</option> }
                </select>
                @if (err('supplierId'); as e) { <span class="error">{{ e }}</span> }
              </label>
              <label class="field">
                <span class="label">{{ i18n.t('po.deliverTo') }}</span>
                <select class="input" formControlName="warehouseId">
                  @for (w of warehouses(); track w.id) { <option [value]="w.id">{{ w.code }} · {{ w.name }}</option> }
                </select>
              </label>
              <label class="field">
                <span class="label">{{ i18n.t('po.expected') }} <span class="text-faint font-normal">· {{ i18n.t('common.optional') }}</span></span>
                <input class="input" type="date" formControlName="expectedOn" />
              </label>
              <label class="field sm:col-span-3">
                <span class="label">{{ i18n.t('po.note') }} <span class="text-faint font-normal">· {{ i18n.t('common.optional') }}</span></span>
                <textarea class="input" rows="2" formControlName="note"></textarea>
              </label>
            </div>
          </section>

          <section class="card">
            <div class="card-head">
              <h2 class="card-title">{{ i18n.t('po.lines') }}</h2>
              <button type="button" class="btn btn-sm" (click)="addLine()"><lucide-icon [img]="I.plus" [size]="14" />{{ i18n.t('po.addLine') }}</button>
            </div>
            <div class="overflow-x-auto">
              <table class="tbl" formArrayName="lines">
                <thead>
                  <tr>
                    <th class="min-w-[280px]">{{ i18n.t('po.lines') }}</th>
                    <th class="w-24">{{ i18n.t('po.qty') }}</th>
                    <th class="w-32">{{ i18n.t('po.unit') }}</th>
                    <th class="w-32">{{ i18n.t('po.price') }}</th>
                    <th class="w-20">{{ i18n.t('po.tax') }}</th>
                    <th class="text-right w-32">{{ i18n.t('po.amount') }}</th>
                    <th class="w-10"></th>
                  </tr>
                </thead>
                <tbody>
                  @for (line of lines.controls; track line; let i = $index) {
                    @let v = value().lines[i];
                    @let p = products()[v?.productId ?? ''];
                    <tr [formGroupName]="i" class="align-top">
                      <td><app-product-picker [placeholder]="i18n.t('adjust.pickProduct')" [label]="p ? p.sku + ' · ' + p.name : ''"
                                              [invalid]="!!err('lines.' + i + '.productId')" (picked)="pick(i, $event.id)" /></td>
                      <td><input class="input num !text-left" type="number" min="0" step="any" formControlName="quantity" [class.invalid]="err('lines.' + i + '.quantity')" /></td>
                      <td>
                        <select class="input" formControlName="unit">
                          @if (p) {
                            <option [value]="p.baseUnit">{{ p.baseUnit }}</option>
                            @for (u of p.units; track u.name) { <option [value]="u.name">{{ u.name }} ({{ u.factor }})</option> }
                          }
                        </select>
                      </td>
                      <td><input class="input num !text-left" type="number" min="0" step="0.01" formControlName="unitPrice" [class.invalid]="err('lines.' + i + '.unitPrice')" /></td>
                      <td><input class="input num !text-left" type="number" min="0" max="100" step="0.01" formControlName="taxRate" /></td>
                      <td class="num !pt-4">{{ i18n.money(lineNet(v), auth.currency()) }}</td>
                      <td><button type="button" class="btn btn-ghost btn-sm h-9" (click)="lines.removeAt(i)" [attr.aria-label]="i18n.t('common.remove')"><lucide-icon [img]="I.trash" [size]="15" /></button></td>
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
      } @else if (po(); as p) {
        <!-- Read view -->
        <div class="page-head mt-3">
          <div>
            <h1 class="h1 font-mono">{{ p.number }}</h1>
            <p class="mt-1.5 flex flex-wrap items-center gap-2 text-[13px] text-muted">
              <span class="badge" [class]="badge[p.status]">{{ i18n.t($any('po.status.' + p.status)) }}</span>
              <span class="font-medium text-ink">{{ p.supplier }}</span> → <span class="code">{{ p.warehouse }}</span>
              @if (p.expectedOn) { · {{ i18n.t('po.expected') }} {{ p.expectedOn }} }
            </p>
          </div>
          <div class="flex flex-wrap gap-2">
            @if (has('cancel')) { <button class="btn btn-ghost btn-danger" (click)="act('cancel')" [disabled]="busy()"><lucide-icon [img]="I.ban" [size]="15" />{{ i18n.t('po.cancel') }}</button> }
            @if (has('close')) { <button class="btn" (click)="act('close')" [disabled]="busy()">{{ i18n.t('po.close') }}</button> }
            @if (has('edit')) { <button class="btn" (click)="editing.set(true)"><lucide-icon [img]="I.edit" [size]="15" />{{ i18n.t('po.edit') }}</button> }
            @if (has('submit')) { <button class="btn btn-primary" (click)="act('submit')" [disabled]="busy()"><lucide-icon [img]="I.send" [size]="15" />{{ i18n.t('po.submit') }}</button> }
            @if (has('reject')) { <button class="btn btn-danger" (click)="act('reject')" [disabled]="busy()"><lucide-icon [img]="I.x" [size]="15" />{{ i18n.t('po.reject') }}</button> }
            @if (has('approve')) { <button class="btn btn-primary" (click)="act('approve')" [disabled]="busy()"><lucide-icon [img]="I.check" [size]="15" />{{ i18n.t('po.approve') }}</button> }
            @if (has('pdf')) { <a class="btn" [href]="'/api/purchase-orders/' + p.id + '/pdf'" target="_blank"><lucide-icon [img]="I.download" [size]="15" />{{ i18n.t('po.pdf') }}</a> }
            @if (has('send')) { <button class="btn btn-primary" (click)="act('send')" [disabled]="busy()"><lucide-icon [img]="I.send" [size]="15" />{{ i18n.t('po.send') }}</button> }
            @if (has('receive')) { <a class="btn btn-primary" [routerLink]="['/purchasing', p.id, 'receive']"><lucide-icon [img]="I.receive" [size]="15" />{{ i18n.t('po.receive') }}</a> }
          </div>
        </div>

        @if (p.status === 'Draft' && p.rejectionReason) {
          <div class="mb-5 rounded-lg bg-signal-soft px-4 py-3 text-[13.5px] text-signal-ink">{{ i18n.t('po.rejectedBanner', { who: p.rejectedBy ?? '', reason: p.rejectionReason }) }}</div>
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
                      @if (p.total != null) { <th class="text-right">{{ i18n.t('po.price') }}</th><th class="text-right">{{ i18n.t('po.amount') }}</th> }
                      <th class="w-44">{{ i18n.t('po.received') }}</th>
                    </tr>
                  </thead>
                  <tbody>
                    @for (l of p.lines; track l.id) {
                      <tr>
                        <td class="whitespace-nowrap"><a class="code hover:underline" [routerLink]="['/stock', l.productId]">{{ l.sku }}</a></td>
                        <td>{{ l.name }}</td>
                        <td class="num whitespace-nowrap">{{ i18n.number(l.quantity, 4) }} <span class="text-faint font-sans text-[12px]">{{ l.unit }}</span></td>
                        @if (p.total != null) {
                          <td class="num">{{ i18n.money(l.unitPrice ?? 0, auth.currency()) }}</td>
                          <td class="num">{{ i18n.money(l.net ?? 0, auth.currency()) }}</td>
                        }
                        <td>
                          <div class="flex items-center gap-2">
                            <div class="h-1.5 flex-1 rounded-full bg-sunken overflow-hidden">
                              <div class="h-full" [class]="l.receivedBase >= l.orderedBase ? 'bg-ok' : 'bg-signal'" [style.width.%]="pct(l)"></div>
                            </div>
                            <span class="text-[12px] text-muted tabular-nums whitespace-nowrap">{{ i18n.number(l.receivedBase / l.factor, 2) }}/{{ i18n.number(l.quantity, 2) }}</span>
                          </div>
                        </td>
                      </tr>
                    }
                  </tbody>
                </table>
              </div>
              @if (p.total != null) {
                <div class="flex justify-end border-t border-line px-5 py-4">
                  <dl class="grid grid-cols-[auto_auto] gap-x-8 gap-y-1 text-[13.5px]">
                    <dt class="text-muted">{{ i18n.t('po.subtotal') }}</dt><dd class="num">{{ i18n.money(p.subtotal ?? 0, auth.currency()) }}</dd>
                    <dt class="text-muted">{{ i18n.t('po.taxTotal') }}</dt><dd class="num">{{ i18n.money(p.taxTotal ?? 0, auth.currency()) }}</dd>
                    <dt class="font-semibold pt-1 border-t border-line">{{ i18n.t('po.total') }}</dt>
                    <dd class="num font-semibold text-[15px] pt-1 border-t border-line">{{ i18n.money(p.total, auth.currency()) }}</dd>
                  </dl>
                </div>
              }
            </section>
            @if (p.note) { <section class="card p-5 text-[13.5px]"><div class="label mb-1">{{ i18n.t('po.note') }}</div>{{ p.note }}</section> }

            <section class="card">
              <div class="card-head"><h2 class="card-title">{{ i18n.t('po.receipts') }}</h2></div>
              <ul class="divide-y divide-line">
                @for (r of p.receipts; track r.id) {
                  <li class="flex items-center justify-between gap-3 px-5 py-3 text-[13.5px]">
                    <span class="code">{{ r.number }}</span>
                    <span class="text-muted">{{ i18n.t('adjustments.lines', { n: r.lines }) }}</span>
                    <span class="text-muted">{{ r.receivedBy }} · {{ i18n.relative(r.receivedAt) }}</span>
                  </li>
                } @empty {
                  <li class="px-5 py-4 text-muted text-[13px]">{{ i18n.t('po.noReceipts') }}</li>
                }
              </ul>
            </section>
          </div>

          <aside class="flex flex-col gap-5">
            <section class="card">
              <div class="card-head"><h2 class="card-title">{{ i18n.t('po.approvals') }}</h2></div>
              <ol class="p-5 flex flex-col gap-4">
                @for (a of p.approvals; track a.level) {
                  <li class="flex gap-3">
                    <span class="grid place-items-center size-7 shrink-0 rounded-full" [class]="a.approvedBy ? 'bg-ok-soft text-ok' : 'bg-sunken text-muted'">
                      <lucide-icon [img]="a.approvedBy ? I.done : I.clock" [size]="15" />
                    </span>
                    <div class="min-w-0 text-[13px]">
                      <div class="font-medium">{{ i18n.t('po.level', { n: a.level }) }} · {{ i18n.t($any('role.' + a.role)) }}</div>
                      @if (a.minTotal != null) { <div class="text-muted">≥ {{ i18n.money(a.minTotal, auth.currency()) }}</div> }
                      @if (a.approvedBy) {
                        <div class="text-ok">{{ i18n.t('po.approvedBy', { who: a.approvedBy, when: i18n.relative(a.at!) }) }}</div>
                        @if (a.comment) { <div class="mt-0.5 text-ink-2 italic">“{{ a.comment }}”</div> }
                      } @else {
                        <div class="text-muted">{{ i18n.t('po.waiting') }}</div>
                      }
                    </div>
                  </li>
                } @empty {
                  <li class="text-muted text-[13px]">{{ i18n.t('po.noApproval') }}</li>
                }
              </ol>
            </section>

            <section class="card">
              <div class="card-head"><h2 class="card-title">{{ i18n.t('po.history') }}</h2></div>
              <ol class="p-5 flex flex-col gap-3 text-[13px]">
                @for (e of events(); track e.label) {
                  <li class="flex justify-between gap-3"><span>{{ e.label }}</span><span class="text-muted whitespace-nowrap" [title]="i18n.dateTime(e.at)">{{ i18n.relative(e.at) }}</span></li>
                }
              </ol>
            </section>
          </aside>
        </div>
      }
    </div>
  `,
})
export class PoPage {
  i18n = inject(I18n);
  auth = inject(Auth);
  private api = inject(Api);
  private router = inject(Router);
  private toasts = inject(Toasts);
  private fb = inject(FormBuilder).nonNullable;

  id = input<string>();
  I = {
    back: ArrowLeft, plus: Plus, trash: Trash2, send: Send, check: Check, x: X, ban: Ban, edit: Pencil, download: Download,
    receive: PackageCheck, done: CircleCheck, clock: Clock, file: FileCheck,
  };
  badge = PO_BADGE;

  po = signal<S['PoDto'] | null>(null);
  editing = signal(false);
  busy = signal(false);
  error = signal('');
  suppliers = signal<S['PartnerDto'][]>([]);
  warehouses = signal<S['WarehouseRef'][]>([]);
  products = signal<Record<string, LineProduct>>({});

  form = this.fb.group({
    supplierId: ['', Validators.required],
    warehouseId: ['', Validators.required],
    expectedOn: [''],
    note: [''],
    lines: this.fb.array<LineGroup>([]),
  });
  get lines() {
    return this.form.controls.lines as FormArray<LineGroup>;
  }
  value = toSignal(this.form.valueChanges.pipe(map(() => this.form.getRawValue())), { initialValue: this.form.getRawValue() });
  totals = computed(() =>
    this.value().lines.reduce(
      (t, l) => {
        const net = this.lineNet(l);
        return { net: t.net + net, tax: t.tax + Math.round(net * (l.taxRate || 0)) / 100 };
      },
      { net: 0, tax: 0 },
    ),
  );

  events = computed(() => {
    const p = this.po();
    if (!p) return [];
    const list = [
      { label: this.i18n.t('po.created', { who: p.createdBy ?? '—' }), at: p.createdAt },
      p.submittedAt && { label: this.i18n.t('po.ev.submitted'), at: p.submittedAt },
      p.approvedAt && { label: this.i18n.t('po.ev.approved'), at: p.approvedAt },
      p.sentAt && { label: this.i18n.t('po.ev.sent'), at: p.sentAt },
      ...p.receipts.map((r) => ({ label: r.number, at: r.receivedAt })),
      p.closedAt && { label: this.i18n.t('po.ev.closed'), at: p.closedAt },
    ];
    return list.filter((e): e is { label: string; at: string } => !!e);
  });

  constructor() {
    Promise.all([
      this.api.get<Page<S['PartnerDto']>>('/suppliers', { pageSize: 200 }),
      this.api.get<S['WarehouseRef'][]>('/warehouses/directory'),
    ]).then(([s, w]) => {
      this.suppliers.set(s.items.filter((x) => x.isActive));
      this.warehouses.set(w);
      if (!this.form.controls.warehouseId.value) this.form.controls.warehouseId.setValue(w[0]?.id ?? '');
    });
    effect(() => {
      const id = this.id();
      untracked(() => (id ? this.load(id) : this.startNew()));
    });
  }

  has(action: string) {
    return this.po()?.actions.includes(action) ?? false;
  }

  pct(l: S['PoLineDto']) {
    return l.orderedBase ? Math.min(100, (l.receivedBase / l.orderedBase) * 100) : 0;
  }

  lineNet(l: ReturnType<LineGroup['getRawValue']> | undefined) {
    return l ? Math.round((l.quantity || 0) * (l.unitPrice || 0) * 100) / 100 : 0;
  }

  private startNew() {
    this.editing.set(true);
    this.addLine();
  }

  private async load(id: string) {
    const p = await this.api.get<S['PoDto']>(`/purchase-orders/${id}`);
    this.po.set(p);
    this.editing.set(false);
    this.fill(p);
  }

  private async fill(p: S['PoDto']) {
    this.lines.clear();
    const detail = await Promise.all(p.lines.map((l) => this.api.get<S['ProductDto']>(`/products/${l.productId}`)));
    this.products.set(Object.fromEntries(detail.map((d) => [d.id, { sku: d.sku, name: d.name, baseUnit: d.baseUnit, units: d.units }])));
    for (const l of p.lines) {
      const g = lineGroup(this.fb);
      g.patchValue({ productId: l.productId, quantity: l.quantity, unit: l.unit, unitPrice: l.unitPrice ?? 0, taxRate: l.taxRate ?? 0 });
      this.lines.push(g);
    }
    this.form.patchValue({ supplierId: p.supplierId, warehouseId: p.warehouseId, expectedOn: p.expectedOn ?? '', note: p.note ?? '' });
  }

  addLine() {
    this.lines.push(lineGroup(this.fb));
  }

  async pick(i: number, productId: string) {
    const [d, stock] = await Promise.all([
      this.api.get<S['ProductDto']>(`/products/${productId}`),
      this.api.get<S['ProductStock']>(`/stock/${productId}`),
    ]);
    this.products.update((m) => ({ ...m, [productId]: { sku: d.sku, name: d.name, baseUnit: d.baseUnit, units: d.units } }));
    // Default to the largest pack, priced from the current average cost.
    const pack = [...d.units].sort((a, b) => b.factor - a.factor)[0];
    const cost = Math.max(0, ...stock.warehouses.map((w) => w.avgCost ?? 0));
    this.lines.at(i).patchValue({
      productId, unit: pack?.name ?? d.baseUnit, taxRate: d.taxRate,
      unitPrice: Math.round(cost * (pack?.factor ?? 1) * 100) / 100,
    });
  }

  err(path: string) {
    return fieldError(this.form.get(path), this.i18n);
  }

  async save(submit: boolean) {
    this.form.markAllAsTouched();
    if (this.form.invalid || this.lines.length === 0) {
      this.error.set(this.i18n.t('form.fix'));
      return;
    }
    const v = this.form.getRawValue();
    const body: S['PoInput'] = {
      supplierId: v.supplierId, warehouseId: v.warehouseId, expectedOn: v.expectedOn || null, note: v.note || null,
      version: this.po()?.version ?? null,
      lines: v.lines.map((l) => ({ productId: l.productId, quantity: l.quantity, unit: l.unit || null, unitPrice: l.unitPrice, taxRate: l.taxRate })),
    };
    this.busy.set(true);
    this.error.set('');
    try {
      const existing = this.po();
      let saved = existing
        ? await this.api.put<S['PoDto']>(`/purchase-orders/${existing.id}`, body)
        : await this.api.post<S['PoDto']>('/purchase-orders', body);
      if (submit) saved = await this.api.post<S['PoDto']>(`/purchase-orders/${saved.id}/submit`, {});
      this.toasts.show(this.i18n.t(submit ? 'po.submitted' : 'po.saved'));
      if (!existing) this.router.navigate(['/purchasing', saved.id], { replaceUrl: true });
      else (this.po.set(saved), this.editing.set(false));
    } catch (e) {
      this.error.set(ApiError.from(e).message);
    } finally {
      this.busy.set(false);
    }
  }

  async act(action: 'submit' | 'approve' | 'reject' | 'send' | 'cancel' | 'close') {
    const p = this.po()!;
    let body: object = {};
    if (action === 'reject') {
      const comment = prompt(this.i18n.t('po.rejectPrompt'));
      if (!comment?.trim()) return;
      body = { comment };
    } else if (action === 'approve') {
      const comment = prompt(this.i18n.t('po.approveComment'), '');
      if (comment === null) return;
      body = { comment: comment || null };
    } else if (action === 'cancel' && !confirm(this.i18n.t('po.cancelConfirm'))) return;
    else if (action === 'close' && !confirm(this.i18n.t('po.closeConfirm'))) return;

    this.busy.set(true);
    try {
      this.po.set(await this.api.post<S['PoDto']>(`/purchase-orders/${p.id}/${action}`, body));
      const toast = { submit: 'po.submitted', approve: 'po.approved', reject: 'po.rejected', send: 'po.sentToast', cancel: 'po.cancelled', close: 'po.closed' } as const;
      this.toasts.show(this.i18n.t(toast[action]));
    } catch (e) {
      this.toasts.show(ApiError.from(e).message, 'error');
    } finally {
      this.busy.set(false);
    }
  }
}
