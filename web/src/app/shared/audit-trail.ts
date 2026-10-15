import { Component, effect, inject, input, signal } from '@angular/core';
import { Api, Page } from '../core/api';
import { I18n } from '../core/i18n';
import { AuditChanges, AuditEntry } from './audit-changes';

/** The change history of one record, newest first. */
@Component({
  selector: 'app-audit-trail',
  imports: [AuditChanges],
  template: `
    <section class="card">
      <div class="card-head"><h2 class="card-title">{{ i18n.t('product.history') }}</h2></div>
      <ol class="px-5 py-4 flex flex-col gap-4">
        @for (e of entries(); track e.id) {
          <li class="relative pl-5">
            <span class="absolute left-0 top-1.5 size-2 rounded-full" [class]="e.action === 'created' ? 'bg-brand' : 'bg-signal'"></span>
            <div class="text-[13px]">
              <span class="font-medium">{{ e.user ?? i18n.t('audit.system') }}</span>
              <span class="text-muted"> {{ i18n.t($any('audit.' + e.action)) }} · {{ i18n.relative(e.createdAt) }}</span>
            </div>
            <app-audit-changes [entry]="e" [limit]="e.action === 'created' ? 4 : 20" class="mt-1 block" />
          </li>
        } @empty {
          <li class="text-muted text-[13px]">{{ i18n.t('audit.empty') }}</li>
        }
      </ol>
    </section>
  `,
})
export class AuditTrail {
  i18n = inject(I18n);
  private api = inject(Api);
  entityId = input.required<string>();
  /** Changes when the record is saved, so the list reloads. */
  refresh = input<unknown>();
  entries = signal<AuditEntry[]>([]);

  constructor() {
    effect(() => {
      this.refresh();
      this.api
        .get<Page<AuditEntry>>('/audit', { entityId: this.entityId(), pageSize: 20 })
        .then((p) => this.entries.set(p.items));
    });
  }
}
