import { Component, effect, inject, input, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { ArrowLeft, LucideAngularModule, PackageCheck, ScanBarcode } from 'lucide-angular';
import { Api, ApiError, S } from '../../core/api';
import { I18n } from '../../core/i18n';
import { Toasts } from '../../core/toast';

interface Row {
  line: S['PoLineDto'];
  now: number;
  batch: string;
  expires: string;
  /** Barcodes of this product and the unit each one stands for. */
  barcodes: { code: string; factor: number }[];
}

@Component({
  selector: 'app-receive',
  imports: [FormsModule, RouterLink, LucideAngularModule],
  template: `
    <div class="page-narrow !max-w-[1040px]">
      <a [routerLink]="['/purchasing', id()]" class="inline-flex items-center gap-1.5 text-[13px] text-muted hover:text-ink">
        <lucide-icon [img]="BackIcon" [size]="15" />{{ po()?.number }}
      </a>
      @if (po(); as p) {
        <div class="page-head mt-3">
          <div>
            <h1 class="h1">{{ i18n.t('receive.title') }}</h1>
            <p class="mt-1 text-muted">{{ i18n.t('receive.subtitle', { number: p.number, supplier: p.supplier, warehouse: p.warehouse }) }}</p>
          </div>
          <button class="btn" (click)="fillAll()">{{ i18n.t('receive.all') }}</button>
        </div>

        @if (error()) { <div class="mb-4 rounded-lg bg-danger-soft px-4 py-3 text-[13.5px] text-danger">{{ error() }}</div> }

        <label class="card flex items-center gap-3 px-4 py-3 mb-5 focus-within:ring-2 focus-within:ring-brand/30">
          <lucide-icon [img]="ScanIcon" [size]="20" class="text-brand" />
          <input #box class="flex-1 bg-transparent outline-none font-mono text-[14px] placeholder:font-sans placeholder:text-faint"
                 [placeholder]="i18n.t('receive.scan')" (keydown.enter)="scanned(box)" autofocus />
          <span class="hint hidden sm:block">{{ i18n.t('receive.scanHint') }}</span>
        </label>

        <section class="card overflow-hidden">
          <div class="overflow-x-auto">
            <table class="tbl">
              <thead>
                <tr>
                  <th>{{ i18n.t('po.lines') }}</th>
                  <th class="text-right">{{ i18n.t('receive.ordered') }}</th>
                  <th class="text-right">{{ i18n.t('receive.already') }}</th>
                  <th class="text-right">{{ i18n.t('receive.remaining') }}</th>
                  <th class="w-36">{{ i18n.t('receive.now') }}</th>
                </tr>
              </thead>
              <tbody>
                @for (r of rows(); track r.line.id) {
                  <tr [class.bg-ok-soft]="r.now > 0" [class.flash]="flash() === r.line.id">
                    <td>
                      <div class="code">{{ r.line.sku }}</div>
                      <div class="text-[13.5px]">{{ r.line.name }}</div>
                      @if (r.line.trackBatches && r.now > 0) {
                        <div class="mt-2 grid grid-cols-2 gap-2 max-w-[360px]">
                          <input class="input input-mono uppercase h-8" [placeholder]="i18n.t('adjust.batch')" [(ngModel)]="r.batch" />
                          <input class="input h-8" type="date" [(ngModel)]="r.expires" [attr.aria-label]="i18n.t('adjust.expiry')" />
                        </div>
                      }
                    </td>
                    <td class="num whitespace-nowrap">{{ i18n.number(r.line.quantity, 4) }} <span class="text-faint font-sans text-[12px]">{{ r.line.unit }}</span></td>
                    <td class="num">{{ i18n.number(r.line.receivedBase / r.line.factor, 4) }}</td>
                    <td class="num font-medium">{{ i18n.number(remaining(r), 4) }}</td>
                    <td><input class="input num !text-left" type="number" min="0" step="any" [(ngModel)]="r.now" /></td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
          <div class="flex items-center justify-between gap-3 px-5 py-4 border-t border-line bg-paper/60">
            <span class="hint">{{ i18n.t('receive.tolerance') }}</span>
            <button class="btn btn-primary" (click)="submit()" [disabled]="busy()"><lucide-icon [img]="ReceiveIcon" [size]="16" />{{ i18n.t('receive.submit') }}</button>
          </div>
        </section>
      }
    </div>
  `,
  styles: `
    .flash { animation: flash 900ms ease-out; }
    @keyframes flash { 0% { background: var(--color-signal-soft); } }
  `,
})
export class ReceivePage {
  i18n = inject(I18n);
  private api = inject(Api);
  private router = inject(Router);
  private toasts = inject(Toasts);
  id = input.required<string>();
  BackIcon = ArrowLeft;
  ScanIcon = ScanBarcode;
  ReceiveIcon = PackageCheck;

  po = signal<S['PoDto'] | null>(null);
  rows = signal<Row[]>([]);
  busy = signal(false);
  error = signal('');
  flash = signal('');
  private key = crypto.randomUUID();

  constructor() {
    effect(() => {
      const id = this.id();
      untracked(() => this.load(id));
    });
  }

  private async load(id: string) {
    const p = await this.api.get<S['PoDto']>(`/purchase-orders/${id}`);
    const products = await Promise.all(p.lines.map((l) => this.api.get<S['ProductDto']>(`/products/${l.productId}`)));
    this.po.set(p);
    this.rows.set(
      p.lines.map((line, i) => {
        const prod = products[i];
        const factorOf = (unit: string | null) =>
          !unit || unit === prod.baseUnit ? 1 : (prod.units.find((u) => u.name === unit)?.factor ?? 1);
        return { line, now: 0, batch: '', expires: '', barcodes: prod.barcodes.map((b) => ({ code: b.code, factor: factorOf(b.unit) })) };
      }),
    );
  }

  remaining(r: Row) {
    return Math.max(0, (r.line.orderedBase - r.line.receivedBase) / r.line.factor);
  }

  fillAll() {
    this.rows.update((rows) => rows.map((r) => ({ ...r, now: this.remaining(r) })));
  }

  /** One scan adds what the barcode stands for (a case barcode adds a case), in the line's unit. */
  scanned(box: HTMLInputElement) {
    // Cleared straight away: the scanner types the next code into the same box.
    const code = box.value.trim();
    box.value = '';
    if (!code) return;
    const row = this.rows().find((r) => r.barcodes.some((b) => b.code === code));
    if (!row) {
      this.toasts.show(this.i18n.t('receive.notOnOrder', { code }), 'error');
      return;
    }
    const base = row.barcodes.find((b) => b.code === code)!.factor;
    row.now = Math.round((row.now + base / row.line.factor) * 10000) / 10000;
    this.rows.set([...this.rows()]);
    this.flash.set('');
    setTimeout(() => this.flash.set(row.line.id));
    this.toasts.show(this.i18n.t('receive.scanned', { unit: base === row.line.factor ? row.line.unit : '', sku: row.line.sku }));
  }

  async submit() {
    const lines = this.rows().filter((r) => r.now > 0);
    if (!lines.length) return;
    this.busy.set(true);
    this.error.set('');
    try {
      const res = await this.api.post<{ number: string }>(`/purchase-orders/${this.id()}/receive`, {
        note: null,
        idempotencyKey: this.key,
        lines: lines.map((r) => ({ lineId: r.line.id, quantity: r.now, batchNumber: r.batch || null, expiresOn: r.expires || null })),
      } satisfies S['ReceiveInput']);
      this.toasts.show(this.i18n.t('receive.posted', { number: res.number }));
      this.router.navigate(['/purchasing', this.id()]);
    } catch (e) {
      this.error.set(ApiError.from(e).message);
      this.key = crypto.randomUUID();
    } finally {
      this.busy.set(false);
    }
  }
}
