import { Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { FormBuilder, FormsModule, ReactiveFormsModule, Validators } from '@angular/forms';
import { LucideAngularModule, Plus, Search } from 'lucide-angular';
import { Api, ApiError, Page, S } from '../core/api';
import { Auth } from '../core/auth';
import { I18n } from '../core/i18n';
import { Toasts } from '../core/toast';
import { applyServerErrors, fieldError, Pager, Sheet } from '../shared/ui';

type Partner = S['PartnerDto'];

/** Suppliers and customers: the same list and form, plus lead time or credit limit. */
@Component({
  selector: 'app-partners',
  imports: [FormsModule, ReactiveFormsModule, LucideAngularModule, Pager, Sheet],
  template: `
    <div class="page">
      <div class="page-head">
        <div>
          <div class="eyebrow">{{ i18n.t('nav.masterData') }}</div>
          <h1 class="h1 mt-1">{{ i18n.t(isSupplier() ? 'partners.suppliers' : 'partners.customers') }}</h1>
        </div>
        @if (canEdit()) {
          <button class="btn btn-primary" (click)="edit(null)">
            <lucide-icon [img]="PlusIcon" [size]="16" />{{ i18n.t(isSupplier() ? 'partners.newSupplier' : 'partners.newCustomer') }}
          </button>
        }
      </div>

      <div class="card overflow-hidden">
        <div class="p-3 border-b border-line">
          <label class="relative block max-w-[420px]">
            <span class="pointer-events-none absolute inset-y-0 left-3 flex items-center text-faint"><lucide-icon [img]="SearchIcon" [size]="16" /></span>
            <input class="input pl-9" [placeholder]="i18n.t('partners.search')" [ngModel]="q()" (ngModelChange)="search($event)" />
          </label>
        </div>
        <div class="overflow-x-auto">
          <table class="tbl">
            <thead>
              <tr>
                <th>{{ i18n.t('common.code') }}</th>
                <th>{{ i18n.t('common.name') }}</th>
                <th class="hidden md:table-cell">{{ i18n.t('partners.contact') }}</th>
                <th class="text-right">{{ i18n.t('partners.terms') }}</th>
                <th class="text-right">{{ i18n.t(isSupplier() ? 'partners.leadTime' : 'partners.creditLimit') }}</th>
              </tr>
            </thead>
            <tbody>
              @for (p of data()?.items ?? []; track p.id) {
                <tr class="clickable" (click)="edit(p)">
                  <td class="code">{{ p.code }}</td>
                  <td>
                    <span class="font-medium">{{ p.name }}</span>
                    @if (!p.isActive) { <span class="badge badge-muted ml-2">{{ i18n.t('common.inactive') }}</span> }
                  </td>
                  <td class="hidden md:table-cell text-ink-2 text-[13px]">
                    <div>{{ p.email }}</div>
                    <div class="text-muted">{{ p.phone }}</div>
                  </td>
                  <td class="num">{{ i18n.t('common.days', { n: p.paymentTermsDays }) }}</td>
                  <td class="num">
                    @if (isSupplier()) {
                      {{ i18n.t('common.days', { n: p.leadTimeDays ?? 0 }) }}
                    } @else if (p.creditLimit) {
                      {{ i18n.money(p.creditLimit, auth.currency()) }}
                    } @else {
                      <span class="badge badge-signal">{{ i18n.t('partners.cashOnly') }}</span>
                    }
                  </td>
                </tr>
              } @empty {
                <tr><td colspan="5" class="!py-14 text-center text-muted">{{ i18n.t('partners.empty') }}</td></tr>
              }
            </tbody>
          </table>
        </div>
        <app-pager [(page)]="page" [size]="size" [total]="data()?.total ?? 0" />
      </div>
    </div>

    @if (editing() !== undefined) {
      <app-sheet [title]="sheetTitle()" (closed)="editing.set(undefined)">
        <form id="partner" class="grid grid-cols-2 gap-4" [formGroup]="form" (ngSubmit)="save()">
          <label class="field">
            <span class="label">{{ i18n.t('common.code') }}</span>
            <input class="input input-mono uppercase" formControlName="code" [class.invalid]="err('code')" />
            @if (err('code'); as e) { <span class="error">{{ e }}</span> }
          </label>
          <label class="field">
            <span class="label">{{ i18n.t('partners.taxId') }} <span class="text-faint font-normal">· {{ i18n.t('common.optional') }}</span></span>
            <input class="input input-mono" formControlName="taxId" />
          </label>
          <label class="field col-span-2">
            <span class="label">{{ i18n.t('common.name') }}</span>
            <input class="input" formControlName="name" [class.invalid]="err('name')" />
            @if (err('name'); as e) { <span class="error">{{ e }}</span> }
          </label>
          <label class="field">
            <span class="label">{{ i18n.t('common.email') }}</span>
            <input class="input" type="email" formControlName="email" [class.invalid]="err('email')" />
            @if (err('email'); as e) { <span class="error">{{ e }}</span> }
          </label>
          <label class="field">
            <span class="label">{{ i18n.t('common.phone') }}</span>
            <input class="input" formControlName="phone" />
          </label>
          <label class="field col-span-2">
            <span class="label">{{ i18n.t('common.address') }}</span>
            <textarea class="input" rows="2" formControlName="address"></textarea>
          </label>
          <label class="field col-span-2 sm:col-span-1">
            <span class="label">{{ i18n.t('partners.terms') }}</span>
            <input class="input num !text-left" type="number" min="0" formControlName="paymentTermsDays" [class.invalid]="err('paymentTermsDays')" />
            <span class="hint">{{ i18n.t('partners.termsHint') }}</span>
          </label>
          @if (isSupplier()) {
            <label class="field col-span-2 sm:col-span-1">
              <span class="label">{{ i18n.t('partners.leadTime') }}</span>
              <input class="input num !text-left" type="number" min="0" formControlName="leadTimeDays" [class.invalid]="err('leadTimeDays')" />
              <span class="hint">{{ i18n.t('partners.leadTimeHint') }}</span>
            </label>
          } @else {
            <label class="field col-span-2 sm:col-span-1">
              <span class="label">{{ i18n.t('partners.creditLimit') }}</span>
              <input class="input num !text-left" type="number" min="0" step="100" formControlName="creditLimit" [class.invalid]="err('creditLimit')" />
              <span class="hint">{{ i18n.t('partners.creditLimitHint') }}</span>
            </label>
          }
          <label class="col-span-2 flex items-center gap-2.5 cursor-pointer">
            <input type="checkbox" class="check" formControlName="isActive" />
            <span class="font-medium">{{ i18n.t('common.active') }}</span>
          </label>
          @if (error()) { <div class="col-span-2 rounded-lg bg-danger-soft px-3 py-2 text-[13px] text-danger">{{ error() }}</div> }
        </form>
        <ng-container actions>
          <button class="btn" (click)="editing.set(undefined)">{{ i18n.t('common.cancel') }}</button>
          @if (canEdit()) {
            <button class="btn btn-primary" form="partner" [disabled]="saving()">{{ i18n.t('common.save') }}</button>
          }
        </ng-container>
      </app-sheet>
    }
  `,
})
export class PartnersPage {
  i18n = inject(I18n);
  auth = inject(Auth);
  private api = inject(Api);
  private toasts = inject(Toasts);

  kind = input.required<'suppliers' | 'customers'>();
  isSupplier = computed(() => this.kind() === 'suppliers');
  canEdit = computed(() => this.auth.can(this.isSupplier() ? 'EditSuppliers' : 'EditCustomers'));

  PlusIcon = Plus;
  SearchIcon = Search;
  size = 25;
  q = signal('');
  page = signal(1);
  data = signal<Page<Partner> | null>(null);
  editing = signal<Partner | null | undefined>(undefined);
  saving = signal(false);
  error = signal('');
  private timer?: ReturnType<typeof setTimeout>;

  sheetTitle = computed(() => {
    const e = this.editing();
    if (this.isSupplier()) return this.i18n.t(e ? 'partners.editSupplier' : 'partners.newSupplier');
    return this.i18n.t(e ? 'partners.editCustomer' : 'partners.newCustomer');
  });

  form = inject(FormBuilder).nonNullable.group({
    code: ['', [Validators.required, Validators.pattern(/^[A-Za-z0-9-]+$/)]],
    name: ['', Validators.required],
    email: ['', Validators.email],
    phone: [''],
    address: [''],
    taxId: [''],
    paymentTermsDays: [30, [Validators.required, Validators.min(0), Validators.max(365)]],
    leadTimeDays: [7, [Validators.min(0), Validators.max(365)]],
    creditLimit: [0, Validators.min(0)],
    isActive: [true],
  });

  constructor() {
    effect(() => {
      const query = { q: this.q(), page: this.page(), pageSize: this.size };
      const kind = this.kind();
      untracked(() => this.load(kind, query));
    });
  }

  private async load(kind: string, query: Record<string, string | number>) {
    this.data.set(await this.api.get<Page<Partner>>(`/${kind}`, query));
  }

  search(value: string) {
    clearTimeout(this.timer);
    this.timer = setTimeout(() => (this.q.set(value), this.page.set(1)), 200);
  }

  edit(p: Partner | null) {
    this.error.set('');
    this.form.reset({
      code: p?.code ?? '',
      name: p?.name ?? '',
      email: p?.email ?? '',
      phone: p?.phone ?? '',
      address: p?.address ?? '',
      taxId: p?.taxId ?? '',
      paymentTermsDays: p?.paymentTermsDays ?? 30,
      leadTimeDays: p?.leadTimeDays ?? 7,
      creditLimit: p?.creditLimit ?? 0,
      isActive: p?.isActive ?? true,
    });
    if (this.canEdit()) this.form.enable();
    else this.form.disable();
    this.editing.set(p);
  }

  err(path: string) {
    return fieldError(this.form.get(path), this.i18n);
  }

  async save() {
    this.form.markAllAsTouched();
    if (this.form.invalid) return;
    const current = this.editing();
    this.saving.set(true);
    try {
      const body = { ...this.form.getRawValue(), version: current?.version ?? null };
      if (current) await this.api.put(`/${this.kind()}/${current.id}`, body);
      else await this.api.post(`/${this.kind()}`, body);
      this.toasts.show(this.i18n.t('common.saved'));
      this.editing.set(undefined);
      await this.load(this.kind(), { q: this.q(), page: this.page(), pageSize: this.size });
    } catch (e) {
      const err = ApiError.from(e);
      const rest = applyServerErrors(this.form, err);
      this.error.set(rest.length ? rest.join(' ') : Object.keys(err.fields).length ? '' : err.message);
    } finally {
      this.saving.set(false);
    }
  }
}
