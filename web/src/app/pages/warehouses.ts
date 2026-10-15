import { Component, inject, OnInit, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { LucideAngularModule, MapPin, Pencil, Plus } from 'lucide-angular';
import { Api, ApiError, S } from '../core/api';
import { Auth } from '../core/auth';
import { I18n } from '../core/i18n';
import { Toasts } from '../core/toast';
import { applyServerErrors, fieldError, Sheet } from '../shared/ui';

@Component({
  selector: 'app-warehouses',
  imports: [ReactiveFormsModule, LucideAngularModule, Sheet],
  template: `
    <div class="page">
      <div class="page-head">
        <div>
          <div class="eyebrow">{{ i18n.t('nav.masterData') }}</div>
          <h1 class="h1 mt-1">{{ i18n.t('warehouses.title') }}</h1>
          <p class="mt-1 text-muted">{{ i18n.t('warehouses.subtitle') }}</p>
        </div>
        @if (auth.can('ManageWarehouses')) {
          <button class="btn btn-primary" (click)="edit(null)"><lucide-icon [img]="PlusIcon" [size]="16" />{{ i18n.t('warehouses.new') }}</button>
        }
      </div>

      <div class="grid gap-4 sm:grid-cols-2 xl:grid-cols-3">
        @for (w of list(); track w.id) {
          <article class="card p-5 flex flex-col gap-4" [class.opacity-60]="!w.isActive">
            <div class="flex items-start justify-between gap-3">
              <div class="grid place-items-center h-11 min-w-14 px-2 rounded-lg bg-brand-soft font-mono text-[14px] font-semibold text-brand-ink">
                {{ w.code }}
              </div>
              @if (auth.can('ManageWarehouses')) {
                <button class="btn btn-ghost btn-sm" (click)="edit(w)" [attr.aria-label]="i18n.t('common.edit')"><lucide-icon [img]="EditIcon" [size]="15" /></button>
              }
            </div>
            <div>
              <h2 class="text-[16px] font-semibold">{{ w.name }}</h2>
              @if (w.city || w.address) {
                <p class="mt-1 flex items-start gap-1.5 text-[13px] text-muted">
                  <lucide-icon [img]="PinIcon" [size]="14" class="mt-0.5 shrink-0" />
                  {{ place(w) }}
                </p>
              }
            </div>
            <div class="mt-auto">
              <span class="badge" [class]="w.isActive ? 'badge-ok' : 'badge-muted'">
                {{ w.isActive ? i18n.t('common.active') : i18n.t('common.inactive') }}
              </span>
            </div>
          </article>
        }
      </div>
    </div>

    @if (editing() !== undefined) {
      <app-sheet [title]="editing() ? i18n.t('warehouses.edit') : i18n.t('warehouses.new')" (closed)="editing.set(undefined)">
        <form id="wh" class="flex flex-col gap-4" [formGroup]="form" (ngSubmit)="save()">
          <label class="field">
            <span class="label">{{ i18n.t('common.code') }}</span>
            <input class="input input-mono uppercase w-40" formControlName="code" [class.invalid]="err('code')" />
            @if (err('code'); as e) { <span class="error">{{ e }}</span> } @else { <span class="hint">{{ i18n.t('warehouses.codeHint') }}</span> }
          </label>
          <label class="field">
            <span class="label">{{ i18n.t('common.name') }}</span>
            <input class="input" formControlName="name" [class.invalid]="err('name')" />
            @if (err('name'); as e) { <span class="error">{{ e }}</span> }
          </label>
          <label class="field">
            <span class="label">{{ i18n.t('common.city') }}</span>
            <input class="input" formControlName="city" />
          </label>
          <label class="field">
            <span class="label">{{ i18n.t('common.address') }}</span>
            <textarea class="input" rows="2" formControlName="address"></textarea>
          </label>
          <label class="flex items-center gap-2.5 cursor-pointer">
            <input type="checkbox" class="check" formControlName="isActive" />
            <span class="font-medium">{{ i18n.t('common.active') }}</span>
          </label>
          @if (error()) { <div class="rounded-lg bg-danger-soft px-3 py-2 text-[13px] text-danger">{{ error() }}</div> }
        </form>
        <ng-container actions>
          <button class="btn" (click)="editing.set(undefined)">{{ i18n.t('common.cancel') }}</button>
          <button class="btn btn-primary" form="wh" [disabled]="saving()">{{ i18n.t('common.save') }}</button>
        </ng-container>
      </app-sheet>
    }
  `,
})
export class WarehousesPage implements OnInit {
  i18n = inject(I18n);
  auth = inject(Auth);
  private api = inject(Api);
  private toasts = inject(Toasts);

  PlusIcon = Plus;
  EditIcon = Pencil;
  PinIcon = MapPin;

  list = signal<S['WarehouseDto'][]>([]);
  /** undefined = closed, null = new, otherwise the warehouse being edited */
  editing = signal<S['WarehouseDto'] | null | undefined>(undefined);
  saving = signal(false);
  error = signal('');

  form = inject(FormBuilder).nonNullable.group({
    code: ['', [Validators.required, Validators.pattern(/^[A-Za-z0-9-]+$/)]],
    name: ['', Validators.required],
    city: [''],
    address: [''],
    isActive: [true],
  });

  async ngOnInit() {
    this.list.set(await this.api.get<S['WarehouseDto'][]>('/warehouses'));
  }

  place = (w: S['WarehouseDto']) => [w.address, w.city].filter(Boolean).join(', ');

  edit(w: S['WarehouseDto'] | null) {
    this.error.set('');
    this.form.reset({ code: w?.code ?? '', name: w?.name ?? '', city: w?.city ?? '', address: w?.address ?? '', isActive: w?.isActive ?? true });
    this.editing.set(w);
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
      if (current) await this.api.put(`/warehouses/${current.id}`, body);
      else await this.api.post('/warehouses', body);
      this.toasts.show(this.i18n.t('common.saved'));
      this.editing.set(undefined);
      await this.ngOnInit();
    } catch (e) {
      const err = ApiError.from(e);
      const rest = applyServerErrors(this.form, err);
      this.error.set(rest.length ? rest.join(' ') : Object.keys(err.fields).length ? '' : err.message);
    } finally {
      this.saving.set(false);
    }
  }
}
