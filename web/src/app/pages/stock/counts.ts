import { Component, computed, DestroyRef, effect, ElementRef, inject, input, OnInit, signal, untracked, viewChild } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { ArrowLeft, Camera, CameraOff, ClipboardCheck, LucideAngularModule, Plus, ScanBarcode } from 'lucide-angular';
import { Api, ApiError, S } from '../../core/api';
import { I18n } from '../../core/i18n';
import { Toasts } from '../../core/toast';
import { Sheet } from '../../shared/ui';

const BADGE: Record<string, string> = { Counting: 'badge-signal', Posted: 'badge-ok', Cancelled: 'badge-muted' };

@Component({
  selector: 'app-counts',
  imports: [FormsModule, LucideAngularModule, Sheet],
  template: `
    <div class="page">
      <div class="page-head">
        <div>
          <div class="eyebrow">{{ i18n.t('nav.inventory') }}</div>
          <h1 class="h1 mt-1">{{ i18n.t('count.title') }}</h1>
          <p class="mt-1 text-muted">{{ i18n.t('count.subtitle') }}</p>
        </div>
        <button class="btn btn-primary" (click)="starting.set(true)"><lucide-icon [img]="PlusIcon" [size]="16" />{{ i18n.t('count.new') }}</button>
      </div>
      <div class="card overflow-hidden">
        <table class="tbl">
          <thead><tr><th>{{ i18n.t('stock.document') }}</th><th>{{ i18n.t('adjust.warehouse') }}</th><th>{{ i18n.t('common.status') }}</th><th>{{ i18n.t('count.counted') }}</th><th>{{ i18n.t('stock.date') }}</th></tr></thead>
          <tbody>
            @for (c of list(); track c.id) {
              <tr class="clickable" (click)="router.navigate(['/stock/counts', c.id])">
                <td class="code">{{ c.number }}</td>
                <td class="code">{{ c.warehouse }}</td>
                <td><span class="badge" [class]="badge[c.status]">{{ i18n.t($any('count.status.' + c.status)) }}</span></td>
                <td class="text-[13px]">
                  {{ i18n.t('count.progress', { counted: c.counted, total: c.lines }) }}
                  @if (c.variances) { · <span class="text-signal-ink">{{ i18n.t('count.variances', { n: c.variances }) }}</span> }
                </td>
                <td class="text-[13px] text-muted">{{ i18n.relative(c.createdAt) }}</td>
              </tr>
            } @empty {
              <tr><td colspan="5" class="!py-14 text-center text-muted">{{ i18n.t('count.empty') }}</td></tr>
            }
          </tbody>
        </table>
      </div>
    </div>

    @if (starting()) {
      <app-sheet [title]="i18n.t('count.new')" (closed)="starting.set(false)">
        <div class="flex flex-col gap-4">
          <label class="field">
            <span class="label">{{ i18n.t('adjust.warehouse') }}</span>
            <select class="input" [(ngModel)]="warehouseId">
              @for (w of warehouses(); track w.id) { <option [value]="w.id">{{ w.code }} · {{ w.name }}</option> }
            </select>
          </label>
          <label class="field">
            <span class="label">{{ i18n.t('count.category') }}</span>
            <select class="input" [(ngModel)]="categoryId">
              <option value="">{{ i18n.t('count.allCategories') }}</option>
              @for (c of categories(); track c.id) { <option [value]="c.id">{{ c.name }}</option> }
            </select>
          </label>
          <label class="field">
            <span class="label">{{ i18n.t('adjust.note') }} <span class="text-faint font-normal">· {{ i18n.t('common.optional') }}</span></span>
            <input class="input" [(ngModel)]="note" />
          </label>
        </div>
        <ng-container actions>
          <button class="btn" (click)="starting.set(false)">{{ i18n.t('common.cancel') }}</button>
          <button class="btn btn-primary" (click)="start()" [disabled]="busy()">{{ i18n.t('count.new') }}</button>
        </ng-container>
      </app-sheet>
    }
  `,
})
export class CountsPage implements OnInit {
  i18n = inject(I18n);
  router = inject(Router);
  private api = inject(Api);
  private toasts = inject(Toasts);
  PlusIcon = Plus;
  badge = BADGE;
  list = signal<S['CountListItem'][]>([]);
  warehouses = signal<S['WarehouseDto'][]>([]);
  categories = signal<S['CategoryDto'][]>([]);
  starting = signal(false);
  busy = signal(false);
  warehouseId = '';
  categoryId = '';
  note = '';

