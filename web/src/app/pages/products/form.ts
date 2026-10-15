import { Component, computed, inject, input, OnInit, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { FormArray, FormBuilder, NonNullableFormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { ArrowLeft, LucideAngularModule, Plus, Trash2 } from 'lucide-angular';
import { Api, ApiError, S } from '../../core/api';
import { Auth } from '../../core/auth';
import { I18n } from '../../core/i18n';
import { Toasts } from '../../core/toast';
import { AuditTrail } from '../../shared/audit-trail';
import { applyServerErrors, fieldError } from '../../shared/ui';

const unitGroup = (fb: NonNullableFormBuilder, name: string, factor: number) =>
  fb.group({ name: [name, Validators.required], factor: [factor, [Validators.required, Validators.min(2)]] });
const barcodeGroup = (fb: NonNullableFormBuilder, code: string, unit: string | null) =>
  fb.group({ code: [code, Validators.required], unit: fb.control<string | null>(unit) });
type UnitGroup = ReturnType<typeof unitGroup>;
type BarcodeGroup = ReturnType<typeof barcodeGroup>;

@Component({
  selector: 'app-product-form',
  imports: [ReactiveFormsModule, RouterLink, LucideAngularModule, AuditTrail],
  template: `
    <form class="page-narrow" [formGroup]="form" (ngSubmit)="save()">
      <a routerLink="/products" class="inline-flex items-center gap-1.5 text-[13px] text-muted hover:text-ink">
        <lucide-icon [img]="BackIcon" [size]="15" />{{ i18n.t('nav.products') }}
      </a>
      <div class="page-head mt-3">
        <div class="min-w-0">
          <h1 class="h1 truncate">{{ id() ? form.value.name || i18n.t('product.edit') : i18n.t('product.new') }}</h1>
          @if (id()) {
            <p class="mt-1 code">{{ form.getRawValue().sku }}</p>
          }
        </div>
        @if (editable()) {
          <div class="flex gap-2">
            <a class="btn" routerLink="/products">{{ i18n.t('common.cancel') }}</a>
            <button class="btn btn-primary" [disabled]="saving() || (id() && form.pristine)">
              {{ saving() ? i18n.t('common.saving') : i18n.t('common.save') }}
            </button>
          </div>
        }
      </div>

      @if (error()) {
        <div class="mb-4 rounded-lg bg-danger-soft px-4 py-3 text-[13.5px] text-danger">{{ error() }}</div>
      }

      <div class="flex flex-col gap-5">
        <section class="card">
          <div class="card-head"><h2 class="card-title">{{ i18n.t('product.basics') }}</h2></div>
          <div class="grid sm:grid-cols-[180px_1fr] gap-4 p-5">
            <label class="field">
              <span class="label">{{ i18n.t('products.sku') }}</span>
              <input class="input input-mono uppercase" formControlName="sku" [class.invalid]="err('sku')" />
              @if (err('sku'); as e) { <span class="error">{{ e }}</span> }
            </label>
            <label class="field">
              <span class="label">{{ i18n.t('product.name') }}</span>
              <input class="input" formControlName="name" [class.invalid]="err('name')" />
              @if (err('name'); as e) { <span class="error">{{ e }}</span> }
            </label>
            <label class="field sm:col-span-2">
              <span class="label">{{ i18n.t('product.category') }}</span>
              <select class="input" formControlName="categoryId" (change)="maybeNewCategory()">
                <option [ngValue]="null">{{ i18n.t('product.noCategory') }}</option>
                @for (c of categories(); track c.id) {
                  <option [ngValue]="c.id">{{ c.name }}</option>
                }
                @if (editable()) {
                  <option [ngValue]="NEW">{{ i18n.t('product.newCategory') }}</option>
                }
              </select>
            </label>
            <label class="field sm:col-span-2">
              <span class="label">{{ i18n.t('product.description') }} <span class="text-faint font-normal">· {{ i18n.t('common.optional') }}</span></span>
              <textarea class="input" rows="2" formControlName="description"></textarea>
            </label>
          </div>
        </section>

        <section class="card">
          <div class="card-head"><h2 class="card-title">{{ i18n.t('product.stockPricing') }}</h2></div>
          <div class="grid sm:grid-cols-3 gap-4 p-5">
            <label class="field">
              <span class="label">{{ i18n.t('product.baseUnit') }}</span>
              <input class="input" formControlName="baseUnit" [class.invalid]="err('baseUnit')" />
              @if (err('baseUnit'); as e) { <span class="error">{{ e }}</span> } @else {
                <span class="hint">{{ i18n.t('product.baseUnitHint') }}</span>
              }
            </label>
            <label class="field">
              <span class="label">{{ i18n.t('product.salePrice') }}</span>
              <div class="relative">
                <span class="absolute left-3 top-1/2 -translate-y-1/2 text-faint text-[13px]">{{ auth.currency() }}</span>
                <input class="input num !text-left pl-12" type="number" step="0.01" min="0" formControlName="salePrice" [class.invalid]="err('salePrice')" />
              </div>
              @if (err('salePrice'); as e) { <span class="error">{{ e }}</span> }
            </label>
            <label class="field">
              <span class="label">{{ i18n.t('product.taxRate') }}</span>
              <input class="input num !text-left" type="number" step="0.01" min="0" max="100" formControlName="taxRate" [class.invalid]="err('taxRate')" />
              @if (err('taxRate'); as e) { <span class="error">{{ e }}</span> }
            </label>
            <label class="sm:col-span-3 flex items-start gap-3 rounded-lg border border-line p-3.5 cursor-pointer hover:bg-paper">
              <input type="checkbox" class="check mt-0.5" formControlName="trackBatches" />
              <span>
                <span class="block font-medium">{{ i18n.t('product.trackBatches') }}</span>
                <span class="hint">{{ i18n.t('product.trackBatchesHint') }}</span>
              </span>
            </label>
            <label class="flex items-center gap-2.5 cursor-pointer">
              <input type="checkbox" class="check" formControlName="isActive" />
              <span class="font-medium">{{ i18n.t('common.active') }}</span>
            </label>
          </div>
        </section>

        <section class="card">
          <div class="card-head">
            <div>
              <h2 class="card-title">{{ i18n.t('product.units') }}</h2>
              <p class="hint mt-0.5">{{ i18n.t('product.unitsHint') }}</p>
            </div>
            @if (editable()) {
              <button type="button" class="btn btn-sm" (click)="addUnit()"><lucide-icon [img]="PlusIcon" [size]="14" />{{ i18n.t('product.addUnit') }}</button>
            }
          </div>
          <div class="p-5 flex flex-col gap-2.5" formArrayName="units">
            @for (u of units.controls; track u; let i = $index) {
              <div class="grid grid-cols-[1fr_auto_1fr_auto] items-start gap-2.5" [formGroupName]="i">
                <div class="field">
                  <input class="input" formControlName="name" [placeholder]="i18n.t('product.unitName')" [class.invalid]="err('units.' + i + '.name')" />
                  @if (err('units.' + i + '.name'); as e) { <span class="error">{{ e }}</span> }
                </div>
                <span class="h-9 grid place-items-center text-muted text-[13px]">=</span>
                <div class="field">
                  <div class="relative">
                    <input class="input num !text-left pr-20" type="number" min="2" formControlName="factor" [class.invalid]="err('units.' + i + '.factor')" />
                    <span class="absolute right-3 top-1/2 -translate-y-1/2 text-faint text-[13px]">{{ baseUnit() }}</span>
                  </div>
                  @if (err('units.' + i + '.factor'); as e) { <span class="error">{{ e }}</span> }
                </div>
                @if (editable()) {
                  <button type="button" class="btn btn-ghost btn-sm h-9" (click)="units.removeAt(i); form.markAsDirty()" [attr.aria-label]="i18n.t('common.remove')">
                    <lucide-icon [img]="TrashIcon" [size]="15" />
                  </button>
                }
              </div>
            } @empty {
              <p class="text-muted text-[13px]">{{ i18n.t('common.none') }}</p>
            }
            @if (err('units'); as e) { <span class="error">{{ e }}</span> }
          </div>
        </section>

        <section class="card">
          <div class="card-head">
            <div>
              <h2 class="card-title">{{ i18n.t('product.barcodes') }}</h2>
              <p class="hint mt-0.5">{{ i18n.t('product.barcodesHint') }}</p>
            </div>
            @if (editable()) {
              <button type="button" class="btn btn-sm" (click)="addBarcode()"><lucide-icon [img]="PlusIcon" [size]="14" />{{ i18n.t('product.addBarcode') }}</button>
            }
          </div>
          <div class="p-5 flex flex-col gap-2.5" formArrayName="barcodes">
            @for (b of barcodes.controls; track b; let i = $index) {
              <div class="grid grid-cols-[1fr_160px_auto] items-start gap-2.5" [formGroupName]="i">
                <div class="field">
                  <input class="input input-mono" formControlName="code" [placeholder]="i18n.t('product.barcode')" [class.invalid]="err('barcodes.' + i + '.code')" />
                  @if (err('barcodes.' + i + '.code'); as e) { <span class="error">{{ e }}</span> }
                </div>
                <select class="input" formControlName="unit" [attr.aria-label]="i18n.t('product.barcodeUnit')">
                  @for (name of unitNames(); track name) {
                    <option [ngValue]="name">{{ name }}</option>
                  }
                </select>
                @if (editable()) {
                  <button type="button" class="btn btn-ghost btn-sm h-9" (click)="barcodes.removeAt(i); form.markAsDirty()" [attr.aria-label]="i18n.t('common.remove')">
                    <lucide-icon [img]="TrashIcon" [size]="15" />
                  </button>
                }
              </div>
            } @empty {
              <p class="text-muted text-[13px]">{{ i18n.t('common.none') }}</p>
            }
            @if (err('barcodes'); as e) { <span class="error">{{ e }}</span> }
          </div>
        </section>

        @if (id() && auth.can('ViewAudit')) {
          <app-audit-trail [entityId]="id()!" [refresh]="version()" />
        }
      </div>
    </form>
  `,
})
export class ProductForm implements OnInit {
  i18n = inject(I18n);
  auth = inject(Auth);
  private api = inject(Api);
  private router = inject(Router);
  private toasts = inject(Toasts);
  private fb = inject(FormBuilder).nonNullable;

  id = input<string>();
  readonly NEW = '__new__';
  BackIcon = ArrowLeft;
  PlusIcon = Plus;
  TrashIcon = Trash2;

  categories = signal<S['CategoryDto'][]>([]);
  saving = signal(false);
  error = signal('');
  version = signal<number | null>(null);
  editable = computed(() => this.auth.can('EditCatalog'));

  form = this.fb.group({
    sku: ['', [Validators.required, Validators.pattern(/^[A-Za-z0-9._-]+$/)]],
    name: ['', Validators.required],
    description: [''],
    categoryId: this.fb.control<string | null>(null),
    baseUnit: ['pcs', Validators.required],
    salePrice: [0, [Validators.required, Validators.min(0)]],
    taxRate: [0, [Validators.required, Validators.min(0), Validators.max(100)]],
    trackBatches: [false],
    isActive: [true],
    units: this.fb.array<UnitGroup>([]),
    barcodes: this.fb.array<BarcodeGroup>([]),
  });

  get units() {
    return this.form.controls.units as FormArray<UnitGroup>;
  }
  get barcodes() {
    return this.form.controls.barcodes as FormArray<BarcodeGroup>;
  }

  private value = toSignal(this.form.valueChanges, { initialValue: this.form.value });
  baseUnit = computed(() => this.value().baseUnit || 'pcs');
  unitNames = computed(() => [this.baseUnit(), ...(this.value().units ?? []).map((u) => u.name).filter((n): n is string => !!n)]);

  async ngOnInit() {
    this.categories.set(await this.api.get<S['CategoryDto'][]>('/categories'));
    const id = this.id();
    if (id) {
      const p = await this.api.get<S['ProductDto']>(`/products/${id}`);
      this.fill(p);
    } else {
      this.addUnit();
    }
    if (!this.editable()) this.form.disable();
  }

  unitGroup(name = '', factor = 12) {
    return unitGroup(this.fb, name, factor);
  }
  barcodeGroup(code = '', unit: string | null = null) {
    return barcodeGroup(this.fb, code, unit ?? this.form.controls.baseUnit.value);
  }
  addUnit() {
    this.units.push(this.unitGroup(this.units.length === 0 ? 'case' : ''));
    this.form.markAsDirty();
  }
  addBarcode() {
    this.barcodes.push(this.barcodeGroup());
    this.form.markAsDirty();
  }

  async maybeNewCategory() {
    if (this.form.controls.categoryId.value !== this.NEW) return;
    const name = prompt(this.i18n.t('product.newCategoryPrompt'))?.trim();
    if (!name) {
      this.form.controls.categoryId.setValue(null);
      return;
    }
    const cat = await this.api.post<S['CategoryDto']>('/categories', { name });
    this.categories.update((list) => [...list.filter((c) => c.id !== cat.id), cat].sort((a, b) => a.name.localeCompare(b.name)));
    this.form.controls.categoryId.setValue(cat.id);
  }

  err(path: string) {
    return fieldError(this.form.get(path), this.i18n);
  }

  async save() {
    this.form.markAllAsTouched();
    if (this.form.invalid) {
      this.error.set(this.i18n.t('form.fix'));
      return;
    }
    this.saving.set(true);
    this.error.set('');
    const v = this.form.getRawValue();
    const body: S['ProductInput'] = { ...v, description: v.description || null, version: this.version() };
    try {
      const id = this.id();
      const saved = id
        ? await this.api.put<S['ProductDto']>(`/products/${id}`, body)
        : await this.api.post<S['ProductDto']>('/products', body);
      this.toasts.show(id ? this.i18n.t('common.saved') : this.i18n.t('product.created'));
      if (id) this.fill(saved);
      else this.router.navigate(['/products', saved.id], { replaceUrl: true });
    } catch (e) {
      const err = ApiError.from(e);
      const rest = applyServerErrors(this.form, err);
      this.error.set(rest.length ? rest.join(' ') : err.message);
    } finally {
      this.saving.set(false);
    }
  }

  private fill(p: S['ProductDto']) {
    this.units.clear();
    this.barcodes.clear();
    p.units.forEach((u) => this.units.push(this.unitGroup(u.name, u.factor)));
    p.barcodes.forEach((b) => this.barcodes.push(this.barcodeGroup(b.code, b.unit)));
    this.form.patchValue({ ...p, description: p.description ?? '' });
    this.version.set(p.version);
    this.form.markAsPristine();
  }
}
