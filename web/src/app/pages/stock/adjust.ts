import { Component, computed, inject, input, OnInit, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { FormArray, FormBuilder, NonNullableFormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { ArrowLeft, LucideAngularModule, Minus, Plus, Trash2 } from 'lucide-angular';
import { Api, ApiError, S } from '../../core/api';
import { Auth } from '../../core/auth';
import { I18n } from '../../core/i18n';
import { Toasts } from '../../core/toast';
import { ProductPicker } from '../../shared/product-picker';
import { fieldError } from '../../shared/ui';

const REASONS = ['found', 'damaged', 'expired', 'count', 'sample', 'opening', 'other'] as const;

interface ProductInfo {
  id: string;
  sku: string;
  name: string;
  baseUnit: string;
  trackBatches: boolean;
  units: { name: string; factor: number }[];
  /** On hand per warehouse id */
  stock: Record<string, number>;
}

const lineGroup = (fb: NonNullableFormBuilder) =>
  fb.group({
    productId: ['', Validators.required],
    direction: ['in' as 'in' | 'out'],
    quantity: [1, [Validators.required, Validators.min(0.0001)]],
    unit: [''],
    batchNumber: [''],
    expiresOn: [''],
    unitCost: fb.control<number | null>(null, Validators.min(0)),
  });
type LineGroup = ReturnType<typeof lineGroup>;

@Component({
  selector: 'app-adjust',
  imports: [ReactiveFormsModule, RouterLink, LucideAngularModule, ProductPicker],
  template: `
    <form class="page-narrow !max-w-[1040px]" [formGroup]="form" (ngSubmit)="submit()">
      <a routerLink="/stock" class="inline-flex items-center gap-1.5 text-[13px] text-muted hover:text-ink">
        <lucide-icon [img]="BackIcon" [size]="15" />{{ i18n.t('stock.title') }}
      </a>
      <div class="page-head mt-3">
        <div>
          <h1 class="h1">{{ i18n.t('adjust.title') }}</h1>
          <p class="mt-1 text-muted max-w-[560px]">{{ i18n.t('adjust.subtitle') }}</p>
        </div>
      </div>

      @if (error()) { <div class="mb-4 rounded-lg bg-danger-soft px-4 py-3 text-[13.5px] text-danger">{{ error() }}</div> }

      <section class="card mb-5">
        <div class="grid sm:grid-cols-3 gap-4 p-5">
          <label class="field">
            <span class="label">{{ i18n.t('adjust.warehouse') }}</span>
            <select class="input" formControlName="warehouseId">
              @for (w of warehouses(); track w.id) { <option [value]="w.id">{{ w.code }} · {{ w.name }}</option> }
            </select>
          </label>
          <label class="field">
            <span class="label">{{ i18n.t('adjust.reason') }}</span>
            <select class="input" formControlName="reason">
              @for (r of reasons; track r) { <option [value]="r">{{ i18n.t($any('reason.' + r)) }}</option> }
            </select>
          </label>
          <label class="field">
            <span class="label">{{ i18n.t('adjust.note') }} @if (value().reason !== 'other') { <span class="text-faint font-normal">· {{ i18n.t('common.optional') }}</span> }</span>
            <input class="input" formControlName="note" [class.invalid]="err('note')" />
            @if (err('note'); as e) { <span class="error">{{ e }}</span> }
          </label>
        </div>
      </section>

      <section class="card">
        <div class="card-head">
          <h2 class="card-title">{{ i18n.t('adjust.lines') }}</h2>
          <button type="button" class="btn btn-sm" (click)="addLine()"><lucide-icon [img]="PlusIcon" [size]="14" />{{ i18n.t('adjust.addLine') }}</button>
        </div>
        <div formArrayName="lines" class="divide-y divide-line">
          @for (line of lines.controls; track line; let i = $index) {
            @let v = line.getRawValue();
            @let p = info()[v.productId];
            <div class="p-5 grid gap-3 lg:grid-cols-[minmax(0,1.6fr)_auto_minmax(0,1fr)_auto]" [formGroupName]="i">
              <div class="field">
                <app-product-picker
                  [placeholder]="i18n.t('adjust.pickProduct')"
                  [label]="p ? p.sku + ' · ' + p.name : ''"
                  [invalid]="!!err('lines.' + i + '.productId')"
                  (picked)="pick(i, $event)"
                />
                @if (p) {
                  <span class="hint">
                    {{ i18n.t('adjust.current', { n: i18n.number(onHand(p)), unit: p.baseUnit }) }}
                    <span class="font-mono">→ {{ i18n.number(after(p, v)) }}</span>
                  </span>
                }
              </div>

              <div class="flex h-9 rounded-lg border border-line-strong p-0.5 self-start" role="group">
                @for (d of directions; track d.value) {
                  <button
                    type="button"
                    class="flex items-center gap-1 px-2.5 rounded-md text-[13px] font-medium"
                    [class]="v.direction === d.value ? (d.value === 'in' ? 'bg-ok-soft text-ok' : 'bg-danger-soft text-danger') : 'text-muted hover:text-ink'"
                    (click)="line.controls.direction.setValue(d.value)"
                  >
                    <lucide-icon [img]="d.icon" [size]="13" />{{ i18n.t(d.label) }}
                  </button>
                }
              </div>

              <div class="flex gap-2 self-start">
                <input class="input num !text-left w-28" type="number" min="0" step="any" formControlName="quantity"
                       [attr.aria-label]="i18n.t('adjust.qty')" [class.invalid]="err('lines.' + i + '.quantity')" />
                <select class="input" formControlName="unit" [attr.aria-label]="i18n.t('adjust.unit')">
                  @if (p) {
                    <option [value]="p.baseUnit">{{ p.baseUnit }}</option>
                    @for (u of p.units; track u.name) { <option [value]="u.name">{{ u.name }} ({{ u.factor }})</option> }
                  }
                </select>
              </div>

              <button type="button" class="btn btn-ghost btn-sm h-9 self-start justify-self-end" (click)="lines.removeAt(i)" [attr.aria-label]="i18n.t('common.remove')">
                <lucide-icon [img]="TrashIcon" [size]="15" />
              </button>

              @if (p?.trackBatches || (v.direction === 'in' && auth.can('ViewCosts'))) {
                <div class="lg:col-span-4 grid gap-3 sm:grid-cols-3 rounded-lg bg-paper p-3">
                  @if (p?.trackBatches) {
                    <label class="field">
                      <span class="label">{{ i18n.t('adjust.batch') }}</span>
                      <input class="input input-mono uppercase" formControlName="batchNumber"
                             [placeholder]="v.direction === 'out' ? i18n.t('adjust.anyBatch') : ''" />
                      @if (v.direction === 'out' && !v.batchNumber) { <span class="hint">{{ i18n.t('adjust.fefo') }}</span> }
                    </label>
                    @if (v.direction === 'in') {
                      <label class="field">
                        <span class="label">{{ i18n.t('adjust.expiry') }}</span>
                        <input class="input" type="date" formControlName="expiresOn" />
                      </label>
                    }
                  }
                  @if (v.direction === 'in' && auth.can('ViewCosts')) {
                    <label class="field">
                      <span class="label">{{ i18n.t('adjust.cost') }} <span class="text-faint font-normal">/ {{ v.unit || p?.baseUnit }}</span></span>
                      <input class="input num !text-left" type="number" min="0" step="0.0001" formControlName="unitCost" />
                      <span class="hint">{{ i18n.t('adjust.costHint') }}</span>
                    </label>
                  }
                </div>
              }
            </div>
          }
        </div>
        <div class="flex justify-end gap-2 px-5 py-4 border-t border-line bg-paper/60 rounded-b-xl">
          <a class="btn" routerLink="/stock">{{ i18n.t('common.cancel') }}</a>
          <button class="btn btn-primary" [disabled]="busy() || lines.length === 0">{{ i18n.t('adjust.submit') }}</button>
        </div>
      </section>
    </form>
  `,
})
export class AdjustPage implements OnInit {
  i18n = inject(I18n);
  auth = inject(Auth);
  private api = inject(Api);
  private router = inject(Router);
  private toasts = inject(Toasts);
  private fb = inject(FormBuilder).nonNullable;

  product = input<string>();
  BackIcon = ArrowLeft;
  PlusIcon = Plus;
  TrashIcon = Trash2;
  reasons = REASONS;
  directions = [
    { value: 'in' as const, label: 'adjust.in' as const, icon: Plus },
    { value: 'out' as const, label: 'adjust.out' as const, icon: Minus },
  ];

  warehouses = signal<S['WarehouseDto'][]>([]);
  info = signal<Record<string, ProductInfo>>({});
  busy = signal(false);
  error = signal('');
  /** One key per form: a double submit or a retry after a timeout posts once. */
  private key = crypto.randomUUID();

  form = this.fb.group({
    warehouseId: ['', Validators.required],
    reason: ['found' as string],
    note: [''],
    lines: this.fb.array<LineGroup>([]),
  });
  get lines() {
    return this.form.controls.lines as FormArray<LineGroup>;
  }
  value = toSignal(this.form.valueChanges, { initialValue: this.form.getRawValue() });

  async ngOnInit() {
    const ws = (await this.api.get<S['WarehouseDto'][]>('/warehouses')).filter((w) => w.isActive);
    this.warehouses.set(ws);
    this.form.controls.warehouseId.setValue(ws[0]?.id ?? '');
    this.form.controls.reason.valueChanges.subscribe((r) => {
      const note = this.form.controls.note;
      note.setValidators(r === 'other' ? Validators.required : null);
      note.updateValueAndValidity();
    });
    this.addLine();
    const preset = this.product();
    if (preset) await this.load(0, preset);
  }

  addLine() {
    this.lines.push(lineGroup(this.fb));
  }

  async pick(i: number, p: S['ProductListItem']) {
    await this.load(i, p.id);
  }

  private async load(i: number, productId: string) {
    const [detail, stock] = await Promise.all([
      this.api.get<S['ProductDto']>(`/products/${productId}`),
      this.api.get<S['ProductStock']>(`/stock/${productId}`),
    ]);
    this.info.update((m) => ({
      ...m,
      [productId]: {
        id: productId, sku: detail.sku, name: detail.name, baseUnit: detail.baseUnit, trackBatches: detail.trackBatches,
        units: detail.units, stock: Object.fromEntries(stock.warehouses.map((w) => [w.warehouseId, w.onHand])),
      },
    }));
    this.lines.at(i).patchValue({ productId, unit: detail.baseUnit });
  }

  onHand(p: ProductInfo) {
    return p.stock[this.form.controls.warehouseId.value] ?? 0;
  }

  after(p: ProductInfo, v: ReturnType<LineGroup['getRawValue']>) {
    const factor = p.units.find((u) => u.name === v.unit)?.factor ?? 1;
    return this.onHand(p) + (v.direction === 'in' ? 1 : -1) * (v.quantity || 0) * factor;
  }

  err(path: string) {
    return fieldError(this.form.get(path), this.i18n);
  }

  async submit() {
    this.form.markAllAsTouched();
    if (this.form.invalid) {
      this.error.set(this.i18n.t('form.fix'));
      return;
    }
    const v = this.form.getRawValue();
    const body: S['AdjustmentInput'] = {
      warehouseId: v.warehouseId,
      reason: v.reason,
      note: v.note || null,
      idempotencyKey: this.key,
      lines: v.lines.map((l) => ({
        productId: l.productId,
        quantity: l.direction === 'in' ? l.quantity : -l.quantity,
        unit: l.unit || null,
        batchNumber: l.batchNumber || null,
        expiresOn: l.direction === 'in' && l.expiresOn ? l.expiresOn : null,
        unitCost: l.direction === 'in' ? l.unitCost : null,
      })),
    };
    this.busy.set(true);
    this.error.set('');
    try {
      const res = await this.api.post<{ number: string }>('/stock/adjustments', body);
      this.toasts.show(this.i18n.t('adjust.posted', { number: res.number }));
      const only = v.lines.length === 1 ? v.lines[0].productId : null;
      this.router.navigate(only ? ['/stock', only] : ['/stock/adjustments']);
    } catch (e) {
      this.error.set(ApiError.from(e).message);
      this.key = crypto.randomUUID(); // the server rejected it; a corrected submit is a new request
    } finally {
      this.busy.set(false);
    }
  }
}
