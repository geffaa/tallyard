import { Component, computed, inject, input } from '@angular/core';
import { I18n } from '../core/i18n';

export interface AuditEntry {
  id: string;
  createdAt: string;
  user: string | null;
  action: 'created' | 'updated' | 'deleted';
  entityType: string;
  entityId: string;
  changes: Record<string, [unknown, unknown]>;
}

/** "salePrice 1.39 → 9.99", one line per changed field. */
@Component({
  selector: 'app-audit-changes',
  template: `
    <dl class="flex flex-col gap-0.5 text-[12.5px]">
      @for (c of shown(); track c.field) {
        <div class="flex flex-wrap items-baseline gap-x-1.5">
          <dt class="text-muted">{{ c.field }}</dt>
          <dd class="font-mono text-[12px] text-ink-2 break-all">
            @if (entry().action === 'updated') {
              <span class="line-through decoration-danger/50 text-muted">{{ c.before }}</span>
              <span class="text-faint mx-1">→</span>
            }
            <span>{{ entry().action === 'deleted' ? c.before : c.after }}</span>
          </dd>
        </div>
      }
      @if (hidden() > 0) {
        <div class="text-faint">{{ i18n.t('audit.more', { n: hidden() }) }}</div>
      }
    </dl>
  `,
})
export class AuditChanges {
  i18n = inject(I18n);
  entry = input.required<AuditEntry>();
  limit = input(20);

  private all = computed(() =>
    Object.entries(this.entry().changes).map(([field, [before, after]]) => ({ field, before: show(before), after: show(after) })),
  );
  shown = computed(() => this.all().slice(0, this.limit()));
  hidden = computed(() => this.all().length - this.shown().length);
}

function show(v: unknown): string {
  if (v === null || v === undefined || v === '') return '∅';
  if (typeof v === 'boolean') return v ? 'yes' : 'no';
  return String(v);
}