  async ngOnInit() {
    const [list, w, c] = await Promise.all([
      this.api.get<S['CountListItem'][]>('/stock-counts'),
      this.api.get<S['WarehouseDto'][]>('/warehouses'),
      this.api.get<S['CategoryDto'][]>('/categories'),
    ]);
    this.list.set(list);
    this.warehouses.set(w.filter((x) => x.isActive));
    this.categories.set(c);
    this.warehouseId ||= this.warehouses()[0]?.id ?? '';
  }

  async start() {
    this.busy.set(true);
    try {
      const res = await this.api.post<{ id: string; number: string }>('/stock-counts', {
        warehouseId: this.warehouseId, categoryId: this.categoryId || null, note: this.note || null,
      } satisfies S['CountStartInput']);
      this.toasts.show(this.i18n.t('count.started', { number: res.number }));
      this.router.navigate(['/stock/counts', res.id]);
    } catch (e) {
      this.toasts.show(ApiError.from(e).message, 'error');
    } finally {
      this.busy.set(false);
    }
  }
}

type Filter = 'all' | 'todo' | 'diff';

/** Minimal typing for the browser's BarcodeDetector (Chrome on Android, macOS and ChromeOS). */
interface Detector { detect(source: HTMLVideoElement): Promise<{ rawValue: string }[]> }

/**
 * Counting screen, built for a phone in one hand: a big scan box (hardware scanners type into
 * it), the camera where the browser can read barcodes, and the lines with what is still missing.
 */
