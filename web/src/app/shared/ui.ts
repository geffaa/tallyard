import { Component, computed, inject, input, model, output } from '@angular/core';
import { AbstractControl, FormGroup } from '@angular/forms';
import { ChevronLeft, ChevronRight, LucideAngularModule, X } from 'lucide-angular';
import { ApiError } from '../core/api';
import { I18n } from '../core/i18n';

/** "1–25 of 400" with previous/next. */
@Component({
  selector: 'app-pager',
  imports: [LucideAngularModule],
  template: `
    @if (total() > 0) {
      <div class="flex items-center justify-between gap-3 px-4 py-3 border-t border-line text-[13px] text-muted">
        <span class="tabular-nums">{{ i18n.t('common.range', { from: from(), to: to(), total: i18n.number(total()) }) }}</span>
        <div class="flex gap-1.5">
          <button class="btn btn-sm" [disabled]="page() <= 1" (click)="page.set(page() - 1)" [attr.aria-label]="i18n.t('common.prev')">
            <lucide-icon [img]="Prev" [size]="15" />
          </button>
          <button class="btn btn-sm" [disabled]="to() >= total()" (click)="page.set(page() + 1)" [attr.aria-label]="i18n.t('common.next')">
            <lucide-icon [img]="Next" [size]="15" />
          </button>
        </div>
      </div>
    }
  `,
})
export class Pager {
  i18n = inject(I18n);
  page = model.required<number>();
  size = input.required<number>();
  total = input.required<number>();
  from = computed(() => (this.page() - 1) * this.size() + 1);
  to = computed(() => Math.min(this.page() * this.size(), this.total()));
  Prev = ChevronLeft;
  Next = ChevronRight;
}

/** A panel that slides in from the right, for create/edit forms that don't need a page. */
@Component({
  selector: 'app-sheet',
  imports: [LucideAngularModule],
  template: `
    <div class="fixed inset-0 z-40 bg-ink/30 sheet-fade" (click)="closed.emit()"></div>
    <aside
      class="fixed inset-y-0 right-0 z-50 flex w-full max-w-[520px] flex-col bg-surface shadow-2xl shadow-ink/20 sheet-in"
      role="dialog"
      aria-modal="true"
    >
      <header class="flex items-center justify-between gap-3 px-6 h-16 border-b border-line">
        <h2 class="text-[16px] font-semibold">{{ title() }}</h2>
        <button class="btn btn-ghost btn-sm" (click)="closed.emit()" aria-label="Close"><lucide-icon [img]="XIcon" [size]="16" /></button>
      </header>
      <div class="flex-1 overflow-y-auto px-6 py-5"><ng-content /></div>
      <footer class="flex justify-end gap-2 px-6 py-4 border-t border-line bg-paper/60"><ng-content select="[actions]" /></footer>
    </aside>
  `,
  styles: `
    .sheet-in { animation: slide 200ms cubic-bezier(.2,.8,.2,1); }
    .sheet-fade { animation: fade 200ms ease-out; }
    @keyframes slide { from { transform: translateX(24px); opacity: 0; } }
    @keyframes fade { from { opacity: 0; } }
  `,
  host: { '(document:keydown.escape)': 'closed.emit()' },
})
export class Sheet {
  title = input.required<string>();
  closed = output();
  XIcon = X;
}

/** Puts the API's field errors onto the matching form controls ("units[0].name" → units.0.name). */
export function applyServerErrors(form: FormGroup, e: ApiError): string[] {
  const unplaced: string[] = [];
  for (const [path, messages] of Object.entries(e.fields)) {
    const control = form.get(path.replace(/\[(\d+)\]/g, '.$1'));
    if (control) {
      control.setErrors({ server: messages[0] });
      control.markAsTouched();
    } else unplaced.push(...messages);
  }
  return unplaced;
}

/** The message to show under a control, if it has been touched and is invalid. */
export function fieldError(c: AbstractControl | null, i18n: I18n): string | null {
  if (!c || !c.touched || !c.errors) return null;
  const e = c.errors;
  if (e['server']) return e['server'];
  if (e['required']) return i18n.t('form.required');
  if (e['email']) return i18n.t('form.email');
  if (e['min']) return i18n.t('form.min', { n: e['min'].min });
  if (e['max']) return i18n.t('form.max', { n: e['max'].max });
  if (e['minlength']) return i18n.t('form.minLength', { n: e['minlength'].requiredLength });
  return i18n.t('form.invalid');
}
