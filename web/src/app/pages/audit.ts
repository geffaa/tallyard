import { Component, effect, inject, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { Api, latest, Page } from '../core/api';
import { I18n } from '../core/i18n';
import { AuditChanges, AuditEntry } from '../shared/audit-changes';
import { Pager } from '../shared/ui';

const TYPES = ['Product', 'Warehouse', 'Supplier', 'Customer', 'User', 'Category'];
const LINKS: Record<string, string> = { Product: '/products' };

@Component({
  selector: 'app-audit',
  imports: [FormsModule, RouterLink, AuditChanges, Pager],
  template: `
    <div class="page">
      <div class="page-head">
        <div>
          <div class="eyebrow">{{ i18n.t('nav.admin') }}</div>
          <h1 class="h1 mt-1">{{ i18n.t('audit.title') }}</h1>
          <p class="mt-1 text-muted max-w-[620px]">{{ i18n.t('audit.subtitle') }}</p>
        </div>
        <select class="input w-auto min-w-[180px]" [ngModel]="type()" (ngModelChange)="type.set($event); page.set(1)">
          <option value="">{{ i18n.t('audit.allTypes') }}</option>
          @for (t of types; track t) { <option [value]="t">{{ t }}</option> }
        </select>
      </div>

      <div class="card overflow-hidden">
        <div class="overflow-x-auto">
          <table class="tbl">
            <thead>
              <tr>
                <th>{{ i18n.t('audit.when') }}</th>
                <th>{{ i18n.t('audit.who') }}</th>
                <th>{{ i18n.t('audit.what') }}</th>
                <th>{{ i18n.t('audit.changes') }}</th>
              </tr>
            </thead>
            <tbody>
              @for (e of data()?.items ?? []; track e.id) {
                <tr class="align-top">
                  <td class="whitespace-nowrap text-[13px]" [title]="i18n.dateTime(e.createdAt)">{{ i18n.relative(e.createdAt) }}</td>
                  <td class="whitespace-nowrap">{{ e.user ?? i18n.t('audit.system') }}</td>
                  <td class="whitespace-nowrap">
                    <span class="badge mr-1.5" [class]="e.action === 'created' ? 'badge-brand' : e.action === 'deleted' ? 'badge-danger' : 'badge-signal'">
                      {{ i18n.t($any('audit.' + e.action)) }}
                    </span>
                    @if (link(e); as l) {
                      <a [routerLink]="l" class="font-medium hover:underline">{{ e.entityType }}</a>
                    } @else {
                      <span class="font-medium">{{ e.entityType }}</span>
                    }
                    <div class="code text-faint mt-0.5">{{ e.entityId.slice(-12) }}</div>
                  </td>
                  <td class="min-w-[280px]"><app-audit-changes [entry]="e" [limit]="6" /></td>
                </tr>
              } @empty {
                <tr><td colspan="4" class="!py-14 text-center text-muted">{{ i18n.t('audit.empty') }}</td></tr>
              }
            </tbody>
          </table>
        </div>
        <app-pager [(page)]="page" [size]="size" [total]="data()?.total ?? 0" />
      </div>
    </div>
  `,
})
export class AuditPage {
  i18n = inject(I18n);
  private api = inject(Api);
  types = TYPES;
  size = 50;
  type = signal('');
  page = signal(1);
  data = signal<Page<AuditEntry> | null>(null);

  private fetch = latest((q: object) => this.api.get<Page<AuditEntry>>('/audit', q as never), (d) => this.data.set(d));

  constructor() {
    effect(() => {
      const query = { entityType: this.type(), page: this.page(), pageSize: this.size };
      untracked(() => this.fetch(query));
    });
  }

  link(e: AuditEntry): unknown[] | null {
    return LINKS[e.entityType] && e.action !== 'deleted' ? [LINKS[e.entityType], e.entityId] : null;
  }
}