@Component({
  selector: 'app-count',
  imports: [FormsModule, RouterLink, LucideAngularModule],
  template: `
    <div class="page-narrow !max-w-[960px]">
      <a routerLink="/stock/counts" class="inline-flex items-center gap-1.5 text-[13px] text-muted hover:text-ink">
        <lucide-icon [img]="I.back" [size]="15" />{{ i18n.t('count.title') }}
      </a>
      @if (c(); as c) {
        <div class="page-head mt-3 !mb-4">
          <div>
            <h1 class="h1 font-mono">{{ c.number }}</h1>
            <p class="mt-1.5 flex flex-wrap items-center gap-2 text-[13px] text-muted">
              <span class="badge" [class]="badge[c.status]">{{ i18n.t($any('count.status.' + c.status)) }}</span>
              <span class="code">{{ c.warehouse }}</span> @if (c.category) { · {{ c.category }} }
              · {{ i18n.t('count.progress', { counted: countedLines(), total: c.lines.length }) }}
              @if (c.adjustment) { · {{ i18n.t('count.adjustment', { number: c.adjustment }) }} }
            </p>
          </div>
          @if (c.status === 'Counting') {
            <div class="flex gap-2">
              <button class="btn btn-ghost btn-danger" (click)="cancel()">{{ i18n.t('count.cancel') }}</button>
              <button class="btn btn-primary" (click)="post()" [disabled]="busy()"><lucide-icon [img]="I.done" [size]="16" />{{ i18n.t('count.post') }}</button>
            </div>
          }
        </div>

        @if (c.status === 'Counting') {
          <div class="card p-3 mb-4">
            <div class="flex items-center gap-2">
              <label class="flex flex-1 items-center gap-3 rounded-lg border border-line-strong px-3 h-12 focus-within:border-brand focus-within:ring-3 focus-within:ring-brand/15">
                <lucide-icon [img]="I.scan" [size]="20" class="text-brand shrink-0" />
                <input #box class="flex-1 bg-transparent outline-none font-mono text-[16px] placeholder:font-sans placeholder:text-faint"
                       [placeholder]="i18n.t('count.scan')" (keydown.enter)="scanBox(box)" autofocus inputmode="numeric" />
              </label>
              @if (canCamera) {
                <button class="btn h-12" (click)="camera() ? stopCamera() : startCamera()">
                  <lucide-icon [img]="camera() ? I.cameraOff : I.camera" [size]="18" />
                  <span class="hidden sm:inline">{{ i18n.t(camera() ? 'count.stopCamera' : 'count.camera') }}</span>
                </button>
              }
            </div>
            @if (camera()) { <video #video class="mt-3 w-full max-h-64 rounded-lg bg-ink object-cover" playsinline muted></video> }
            <p class="hint mt-2">{{ i18n.t('count.scanHint') }}</p>
            @if (choose().length) {
              <div class="mt-3 rounded-lg bg-signal-soft p-3">
                <div class="text-[13px] font-medium text-signal-ink mb-2">{{ i18n.t('count.chooseBatch') }}</div>
                <div class="flex flex-wrap gap-2">
                  @for (l of choose(); track l.id) {
                    <button class="btn btn-sm" (click)="addTo(l)">{{ l.batch }} · {{ l.expiresOn }}</button>
                  }
                </div>
              </div>
            }
          </div>
        }

        <div class="card overflow-hidden">
          <div class="flex flex-wrap items-center gap-2.5 p-3 border-b border-line">
            <div class="flex rounded-lg border border-line-strong p-0.5 bg-sunken/50">
              @for (f of filters; track f) {
                <button class="h-8 px-3 rounded-md text-[13px] font-medium" [class]="filter() === f ? 'bg-surface text-ink shadow-sm' : 'text-muted hover:text-ink'"
                        (click)="filter.set(f)">{{ i18n.t($any('count.filter.' + f)) }}</button>
              }
            </div>
            <input class="input flex-1 min-w-[180px]" [placeholder]="i18n.t('count.search')" [ngModel]="q()" (ngModelChange)="q.set($event)" />
          </div>
          <div class="max-h-[60vh] overflow-auto">
            <table class="tbl">
              <thead class="sticky top-0">
                <tr><th class="hidden sm:table-cell">{{ i18n.t('products.sku') }}</th><th>{{ i18n.t('common.name') }}</th><th class="text-right">{{ i18n.t('count.expected') }}</th>
                  <th class="w-24 sm:w-32">{{ i18n.t('count.counted') }}</th><th class="text-right hidden sm:table-cell">{{ i18n.t('count.variance') }}</th></tr>
              </thead>
              <tbody>
                @for (l of visible(); track l.id) {
                  <tr [class.flash]="flash() === l.id">
                    <td class="code whitespace-nowrap hidden sm:table-cell">{{ l.sku }}</td>
                    <td><span class="code sm:hidden block text-[11.5px] whitespace-nowrap">{{ l.sku }}</span>{{ l.name }} @if (l.batch) { <div class="text-[12px] text-muted code">{{ l.batch }} · {{ l.expiresOn }}</div> }</td>
                    <td class="num">{{ i18n.number(l.expected, 4) }}</td>
                    <td>
                      @if (c.status === 'Counting') {
                        <input class="input num !text-left h-8" type="number" min="0" step="any" [ngModel]="l.counted" (change)="set(l, $any($event.target).value)" />
                      } @else { <span class="num">{{ l.counted ?? '—' }}</span> }
                      @if (l.variance != null && l.variance !== 0) {
                        <div class="sm:hidden mt-0.5 text-[12px] font-medium tabular-nums" [class]="l.variance > 0 ? 'text-ok' : 'text-danger'">{{ l.variance > 0 ? '+' : '' }}{{ i18n.number(l.variance, 4) }}</div>
                      }
                    </td>
                    <td class="num font-medium hidden sm:table-cell" [class]="l.variance == null ? 'text-faint' : l.variance > 0 ? 'text-ok' : l.variance < 0 ? 'text-danger' : ''">
                      {{ l.variance == null ? i18n.t('count.notCounted') : (l.variance > 0 ? '+' : '') + i18n.number(l.variance, 4) }}
                    </td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
        </div>
      }
    </div>
  `,
  styles: `
    .flash { animation: flash 900ms ease-out; }
    @keyframes flash { 0% { background: var(--color-signal-soft); } }
  `,
})
export class CountPage {
  i18n = inject(I18n);
  private api = inject(Api);
  private router = inject(Router);
  private toasts = inject(Toasts);
  id = input.required<string>();
  I = { back: ArrowLeft, scan: ScanBarcode, camera: Camera, cameraOff: CameraOff, done: ClipboardCheck };
  badge = BADGE;
  filters: Filter[] = ['all', 'todo', 'diff'];
  canCamera = typeof window !== 'undefined' && 'BarcodeDetector' in window;

  c = signal<S['CountDto'] | null>(null);
  filter = signal<Filter>('all');
  q = signal('');
  busy = signal(false);
  flash = signal('');
  choose = signal<S['CountLineDto'][]>([]);
  camera = signal(false);
  video = viewChild<ElementRef<HTMLVideoElement>>('video');
  private stream: MediaStream | null = null;
  private lastCode = { code: '', at: 0 };
  private pending = 0;

