import { Component, computed, effect, inject, input, OnInit, signal, untracked } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { FormArray, FormBuilder, NonNullableFormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { map } from 'rxjs';
import { ArrowLeft, ArrowRight, LucideAngularModule, PackageCheck, Plus, Trash2, Truck } from 'lucide-angular';
import { Api, latest, ApiError, Page, S } from '../core/api';
import { Auth } from '../core/auth';
import { I18n } from '../core/i18n';
import { Toasts } from '../core/toast';
import { ProductPicker } from '../shared/product-picker';
import { fieldError, Pager } from '../shared/ui';

const STATUS_BADGE: Record<string, string> = { InTransit: 'badge-signal', Received: 'badge-ok', Cancelled: 'badge-muted' };

@Component({
  selector: 'app-transfers',
  imports: [RouterLink, LucideAngularModule, Pager],
  template: `
    <div class="page">
      <div class="page-head">
        <div>
          <div class="eyebrow">{{ i18n.t('nav.inventory') }}</div>
          <h1 class="h1 mt-1">{{ i18n.t('transfers.title') }}</h1>
          <p class="mt-1 text-muted">{{ i18n.t('transfers.subtitle') }}</p>
        </div>
        @if (auth.can('TransferStock')) {
          <a class="btn btn-primary" routerLink="/transfers/new"><lucide-icon [img]="PlusIcon" [size]="16" />{{ i18n.t('transfers.new') }}</a>
        }
      </div>
      <div class="card overflow-hidden">
        <div class="flex p-3 border-b border-line">
          <div class="flex rounded-lg border border-line-strong p-0.5 bg-sunken/50">
            @for (f of [{ v: 'InTransit', k: 'transfers.filter.open' }, { v: '', k: 'transfers.filter.all' }]; track f.v) {
              <button class="h-8 px-3 rounded-md text-[13px] font-medium" [class]="status() === f.v ? 'bg-surface text-ink shadow-sm' : 'text-muted hover:text-ink'"
                      (click)="status.set(f.v); page.set(1)">{{ i18n.t($any(f.k)) }}</button>
            }
          </div>
        </div>
        <div class="overflow-x-auto">
          <table class="tbl">
            <thead>
              <tr>
                <th>{{ i18n.t('stock.document') }}</th>
                <th>{{ i18n.t('transfers.from') }} → {{ i18n.t('transfers.to') }}</th>
                <th>{{ i18n.t('common.status') }}</th>
                <th class="text-right">{{ i18n.t('adjust.lines') }}</th>
                <th>{{ i18n.t('transfers.shipped') }}</th>
              </tr>
            </thead>
            <tbody>
              @for (t of data()?.items ?? []; track t.id) {
                <tr class="clickable" (click)="router.navigate(['/transfers', t.id])">
                  <td class="code">{{ t.number }}</td>
                  <td class="font-mono text-[13px]">{{ t.from }} <span class="text-faint">→</span> {{ t.to }}</td>
                  <td><span class="badge" [class]="badge[t.status]">{{ i18n.t($any('transfers.status.' + t.status)) }}</span></td>
                  <td class="num">{{ t.lines }}</td>
                  <td class="text-[13px]" [title]="i18n.dateTime(t.shippedAt)">{{ i18n.relative(t.shippedAt) }}</td>
                </tr>
              } @empty {
                <tr><td colspan="5" class="!py-14 text-center text-muted">{{ i18n.t('transfers.empty') }}</td></tr>
              }
            </tbody>
          </table>
        </div>
        <app-pager [(page)]="page" [size]="25" [total]="data()?.total ?? 0" />
      </div>
    </div>
  `,
})
export class TransfersPage {
  i18n = inject(I18n);
  auth = inject(Auth);
  router = inject(Router);
  private api = inject(Api);
  PlusIcon = Plus;
  badge = STATUS_BADGE;
  status = signal('InTransit');
  page = signal(1);
  data = signal<Page<S['TransferListItem']> | null>(null);

  private fetch = latest((q: object) => this.api.get<Page<S['TransferListItem']>>('/transfers', q as never), (d) => this.data.set(d));

  constructor() {
    effect(() => {
      const q = { status: this.status(), page: this.page() };
      untracked(() => this.fetch(q));
    });
  }
}

@Component({
  selector: 'app-transfer-detail',
  imports: [RouterLink, LucideAngularModule],
  template: `
    <div class="page-narrow">
      <a routerLink="/transfers" class="inline-flex items-center gap-1.5 text-[13px] text-muted hover:text-ink">
        <lucide-icon [img]="BackIcon" [size]="15" />{{ i18n.t('transfers.title') }}
      </a>
      @if (t(); as t) {
        <div class="page-head mt-3">
          <div>
            <h1 class="h1 font-mono">{{ t.number }}</h1>
            <p class="mt-1"><span class="badge" [class]="badge[t.status]">{{ i18n.t($any('transfers.status.' + t.status)) }}</span></p>
          </div>
          @if (t.status === 'InTransit' && canReceive()) {
            <button class="btn btn-primary" (click)="receive()" [disabled]="busy()">
              <lucide-icon [img]="ReceiveIcon" [size]="16" />{{ i18n.t('transfers.receive', { to: t.to }) }}
            </button>
          }
        </div>

        <!-- Route: source → in transit → destination -->
        <div class="card p-5 mb-5">
          <div class="grid grid-cols-[1fr_auto_1fr] items-center gap-4">
            <div>
              <div class="eyebrow">{{ i18n.t('transfers.from') }}</div>
              <div class="mt-1 font-mono text-[20px] font-semibold">{{ t.from }}</div>
              <div class="text-[12.5px] text-muted mt-1">{{ i18n.t('transfers.by', { when: i18n.dateTime(t.shippedAt), who: t.shippedBy ?? '—' }) }}</div>
            </div>
            <div class="flex items-center gap-2 text-faint">
              <span class="h-px w-10 bg-line-strong"></span>
              <span class="grid place-items-center size-9 rounded-full" [class]="t.status === 'InTransit' ? 'bg-signal-soft text-signal-ink' : 'bg-ok-soft text-ok'">
                <lucide-icon [img]="TruckIcon" [size]="17" />
              </span>
              <span class="h-px w-10 bg-line-strong"></span>
            </div>
            <div class="text-right">
              <div class="eyebrow">{{ i18n.t('transfers.to') }}</div>
              <div class="mt-1 font-mono text-[20px] font-semibold">{{ t.to }}</div>
              <div class="text-[12.5px] text-muted mt-1">
                @if (t.receivedAt) { {{ i18n.t('transfers.by', { when: i18n.dateTime(t.receivedAt), who: t.receivedBy ?? '—' }) }} }
                @else { {{ i18n.t('transfers.waiting', { to: t.to }) }} }
              </div>
            </div>
          </div>
          @if (t.note) { <p class="mt-4 pt-4 border-t border-line text-[13.5px] text-ink-2">{{ t.note }}</p> }
        </div>

        <section class="card overflow-hidden">
          <table class="tbl">
            <thead>
              <tr>
                <th>{{ i18n.t('products.sku') }}</th>
                <th>{{ i18n.t('common.name') }}</th>
                <th>{{ i18n.t('stock.batch') }}</th>
                <th class="text-right">{{ i18n.t('adjust.qty') }}</th>
              </tr>
            </thead>
            <tbody>
              @for (l of t.lines; track $index) {
                <tr>
                  <td><a class="code hover:underline" [routerLink]="['/stock', l.productId]">{{ l.sku }}</a></td>
                  <td>{{ l.name }}</td>
                  <td class="code">{{ l.batch ?? '—' }} @if (l.expiresOn) { <span class="text-faint">· {{ l.expiresOn }}</span> }</td>
                  <td class="num">{{ i18n.number(l.quantity, 4) }} <span class="text-faint font-sans text-[12px]">{{ l.baseUnit }}</span></td>
                </tr>
              }
            </tbody>
          </table>
        </section>
      }
    </div>
  `,
})
export class TransferDetailPage {
  i18n = inject(I18n);
  private auth = inject(Auth);
  private api = inject(Api);
  private toasts = inject(Toasts);
  id = input.required<string>();
  BackIcon = ArrowLeft;
  TruckIcon = Truck;
  ReceiveIcon = PackageCheck;
  badge = STATUS_BADGE;
  t = signal<S['TransferDto'] | null>(null);
  busy = signal(false);
  canReceive = computed(() => {
    const me = this.auth.me();
    const t = this.t();
    return !!t && this.auth.can('TransferStock') && (me?.allWarehouses || me?.warehouseIds.includes(t.toWarehouseId));
  });

  constructor() {
    effect(() => {
      const id = this.id();
      untracked(() => this.api.get<S['TransferDto']>(`/transfers/${id}`).then((t) => this.t.set(t)));
    });
  }

  async receive() {
    this.busy.set(true);
    try {
      const t = await this.api.post<S['TransferDto']>(`/transfers/${this.id()}/receive`, {});
      this.t.set(t);
      this.toasts.show(this.i18n.t('transfers.receivedToast', { to: t.to }));
    } catch (e) {
      this.toasts.show(ApiError.from(e).message, 'error');
    } finally {
      this.busy.set(false);
    }
  }
}

const transferLine = (fb: NonNullableFormBuilder) =>
  fb.group({ productId: ['', Validators.required], quantity: [1, [Validators.required, Validators.min(0.0001)]], unit: [''] });
type TransferLineGroup = ReturnType<typeof transferLine>;

interface PickedProduct {
  sku: string;
  name: string;
  baseUnit: string;
  units: { name: string; factor: number }[];
  available: Record<string, number | undefined>;
}

@Component({
  selector: 'app-transfer-new',
  imports: [ReactiveFormsModule, RouterLink, LucideAngularModule, ProductPicker],
  template: `
    <form class="page-narrow" [formGroup]="form" (ngSubmit)="submit()">
      <a routerLink="/transfers" class="inline-flex items-center gap-1.5 text-[13px] text-muted hover:text-ink">
        <lucide-icon [img]="BackIcon" [size]="15" />{{ i18n.t('transfers.title') }}
      </a>
      <div class="page-head mt-3"><h1 class="h1">{{ i18n.t('transfers.new') }}</h1></div>
      @if (error()) { <div class="mb-4 rounded-lg bg-danger-soft px-4 py-3 text-[13.5px] text-danger">{{ error() }}</div> }

      <section class="card mb-5 p-5">
        <div class="grid grid-cols-[1fr_auto_1fr] items-end gap-3">
          <label class="field">
            <span class="label">{{ i18n.t('transfers.from') }}</span>
            <select class="input" formControlName="fromWarehouseId">
              @for (w of sources(); track w.id) { <option [value]="w.id">{{ w.code }} · {{ w.name }}</option> }
            </select>
          </label>
          <span class="h-9 grid place-items-center text-faint"><lucide-icon [img]="ArrowIcon" [size]="18" /></span>
          <label class="field">
            <span class="label">{{ i18n.t('transfers.to') }}</span>
            <select class="input" formControlName="toWarehouseId" [class.invalid]="err('toWarehouseId')">
              @for (w of destinations(); track w.id) { <option [value]="w.id">{{ w.code }} · {{ w.name }}</option> }
            </select>
          </label>
          <label class="field col-span-3">
            <span class="label">{{ i18n.t('adjust.note') }} <span class="text-faint font-normal">· {{ i18n.t('common.optional') }}</span></span>
            <input class="input" formControlName="note" />
          </label>
        </div>
        <p class="hint mt-3">{{ i18n.t('transfers.hint', { from: code(value().fromWarehouseId), to: code(value().toWarehouseId) }) }}</p>
      </section>

      <section class="card">
        <div class="card-head">
          <h2 class="card-title">{{ i18n.t('adjust.lines') }}</h2>
          <button type="button" class="btn btn-sm" (click)="add()"><lucide-icon [img]="PlusIcon" [size]="14" />{{ i18n.t('adjust.addLine') }}</button>
        </div>
        <div formArrayName="lines" class="divide-y divide-line">
          @for (line of lines.controls; track line; let i = $index) {
            @let v = line.getRawValue();
            @let p = picked()[v.productId];
            <div class="p-5 grid gap-3 sm:grid-cols-[minmax(0,1fr)_auto_auto]" [formGroupName]="i">
              <div class="field">
                <app-product-picker [placeholder]="i18n.t('adjust.pickProduct')" [label]="p ? p.sku + ' · ' + p.name : ''"
                                    [invalid]="!!err('lines.' + i + '.productId')" (picked)="pick(i, $event.id)" />
                @if (p) {
                  <span class="hint">{{ i18n.t('stock.available') }}: {{ i18n.number(p.available[value().fromWarehouseId] ?? 0) }} {{ p.baseUnit }}</span>
                }
              </div>
              <div class="flex gap-2 self-start">
                <input class="input num !text-left w-28" type="number" min="0" step="any" formControlName="quantity" [class.invalid]="err('lines.' + i + '.quantity')" />
                <select class="input" formControlName="unit">
                  @if (p) {
                    <option [value]="p.baseUnit">{{ p.baseUnit }}</option>
                    @for (u of p.units; track u.name) { <option [value]="u.name">{{ u.name }} ({{ u.factor }})</option> }
                  }
                </select>
              </div>
              <button type="button" class="btn btn-ghost btn-sm h-9 self-start" (click)="lines.removeAt(i)" [attr.aria-label]="i18n.t('common.remove')">
                <lucide-icon [img]="TrashIcon" [size]="15" />
              </button>
            </div>
          }
        </div>
        <div class="flex justify-end gap-2 px-5 py-4 border-t border-line bg-paper/60 rounded-b-xl">
          <a class="btn" routerLink="/transfers">{{ i18n.t('common.cancel') }}</a>
          <button class="btn btn-primary" [disabled]="busy() || lines.length === 0"><lucide-icon [img]="TruckIcon" [size]="16" />{{ i18n.t('transfers.ship') }}</button>
        </div>
      </section>
    </form>
  `,
})
export class TransferNewPage implements OnInit {
  i18n = inject(I18n);
  private auth = inject(Auth);
  private api = inject(Api);
  private router = inject(Router);
  private toasts = inject(Toasts);
  private fb = inject(FormBuilder).nonNullable;
  product = input<string>();

  BackIcon = ArrowLeft;
  ArrowIcon = ArrowRight;
  PlusIcon = Plus;
  TrashIcon = Trash2;
  TruckIcon = Truck;

  /** Ship from warehouses this user works in; send to any active warehouse. */
  sources = signal<S['WarehouseDto'][]>([]);
  picked = signal<Record<string, PickedProduct>>({});
  busy = signal(false);
  error = signal('');
  private key = crypto.randomUUID();

  form = this.fb.group({
    fromWarehouseId: ['', Validators.required],
    toWarehouseId: ['', Validators.required],
    note: [''],
    lines: this.fb.array<TransferLineGroup>([]),
  });
  get lines() {
    return this.form.controls.lines as FormArray<TransferLineGroup>;
  }
  value = toSignal(this.form.valueChanges.pipe(map(() => this.form.getRawValue())), { initialValue: this.form.getRawValue() });
  private allActive = signal<S['WarehouseRef'][]>([]);
  destinations = computed(() => this.allActive().filter((w) => w.id !== this.value().fromWarehouseId));

  async ngOnInit() {
    const [mine, all] = await Promise.all([
      this.api.get<S['WarehouseDto'][]>('/warehouses'),
      this.api.get<S['WarehouseRef'][]>('/warehouses/directory'),
    ]);
    const from = mine.find((w) => w.isActive);
    this.sources.set(mine.filter((w) => w.isActive));
    this.allActive.set(all);
    this.form.patchValue({ fromWarehouseId: from?.id ?? '', toWarehouseId: all.find((w) => w.id !== from?.id)?.id ?? '' });
    this.form.controls.fromWarehouseId.valueChanges.subscribe((from) => {
      if (this.form.controls.toWarehouseId.value === from)
        this.form.controls.toWarehouseId.setValue(this.allActive().find((w) => w.id !== from)?.id ?? '');
    });
    this.add();
    const preset = this.product();
    if (preset) await this.pick(0, preset);
  }

  code(id: string) {
    return this.allActive().find((w) => w.id === id)?.code ?? '…';
  }

  add() {
    this.lines.push(transferLine(this.fb));
  }

  async pick(i: number, productId: string) {
    const [d, s] = await Promise.all([
      this.api.get<S['ProductDto']>(`/products/${productId}`),
      this.api.get<S['ProductStock']>(`/stock/${productId}`),
    ]);
    this.picked.update((m) => ({
      ...m,
      [productId]: {
        sku: d.sku, name: d.name, baseUnit: d.baseUnit, units: d.units,
        available: Object.fromEntries(s.warehouses.map((w) => [w.warehouseId, w.available])),
      },
    }));
    this.lines.at(i).patchValue({ productId, unit: d.baseUnit });
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
    this.busy.set(true);
    this.error.set('');
    try {
      const t = await this.api.post<S['TransferDto']>('/transfers', {
        ...v,
        note: v.note || null,
        idempotencyKey: this.key,
        lines: v.lines.map((l) => ({ productId: l.productId, quantity: l.quantity, unit: l.unit || null, batchNumber: null })),
      } satisfies S['TransferInput']);
      this.toasts.show(this.i18n.t('transfers.shippedToast', { number: t.number }));
      this.router.navigate(['/transfers', t.id]);
    } catch (e) {
      this.error.set(ApiError.from(e).message);
      this.key = crypto.randomUUID();
    } finally {
      this.busy.set(false);
    }
  }
}
