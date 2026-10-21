import { Component, computed, effect, inject, input, OnInit, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { DomSanitizer } from '@angular/platform-browser';
import { Router, RouterLink } from '@angular/router';
import { ArrowLeft, FileUp, LoaderCircle, LucideAngularModule, Sparkles, TriangleAlert } from 'lucide-angular';
import { Api, ApiError, Page, S } from '../../core/api';
import { Auth } from '../../core/auth';
import { I18n } from '../../core/i18n';
import { Toasts } from '../../core/toast';
import { ProductPicker } from '../../shared/product-picker';

const STATUS_BADGE: Record<string, string> = { Ready: 'badge-signal', Failed: 'badge-danger', Converted: 'badge-ok' };

@Component({
  selector: 'app-documents',
  imports: [LucideAngularModule],
  template: `
    <div class="page">
      <div class="page-head">
        <div>
          <div class="eyebrow">{{ i18n.t('nav.purchasing') }}</div>
          <h1 class="h1 mt-1">{{ i18n.t('docs.title') }}</h1>
          <p class="mt-1 text-muted max-w-[640px]">{{ i18n.t('docs.subtitle') }}</p>
        </div>
      </div>

      <label
        class="card mb-5 flex flex-col items-center justify-center gap-2 border-2 border-dashed px-6 py-10 text-center cursor-pointer transition-colors"
        [class]="dragging() ? 'border-brand bg-brand-soft/40' : 'border-line-strong hover:bg-paper'"
        (dragover)="$event.preventDefault(); dragging.set(true)"
        (dragleave)="dragging.set(false)"
        (drop)="drop($event)"
      >
        <input type="file" class="hidden" accept="application/pdf,image/png,image/jpeg,image/webp" (change)="pick($event)" [disabled]="!!reading()" />
        @if (reading(); as name) {
          <lucide-icon [img]="SpinIcon" [size]="28" class="text-brand animate-spin" />
          <div class="font-medium">{{ i18n.t('docs.reading', { name }) }}</div>
          <div class="hint">{{ i18n.t('docs.readingHint') }}</div>
        } @else {
          <span class="grid place-items-center size-12 rounded-full bg-brand-soft text-brand"><lucide-icon [img]="UploadIcon" [size]="22" /></span>
          <div class="font-medium">{{ i18n.t('docs.drop') }}</div>
          <div class="hint">PDF · PNG · JPEG · WebP · max 10 MB</div>
        }
      </label>

      <div class="card overflow-hidden">
        <table class="tbl">
          <thead>
            <tr>
              <th>{{ i18n.t('common.name') }}</th>
              <th>{{ i18n.t('review.supplier') }}</th>
              <th>{{ i18n.t('review.docNumber') }}</th>
              <th>{{ i18n.t('common.status') }}</th>
              <th class="text-right">{{ i18n.t('po.lines') }}</th>
              <th>{{ i18n.t('stock.date') }}</th>
            </tr>
          </thead>
          <tbody>
            @for (d of docs(); track d.id) {
              <tr class="clickable" (click)="router.navigate(['/purchasing/documents', d.id])">
                <td class="font-medium truncate max-w-[260px]">{{ d.fileName }}</td>
                <td>{{ d.supplier ?? '—' }}</td>
                <td class="code">{{ d.documentNumber ?? '—' }}</td>
                <td>
                  <span class="badge" [class]="badge[d.status]">{{ i18n.t($any('docs.status.' + d.status)) }}</span>
                  @if (d.purchaseOrder) { <span class="code ml-1.5">{{ d.purchaseOrder }}</span> }
                </td>
                <td class="num whitespace-nowrap">
                  {{ i18n.t('docs.lines', { n: d.lines }) }}
                  @if (d.flagged && d.status === 'Ready') { <div class="text-[11.5px] text-signal-ink">{{ i18n.t('docs.flagged', { n: d.flagged }) }}</div> }
                </td>
                <td class="text-[13px] text-muted">{{ i18n.relative(d.createdAt) }}</td>
              </tr>
            } @empty {
              <tr><td colspan="6" class="!py-14 text-center text-muted">{{ i18n.t('docs.empty') }}</td></tr>
            }
          </tbody>
        </table>
      </div>
    </div>
  `,
})
export class DocumentsPage implements OnInit {
  i18n = inject(I18n);
  router = inject(Router);
  private api = inject(Api);
  private toasts = inject(Toasts);
  UploadIcon = FileUp;
  SpinIcon = LoaderCircle;
  badge = STATUS_BADGE;
  docs = signal<S['DocumentListItem'][]>([]);
  dragging = signal(false);
  reading = signal<string | null>(null);

  async ngOnInit() {
    this.docs.set(await this.api.get<S['DocumentListItem'][]>('/ai/documents'));
  }

  pick(e: Event) {
    const file = (e.target as HTMLInputElement).files?.[0];
    if (file) this.upload(file);
  }

  drop(e: DragEvent) {
    e.preventDefault();
    this.dragging.set(false);
    const file = e.dataTransfer?.files?.[0];
    if (file) this.upload(file);
  }

  private async upload(file: File) {
    this.reading.set(file.name);
    try {
      const form = new FormData();
      form.append('file', file);
      const doc = await this.api.post<S['DocumentDto']>('/ai/documents', form);
      this.router.navigate(['/purchasing/documents', doc.id]);
    } catch (e) {
      this.toasts.show(ApiError.from(e).message, 'error');
    } finally {
      this.reading.set(null);
    }
  }
}

interface Row {
  line: S['ReviewLine'];
  include: boolean;
  productId: string | null;
  label: string;
  unit: string;
  units: string[];
  quantity: number;
  unitPrice: number;
}

@Component({
  selector: 'app-document-review',
  imports: [FormsModule, RouterLink, LucideAngularModule, ProductPicker],
  template: `
    <div class="px-4 sm:px-8 py-6">
      <a routerLink="/purchasing/documents" class="inline-flex items-center gap-1.5 text-[13px] text-muted hover:text-ink">
        <lucide-icon [img]="BackIcon" [size]="15" />{{ i18n.t('docs.title') }}
      </a>
      @if (doc(); as d) {
        <div class="page-head mt-3 !mb-4">
          <div class="min-w-0">
            <h1 class="h1 truncate">{{ d.fileName }}</h1>
            <p class="mt-1 flex flex-wrap items-center gap-2 text-[13px] text-muted">
              <span class="badge" [class]="badge[d.status]">{{ i18n.t($any('docs.status.' + d.status)) }}</span>
              @if (d.reader) {
                <span class="inline-flex items-center gap-1"><lucide-icon [img]="SparkIcon" [size]="13" class="text-brand" />
                  {{ i18n.t(d.reader === 'vision' ? 'review.viaVision' : 'review.viaPdf') }} · {{ i18n.t('review.stats', { seconds: (d.durationMs / 1000).toFixed(1), tokens: i18n.number(d.tokens) }) }}</span>
              }
            </p>
          </div>
          @if (d.status === 'Ready') {
            <button class="btn btn-primary" (click)="create()" [disabled]="busy() || !canCreate()">{{ i18n.t('review.create', { n: included() }) }}</button>
          }
        </div>

        @if (d.status === 'Failed') {
          <div class="mb-4 rounded-lg bg-danger-soft px-4 py-3 text-[13.5px] text-danger">{{ i18n.t($any('docs.status.Failed')) }}: {{ d.errorMessage }}</div>
        }
        @if (d.purchaseOrder) {
          <div class="mb-4 rounded-lg bg-ok-soft px-4 py-3 text-[13.5px] text-ok">
            {{ i18n.t('review.converted', { number: d.purchaseOrder }) }}
            <a class="underline ml-1" [routerLink]="['/purchasing', d.purchaseOrderId]">{{ d.purchaseOrder }}</a>
          </div>
        }

        <div class="grid gap-5 xl:grid-cols-[minmax(0,5fr)_minmax(0,7fr)]">
          <!-- The document itself, beside what was read from it -->
          <div class="card overflow-hidden xl:sticky xl:top-4 self-start h-[70vh] xl:h-[calc(100dvh-140px)] bg-sunken">
            @if (d.contentType === 'application/pdf') {
              <iframe class="w-full h-full" [src]="fileUrl()" title="document"></iframe>
            } @else {
              <div class="h-full overflow-auto"><img class="w-full" [src]="fileUrl()" alt="" /></div>
            }
          </div>

          @if (d.result; as r) {
            <div class="flex flex-col gap-4 min-w-0">
              <p class="hint">{{ i18n.t('review.hint') }}</p>
              @for (w of r.warnings; track w) {
                <div class="flex items-start gap-2 rounded-lg bg-signal-soft px-3 py-2 text-[13px] text-signal-ink">
                  <lucide-icon [img]="WarnIcon" [size]="15" class="mt-0.5 shrink-0" />{{ i18n.t($any('warn.' + w)) }}
                </div>
              }

              <section class="card">
                <div class="grid sm:grid-cols-2 gap-4 p-4">
                  <label class="field">
                    <span class="label">{{ i18n.t('review.supplier') }}</span>
                    <select class="input" [(ngModel)]="supplierId" [class.invalid]="!supplierId">
                      <option value="">—</option>
                      @for (s of suppliers(); track s.id) { <option [value]="s.id">{{ s.name }}</option> }
                    </select>
                    @if (r.extracted.supplierName) { <span class="hint">{{ i18n.t('review.supplierGuess', { name: r.extracted.supplierName }) }}</span> }
                  </label>
                  <label class="field">
                    <span class="label">{{ i18n.t('review.deliverTo') }}</span>
                    <select class="input" [(ngModel)]="warehouseId">
                      @for (w of warehouses(); track w.id) { <option [value]="w.id">{{ w.code }} · {{ w.name }}</option> }
                    </select>
                  </label>
                  <dl class="sm:col-span-2 grid grid-cols-2 sm:grid-cols-4 gap-3 text-[13px]">
                    <div><dt class="text-muted">{{ i18n.t('review.docNumber') }}</dt><dd class="code">{{ r.extracted.documentNumber ?? '—' }}</dd></div>
                    <div><dt class="text-muted">{{ i18n.t('review.docDate') }}</dt><dd class="tabular-nums">{{ r.extracted.documentDate ?? '—' }}</dd></div>
                    <div><dt class="text-muted">{{ i18n.t('review.total') }}</dt><dd class="num !text-left">{{ r.extracted.total != null ? i18n.number(r.extracted.total, 2) + ' ' + (r.extracted.currency ?? '') : '—' }}</dd></div>
                    <div><dt class="text-muted">{{ i18n.t('review.ourTotal') }}</dt><dd class="num !text-left">{{ i18n.number(linesTotal(), 2) }}</dd></div>
                  </dl>
                </div>
              </section>

              <section class="card divide-y divide-line">
                @for (row of rows(); track row.line.index) {
                  <div class="p-4 grid gap-3" [class.bg-signal-soft]="row.include && row.line.flags.length > 0" [class.opacity-50]="!row.include">
                    <div class="flex items-start gap-3">
                      <input type="checkbox" class="check mt-1" [(ngModel)]="row.include" (ngModelChange)="touch()" [attr.aria-label]="i18n.t('review.include')" />
                      <div class="min-w-0 flex-1">
                        <div class="text-[11.5px] uppercase tracking-wide text-muted">{{ i18n.t('review.printed') }}</div>
                        <div class="font-mono text-[13px]">{{ row.line.source.description }}</div>
                        <div class="text-[12px] text-muted">
                          @if (row.line.source.supplierCode) { <span class="code">{{ row.line.source.supplierCode }}</span> · }
                          {{ row.line.source.quantity }} {{ row.line.source.unit }} × {{ row.line.source.unitPrice }}
                          @if (row.line.source.lineTotal != null) { = {{ row.line.source.lineTotal }} }
                        </div>
                      </div>
                      <div class="flex flex-wrap justify-end gap-1">
                        <span class="badge" [class]="row.line.match === 'none' ? 'badge-danger' : row.line.match === 'ai' || row.line.match === 'name' ? 'badge-brand' : 'badge-ok'">{{ i18n.t($any('match.' + row.line.match)) }}</span>
                        @for (f of row.line.flags; track f) { <span class="badge badge-signal">{{ i18n.t($any('flag.' + f)) }}</span> }
                      </div>
                    </div>
                    @if (row.include) {
                      <div class="grid gap-2 sm:grid-cols-[minmax(0,1fr)_90px_120px_110px] pl-7">
                        <div class="field">
                          <app-product-picker [placeholder]="i18n.t('review.pick')" [label]="row.label" [invalid]="!row.productId" (picked)="choose(row, $event)" />
                          @if (row.line.candidates.length && (row.line.flags.includes('no_match') || row.line.flags.includes('check_match'))) {
                            <div class="flex flex-wrap gap-1">
                              @for (c of row.line.candidates.slice(0, 4); track c.productId) {
                                <button type="button" class="badge badge-muted hover:bg-brand-soft" [class.!bg-brand-soft]="row.productId === c.productId"
                                        (click)="chooseCandidate(row, c)">{{ c.sku }} · {{ c.name }}</button>
                              }
                            </div>
                          }
                        </div>
                        <input class="input num !text-left" type="number" min="0" step="any" [(ngModel)]="row.quantity" (ngModelChange)="touch()" [attr.aria-label]="i18n.t('po.qty')" />
                        <select class="input" [(ngModel)]="row.unit" [attr.aria-label]="i18n.t('po.unit')">
                          @for (u of row.units; track u) { <option [value]="u">{{ u }}</option> }
                        </select>
                        <input class="input num !text-left" type="number" min="0" step="0.01" [(ngModel)]="row.unitPrice" (ngModelChange)="touch()" [attr.aria-label]="i18n.t('po.price')" />
                      </div>
                    }
                  </div>
                }
              </section>
            </div>
          }
        </div>
      }
    </div>
  `,
})
export class DocumentReviewPage {
  i18n = inject(I18n);
  private api = inject(Api);
  private auth = inject(Auth);
  private router = inject(Router);
  private toasts = inject(Toasts);
  private sanitizer = inject(DomSanitizer);
  id = input.required<string>();
  BackIcon = ArrowLeft;
  SparkIcon = Sparkles;
  WarnIcon = TriangleAlert;
  badge = STATUS_BADGE;

  doc = signal<S['DocumentDto'] | null>(null);
  rows = signal<Row[]>([]);
  suppliers = signal<S['PartnerDto'][]>([]);
  warehouses = signal<S['WarehouseRef'][]>([]);
  supplierId = '';
  warehouseId = '';
  busy = signal(false);
  private version = signal(0);
  fileUrl = computed(() => this.sanitizer.bypassSecurityTrustResourceUrl(`/api/ai/documents/${this.id()}/file`));
  included = computed(() => (this.version(), this.rows().filter((r) => r.include).length));
  canCreate = computed(() => (this.version(), !!this.supplierId && this.included() > 0 && this.rows().every((r) => !r.include || (r.productId && r.quantity > 0))));

  constructor() {
    Promise.all([
      this.api.get<Page<S['PartnerDto']>>('/suppliers', { pageSize: 200 }),
      this.api.get<S['WarehouseRef'][]>('/warehouses/directory'),
    ]).then(([s, w]) => {
      this.suppliers.set(s.items);
      this.warehouses.set(w);
      this.warehouseId ||= w[0]?.id ?? '';
    });
    effect(() => {
      const id = this.id();
      untracked(() => this.load(id));
    });
  }

  touch() {
    this.version.update((v) => v + 1);
  }

  linesTotal() {
    this.version();
    return this.rows().filter((r) => r.include).reduce((s, r) => s + r.quantity * r.unitPrice, 0);
  }

  private async load(id: string) {
    const d = await this.api.get<S['DocumentDto']>(`/ai/documents/${id}`);
    this.doc.set(d);
    this.supplierId = d.result?.supplier?.id ?? '';
    const rows: Row[] = [];
    for (const line of d.result?.lines ?? []) {
      const units = line.productId ? await this.unitsOf(line.productId) : [];
      rows.push({
        line, include: true, productId: line.productId ?? null, label: line.sku ? `${line.sku} · ${line.name}` : '',
        unit: line.unit || units[0] || '', units, quantity: line.quantity, unitPrice: line.unitPrice,
      });
    }
    this.rows.set(rows);
    this.touch();
  }

  private async unitsOf(productId: string) {
    const p = await this.api.get<S['ProductDto']>(`/products/${productId}`);
    return [p.baseUnit, ...p.units.map((u) => u.name)];
  }

  async choose(row: Row, p: S['ProductListItem']) {
    row.productId = p.id;
    row.label = `${p.sku} · ${p.name}`;
    row.units = await this.unitsOf(p.id);
    if (!row.units.includes(row.unit)) row.unit = row.units.find((u) => u !== row.units[0]) ?? row.units[0];
    this.rows.set([...this.rows()]);
    this.touch();
  }

  chooseCandidate(row: Row, c: S['Candidate']) {
    this.choose(row, { id: c.productId, sku: c.sku, name: c.name } as S['ProductListItem']);
  }

  async create() {
    this.busy.set(true);
    try {
      const res = await this.api.post<{ id: string; number: string }>(`/ai/documents/${this.id()}/purchase-order`, {
        supplierId: this.supplierId, warehouseId: this.warehouseId, note: null,
        lines: this.rows().filter((r) => r.include).map((r) => ({ productId: r.productId!, quantity: r.quantity, unit: r.unit, unitPrice: r.unitPrice })),
      } satisfies S['ConvertInput']);
      this.toasts.show(this.i18n.t('review.created', { number: res.number }));
      this.router.navigate(['/purchasing', res.id]);
    } catch (e) {
      this.toasts.show(ApiError.from(e).message, 'error');
    } finally {
      this.busy.set(false);
    }
  }
}
