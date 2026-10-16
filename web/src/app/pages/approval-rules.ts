import { Component, inject, OnInit, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { LucideAngularModule, Plus, Trash2 } from 'lucide-angular';
import { Api, ApiError, S } from '../core/api';
import { Auth } from '../core/auth';
import { I18n } from '../core/i18n';
import { Toasts } from '../core/toast';

const ROLES = ['Finance', 'Owner', 'Purchasing'];

@Component({
  selector: 'app-approval-rules',
  imports: [FormsModule, LucideAngularModule],
  template: `
    <div class="page-narrow">
      <div class="page-head">
        <div>
          <div class="eyebrow">{{ i18n.t('nav.admin') }}</div>
          <h1 class="h1 mt-1">{{ i18n.t('rules.title') }}</h1>
          <p class="mt-1 text-muted max-w-[560px]">{{ i18n.t('rules.subtitle') }}</p>
        </div>
        <button class="btn btn-primary" (click)="save()" [disabled]="busy()">{{ i18n.t('rules.save') }}</button>
      </div>
      <section class="card">
        <ol class="divide-y divide-line">
          @for (r of rules(); track $index; let i = $index) {
            <li class="flex flex-wrap items-center gap-3 px-5 py-4">
              <span class="grid place-items-center size-8 rounded-full bg-brand-soft text-brand-ink font-semibold text-[13px]">{{ i + 1 }}</span>
              <label class="field">
                <span class="label">{{ i18n.t('rules.from') }}</span>
                <div class="relative">
                  <span class="absolute left-3 top-1/2 -translate-y-1/2 text-faint text-[13px]">{{ auth.currency() }}</span>
                  <input class="input num !text-left pl-12 w-44" type="number" min="0" step="100" [(ngModel)]="r.minTotal" />
                </div>
              </label>
              <label class="field">
                <span class="label">{{ i18n.t('rules.role') }}</span>
                <select class="input w-44" [(ngModel)]="r.role">
                  @for (role of roles; track role) { <option [value]="role">{{ i18n.t($any('role.' + role)) }}</option> }
                </select>
              </label>
              <button class="btn btn-ghost btn-sm ml-auto self-end h-9" (click)="remove(i)" [attr.aria-label]="i18n.t('common.remove')">
                <lucide-icon [img]="TrashIcon" [size]="15" />
              </button>
            </li>
          } @empty {
            <li class="px-5 py-6 text-muted text-[13.5px]">{{ i18n.t('rules.none') }}</li>
          }
        </ol>
        <div class="px-5 py-3 border-t border-line">
          <button class="btn btn-sm" (click)="add()"><lucide-icon [img]="PlusIcon" [size]="14" />{{ i18n.t('rules.add') }}</button>
        </div>
      </section>
    </div>
  `,
})
export class ApprovalRulesPage implements OnInit {
  i18n = inject(I18n);
  auth = inject(Auth);
  private api = inject(Api);
  private toasts = inject(Toasts);
  PlusIcon = Plus;
  TrashIcon = Trash2;
  roles = ROLES;
  rules = signal<S['ApprovalRuleDto'][]>([]);
  busy = signal(false);

  async ngOnInit() {
    this.rules.set(await this.api.get<S['ApprovalRuleDto'][]>('/approval-rules'));
  }

  add() {
    const last = this.rules().at(-1);
    this.rules.update((r) => [...r, { level: r.length + 1, minTotal: (last?.minTotal ?? 0) + 5000, role: 'Owner' }]);
  }

  remove(i: number) {
    this.rules.update((r) => r.filter((_, j) => j !== i));
  }

  async save() {
    this.busy.set(true);
    try {
      await this.api.put('/approval-rules', this.rules());
      this.toasts.show(this.i18n.t('rules.saved'));
      await this.ngOnInit();
    } catch (e) {
      this.toasts.show(ApiError.from(e).message, 'error');
    } finally {
      this.busy.set(false);
    }
  }
}