  countedLines = computed(() => this.c()?.lines.filter((l) => l.counted != null).length ?? 0);
  visible = computed(() => {
    const q = this.q().trim().toLowerCase();
    return (this.c()?.lines ?? []).filter((l) =>
      (this.filter() === 'all' || (this.filter() === 'todo' ? l.counted == null : l.variance != null && l.variance !== 0)) &&
      (!q || l.sku.toLowerCase().includes(q) || l.name.toLowerCase().includes(q)));
  });

  constructor() {
    effect(() => {
      const id = this.id();
      untracked(() => this.load(id));
    });
    inject(DestroyRef).onDestroy(() => this.stopCamera());
  }

  private async load(id: string) {
    this.c.set(await this.api.get<S['CountDto']>(`/stock-counts/${id}`));
  }

  scanBox(box: HTMLInputElement) {
    const code = box.value.trim();
    box.value = '';
    if (code) this.scan(code);
  }

  async scan(code: string) {
    try {
      const r = await this.api.post<S['ScanResult']>(`/stock-counts/${this.id()}/scan`, { code, quantity: null });
      if (r.choose.length) {
        this.choose.set(r.choose);
        this.pending = r.added;
        return;
      }
      this.applyCounted(r.lineId!, r.counted ?? null);
      this.toasts.show(this.i18n.t('count.added', { n: this.i18n.number(r.added), sku: r.sku ?? '' }));
    } catch (e) {
      this.toasts.show(ApiError.from(e).message, 'error');
    }
  }

  async addTo(l: S['CountLineDto']) {
    this.choose.set([]);
    await this.set(l, String((l.counted ?? 0) + this.pending));
  }

  async set(l: S['CountLineDto'], raw: string) {
    const counted = raw === '' ? null : Number(raw);
    try {
      await this.api.post(`/stock-counts/${this.id()}/set`, { lineId: l.id, counted });
      this.applyCounted(l.id, counted);
    } catch (e) {
      this.toasts.show(ApiError.from(e).message, 'error');
    }
  }

  private applyCounted(lineId: string, counted: number | null) {
    this.c.update((c) => c && {
      ...c,
      lines: c.lines.map((l) => (l.id === lineId ? { ...l, counted, variance: counted == null ? null : counted - l.expected } : l)),
    });
    this.flash.set('');
    setTimeout(() => this.flash.set(lineId));
  }

  async startCamera() {
    try {
      this.stream = await navigator.mediaDevices.getUserMedia({ video: { facingMode: 'environment' } });
      this.camera.set(true);
      setTimeout(async () => {
        const video = this.video()!.nativeElement;
        video.srcObject = this.stream;
        await video.play();
        const detector: Detector = new (window as unknown as { BarcodeDetector: new (o: object) => Detector }).BarcodeDetector({
          formats: ['ean_13', 'ean_8', 'upc_a', 'upc_e', 'code_128'],
        });
        const loop = async () => {
          if (!this.camera()) return;
          const codes = await detector.detect(video).catch(() => []);
          const code = codes[0]?.rawValue;
          // The same code stays in view for a while: count it once per 1.5 s.
          if (code && (code !== this.lastCode.code || Date.now() - this.lastCode.at > 1500)) {
            this.lastCode = { code, at: Date.now() };
            this.scan(code);
          }
          setTimeout(loop, 250);
        };
        loop();
      });
    } catch {
      this.camera.set(false);
    }
  }

  stopCamera() {
    this.camera.set(false);
    this.stream?.getTracks().forEach((t) => t.stop());
    this.stream = null;
  }

  async post() {
    const diffs = this.c()!.lines.filter((l) => l.variance != null && l.variance !== 0).length;
    if (!confirm(this.i18n.t('count.postConfirm', { n: diffs }))) return;
    this.busy.set(true);
    try {
      this.c.set(await this.api.post<S['CountDto']>(`/stock-counts/${this.id()}/post`, {}));
      this.toasts.show(this.i18n.t('count.posted'));
      this.stopCamera();
    } catch (e) {
      this.toasts.show(ApiError.from(e).message, 'error');
    } finally {
      this.busy.set(false);
    }
  }

  async cancel() {
    await this.api.post(`/stock-counts/${this.id()}/cancel`, {});
    this.router.navigate(['/stock/counts']);
  }
}
