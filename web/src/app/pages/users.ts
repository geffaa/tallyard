import { Component, computed, inject, OnInit, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { LucideAngularModule, Plus } from 'lucide-angular';
import { Api, ApiError, Page, S } from '../core/api';
import { Auth } from '../core/auth';
import { I18n } from '../core/i18n';
import { Toasts } from '../core/toast';
import { applyServerErrors, fieldError, Sheet } from '../shared/ui';

const ROLES = ['Owner', 'Purchasing', 'Warehouse', 'Sales', 'Finance'] as const;

@Component({
  selector: 'app-users',
  imports: [ReactiveFormsModule, LucideAngularModule, Sheet],
  template: `
    <div class="page">
      <div class="page-head">
        <div>
          <div class="eyebrow">{{ i18n.t('nav.admin') }}</div>
          <h1 class="h1 mt-1">{{ i18n.t('users.title') }}</h1>
          <p class="mt-1 text-muted">{{ i18n.t('users.subtitle') }}</p>
        </div>
        <button class="btn btn-primary" (click)="edit(null)"><lucide-icon [img]="PlusIcon" [size]="16" />{{ i18n.t('users.new') }}</button>
      </div>

      <div class="card overflow-x-auto">
        <table class="tbl">
          <thead>
            <tr>
              <th>{{ i18n.t('common.name') }}</th>
              <th>{{ i18n.t('users.role') }}</th>
              <th class="hidden sm:table-cell">{{ i18n.t('users.access') }}</th>
              <th>{{ i18n.t('common.status') }}</th>
            </tr>
          </thead>
          <tbody>
            @for (u of users(); track u.id) {
              <tr class="clickable" (click)="edit(u)">
                <td>
                  <div class="font-medium">
                    {{ u.name }}
                    @if (u.id === auth.me()?.id) { <span class="text-muted font-normal">({{ i18n.t('users.you') }})</span> }
                  </div>
                  <div class="text-[12.5px] text-muted">{{ u.email }}</div>
                </td>
                <td><span class="badge" [class]="u.role === 'Owner' ? 'badge-brand' : 'badge-muted'">{{ i18n.t($any('role.' + u.role)) }}</span></td>
                <td class="hidden sm:table-cell text-ink-2">{{ accessLabel(u) }}</td>
                <td>
                  <span class="badge" [class]="u.isActive ? 'badge-ok' : 'badge-muted'">
                    {{ u.isActive ? i18n.t('common.active') : i18n.t('common.inactive') }}
                  </span>
                </td>
              </tr>
            }
          </tbody>
        </table>
      </div>
    </div>

    @if (editing() !== undefined) {
      <app-sheet [title]="editing() ? i18n.t('users.edit') : i18n.t('users.new')" (closed)="editing.set(undefined)">
        <form id="user" class="flex flex-col gap-4" [formGroup]="form" (ngSubmit)="save()">
          <label class="field">
            <span class="label">{{ i18n.t('common.name') }}</span>
            <input class="input" formControlName="name" [class.invalid]="err('name')" />
            @if (err('name'); as e) { <span class="error">{{ e }}</span> }
          </label>
          <label class="field">
            <span class="label">{{ i18n.t('common.email') }}</span>
            <input class="input" type="email" formControlName="email" [class.invalid]="err('email')" />
            @if (err('email'); as e) { <span class="error">{{ e }}</span> }
          </label>

          <fieldset class="field">
            <legend class="label mb-1.5">{{ i18n.t('users.role') }}</legend>
            <div class="flex flex-col gap-1.5">
              @for (r of roles; track r) {
                <label class="flex items-start gap-3 rounded-lg border p-3 cursor-pointer transition-colors"
                       [class]="value().role === r ? 'border-brand bg-brand-soft/50' : 'border-line hover:bg-paper'">
                  <input type="radio" class="mt-0.5 accent-brand" formControlName="role" [value]="r" />
                  <span>
                    <span class="block font-medium">{{ i18n.t($any('role.' + r)) }}</span>
                    <span class="hint">{{ i18n.t($any('role.' + r + '.desc')) }}</span>
                  </span>
                </label>
              }
            </div>
          </fieldset>

          <fieldset class="field">
            <legend class="label mb-1.5">{{ i18n.t('users.access') }}</legend>
            @if (value().role === 'Owner') {
              <p class="hint">{{ i18n.t('users.ownerSeesAll') }}</p>
            } @else {
              <label class="flex items-center gap-2.5 cursor-pointer">
                <input type="checkbox" class="check" formControlName="allWarehouses" />
                <span><span class="font-medium">{{ i18n.t('users.allWarehouses') }}</span>
                  <span class="hint"> · {{ i18n.t('users.allWarehousesHint') }}</span></span>
              </label>
              @if (!value().allWarehouses) {
                <div class="mt-2 grid grid-cols-2 gap-1.5">
                  @for (w of warehouses(); track w.id) {
                    <label class="flex items-center gap-2 rounded-lg border border-line px-3 py-2 cursor-pointer hover:bg-paper">
                      <input type="checkbox" class="check" [checked]="selected().has(w.id)" (change)="toggle(w.id)" />
                      <span class="code">{{ w.code }}</span>
                      <span class="truncate text-[13px]">{{ w.name }}</span>
                    </label>
                  }
                </div>
              }
            }
          </fieldset>

          <label class="field">
            <span class="label">{{ i18n.t('users.password') }}</span>
            <input class="input" type="password" formControlName="password" autocomplete="new-password" [class.invalid]="err('password')" />
            @if (err('password'); as e) { <span class="error">{{ e }}</span> } @else {
              <span class="hint">{{ editing() ? i18n.t('users.passwordKeep') : i18n.t('users.passwordNew') }}</span>
            }
          </label>
          <label class="flex items-center gap-2.5 cursor-pointer">
            <input type="checkbox" class="check" formControlName="isActive" />
            <span class="font-medium">{{ i18n.t('common.active') }}</span>
          </label>
          @if (error()) { <div class="rounded-lg bg-danger-soft px-3 py-2 text-[13px] text-danger">{{ error() }}</div> }
        </form>
        <ng-container actions>
          <button class="btn" (click)="editing.set(undefined)">{{ i18n.t('common.cancel') }}</button>
          <button class="btn btn-primary" form="user" [disabled]="saving()">{{ i18n.t('common.save') }}</button>
        </ng-container>
      </app-sheet>
    }
  `,
})
export class UsersPage implements OnInit {
  i18n = inject(I18n);
  auth = inject(Auth);
  private api = inject(Api);
  private toasts = inject(Toasts);

  PlusIcon = Plus;
  roles = ROLES;
  users = signal<S['UserDto'][]>([]);
  warehouses = signal<S['WarehouseDto'][]>([]);
  editing = signal<S['UserDto'] | null | undefined>(undefined);
  selected = signal(new Set<string>());
  saving = signal(false);
  error = signal('');

  form = inject(FormBuilder).nonNullable.group({
    name: ['', Validators.required],
    email: ['', [Validators.required, Validators.email]],
    role: ['Sales' as string, Validators.required],
    allWarehouses: [true],
    password: ['', Validators.minLength(8)],
    isActive: [true],
  });
  value = toSignal(this.form.valueChanges, { initialValue: this.form.getRawValue() });

  async ngOnInit() {
    const [users, warehouses] = await Promise.all([
      this.api.get<Page<S['UserDto']>>('/users', { pageSize: 200 }),
      this.api.get<S['WarehouseDto'][]>('/warehouses'),
    ]);
    this.users.set(users.items);
    this.warehouses.set(warehouses);
  }

  accessLabel = (u: S['UserDto']) =>
    u.role === 'Owner' || u.allWarehouses
      ? this.i18n.t('users.allWarehouses')
      : this.warehouses().filter((w) => u.warehouseIds.includes(w.id)).map((w) => w.code).join(', ') || '—';

  edit(u: S['UserDto'] | null) {
    this.error.set('');
    this.form.reset({
      name: u?.name ?? '',
      email: u?.email ?? '',
      role: u?.role ?? 'Sales',
      allWarehouses: u?.allWarehouses ?? true,
      password: '',
      isActive: u?.isActive ?? true,
    });
    const pw = this.form.controls.password;
    pw.setValidators(u ? Validators.minLength(8) : [Validators.required, Validators.minLength(8)]);
    pw.updateValueAndValidity();
    this.selected.set(new Set(u?.warehouseIds ?? []));
    this.editing.set(u);
  }

  toggle(id: string) {
    this.selected.update((s) => {
      const next = new Set(s);
      if (!next.delete(id)) next.add(id);
      return next;
    });
  }

  err(path: string) {
    return fieldError(this.form.get(path), this.i18n);
  }

  async save() {
    this.form.markAllAsTouched();
    if (this.form.invalid) return;
    const current = this.editing();
    const v = this.form.getRawValue();
    const body: S['UserInput'] = {
      ...v,
      password: v.password || null,
      warehouseIds: v.allWarehouses || v.role === 'Owner' ? [] : [...this.selected()],
      version: current?.version ?? null,
    };
    this.saving.set(true);
    try {
      if (current) await this.api.put(`/users/${current.id}`, body);
      else await this.api.post('/users', body);
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
