import { Component, computed, inject, signal } from '@angular/core';
import { CircleCheck, Download, FileSpreadsheet, LucideAngularModule, TriangleAlert, Upload } from 'lucide-angular';
import { Api, ApiError, S } from '../core/api';
import { Auth, Permission } from '../core/auth';
import { I18n } from '../core/i18n';
import { Toasts } from '../core/toast';

const KINDS: { kind: string; perm: Permission }[] = [
  { kind: 'products', perm: 'EditCatalog' },
  { kind: 'customers', perm: 'EditCustomers' },
  { kind: 'suppliers', perm: 'EditSuppliers' },
  { kind: 'opening_stock', perm: 'AdjustStock' },
];

/** Choose a type, download its template, check a file, then import it when it has no errors. */
@Component({
  selector: 'app-import',
  imports: [LucideAngularModule],
  template: `
    <div class="page-narrow !max-w-[1040px]">
      <div class="page-head">
        <div>
          <div class="eyebrow">{{ i18n.t('nav.masterData') }}</div>
          <h1 class="h1 mt-1">{{ i18n.t('import.title') }}</h1>
          <p class="mt-1 text-muted max-w-[640px]">{{ i18n.t('import.subtitle') }}</p>
        </div>
      </div>

      <div class="grid grid-cols-2 sm:grid-cols-4 gap-2 mb-5">
        @for (k of kinds(); track k.kind) {
          <button class="card px-4 py-3 text-left transition-colors" [class]="kind() === k.kind ? '!border-brand bg-brand-soft/40' : 'hover:bg-paper'"
                  (click)="pickKind(k.kind)">
            <lucide-icon [img]="SheetIcon" [size]="18" class="text-brand" />
            <div class="mt-1 font-medium text-[14px]">{{ i18n.t($any('import.kind.' + k.kind)) }}</div>
          </button>
        }
      </div>

      <section class="card p-5 mb-5 flex flex-wrap items-center gap-3">
        <a class="btn" [href]="'/api/import/' + kind() + '/template'"><lucide-icon [img]="DownloadIcon" [size]="15" />{{ i18n.t('import.template') }}</a>
        <label class="btn cursor-pointer">
          <lucide-icon [img]="UploadIcon" [size]="15" />{{ file()?.name ?? i18n.t('import.choose') }}
          <input type="file" class="hidden" accept=".xlsx,application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" (change)="choose($event)" />
        </label>
        <span class="flex-1"></span>
        <button class="btn" [disabled]="!file() || busy()" (click)="send(false)">{{ i18n.t('import.preview') }}</button>
        <button class="btn btn-primary" [disabled]="!ready() || busy()" (click)="send(true)">{{ i18n.t('import.commit', { n: result()?.valid ?? 0 }) }}</button>
      </section>

      @if (result(); as r) {
        <div class="mb-3 flex flex-wrap items-center gap-3 text-[13.5px]">
          <span class="text-muted">{{ i18n.t('import.rows', { n: r.rows, valid: r.valid }) }}</span>
          @if (r.committed) {
            <span class="inline-flex items-center gap-1.5 text-ok font-medium"><lucide-icon [img]="OkIcon" [size]="15" />{{ i18n.t('import.done', { created: r.created, updated: r.updated }) }}</span>
          } @else if (r.errors.length) {
            <span class="inline-flex items-center gap-1.5 text-danger font-medium"><lucide-icon [img]="WarnIcon" [size]="15" />{{ i18n.t('import.errors') }}</span>
          } @else {
            <span class="inline-flex items-center gap-1.5 text-ok font-medium"><lucide-icon [img]="OkIcon" [size]="15" />{{ i18n.t('import.ok') }}</span>
          }
        </div>
        @if (r.errors.length) {
          <section class="card overflow-hidden mb-5">
            <table class="tbl">
              <thead><tr><th class="w-20">{{ i18n.t('import.row') }}</th><th class="w-44">{{ i18n.t('import.column') }}</th><th>{{ i18n.t('import.problem') }}</th></tr></thead>
              <tbody>
                @for (e of r.errors; track $index) {
                  <tr><td class="num !text-left">{{ e.row }}</td><td class="code">{{ e.column }}</td><td class="text-danger">{{ e.message }}</td></tr>
                }
              </tbody>
            </table>
          </section>
        }
        <section class="card overflow-auto">
          <table class="tbl">
            <thead><tr><th class="w-14">#</th>@for (c of r.columns; track c) { <th>{{ c }}</th> }</tr></thead>
            <tbody>
              @for (row of r.preview; track $index; let i = $index) {
                <tr [class.bg-danger-soft]="badRows().has(i + 2)">
                  <td class="text-faint tabular-nums">{{ i + 2 }}</td>
                  @for (v of row; track $index) { <td class="text-[13px] whitespace-nowrap">{{ v ?? '' }}</td> }
                </tr>
              }
            </tbody>
          </table>
        </section>
      }
    </div>
  `,
})
export class ImportPage {
  i18n = inject(I18n);
  private auth = inject(Auth);
  private api = inject(Api);
  private toasts = inject(Toasts);
  SheetIcon = FileSpreadsheet;
  DownloadIcon = Download;
  UploadIcon = Upload;
  OkIcon = CircleCheck;
  WarnIcon = TriangleAlert;

  kinds = computed(() => KINDS.filter((k) => this.auth.can(k.perm)));
  kind = signal(KINDS.find((k) => this.auth.can(k.perm))?.kind ?? 'products');
  file = signal<File | null>(null);
  result = signal<S['ImportResult'] | null>(null);
  busy = signal(false);
  ready = computed(() => !!this.result() && !this.result()!.committed && this.result()!.errors.length === 0 && this.result()!.rows > 0);
  badRows = computed(() => new Set(this.result()?.errors.map((e) => e.row) ?? []));

  pickKind(k: string) {
    this.kind.set(k);
    this.result.set(null);
  }

  choose(e: Event) {
    this.file.set((e.target as HTMLInputElement).files?.[0] ?? null);
    this.result.set(null);
    (e.target as HTMLInputElement).value = '';
  }

  async send(commit: boolean) {
    const f = this.file();
    if (!f) return;
    this.busy.set(true);
    try {
      const form = new FormData();
      form.append('file', f);
      this.result.set(await this.api.post<S['ImportResult']>(`/import/${this.kind()}${commit ? '?commit=true' : ''}`, form));
    } catch (e) {
      this.toasts.show(ApiError.from(e).message, 'error');
    } finally {
      this.busy.set(false);
    }
  }
}
