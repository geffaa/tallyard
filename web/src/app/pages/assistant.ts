import { Component, ElementRef, inject, signal, viewChild } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ArrowUp, Database, LucideAngularModule, RotateCcw, Sparkles } from 'lucide-angular';
import { Api, ApiError, S } from '../core/api';
import { I18n } from '../core/i18n';
import { MessageKey } from '../core/messages';

interface Turn {
  role: 'user' | 'assistant';
  content: string;
  tables?: S['DataTable'][];
  tools?: S['ToolUse'][];
  error?: boolean;
}

/**
 * Ask questions about the data. The answer is short; the tables it was based on are shown under
 * it, so every number can be checked. The conversation lives in the page only.
 */
@Component({
  selector: 'app-assistant',
  imports: [FormsModule, LucideAngularModule],
  template: `
    <div class="mx-auto flex h-dvh max-w-[980px] flex-col px-4 sm:px-8 lg:h-dvh">
      <div class="flex items-end justify-between gap-3 pt-6 pb-4">
        <div>
          <div class="eyebrow flex items-center gap-1.5"><lucide-icon [img]="SparkIcon" [size]="13" class="text-brand" />AI</div>
          <h1 class="h1 mt-1">{{ i18n.t('chat.title') }}</h1>
          <p class="mt-1 text-muted max-w-[620px]">{{ i18n.t('chat.subtitle') }}</p>
        </div>
        @if (turns().length) {
          <button class="btn btn-sm" (click)="turns.set([])"><lucide-icon [img]="ResetIcon" [size]="14" />{{ i18n.t('chat.clear') }}</button>
        }
      </div>

      <div #scroller class="flex-1 overflow-y-auto pb-4">
        @if (!turns().length) {
          <div class="grid gap-2 sm:grid-cols-2 pt-4">
            @for (q of examples; track q) {
              <button class="card px-4 py-3 text-left text-[13.5px] hover:border-brand hover:bg-brand-soft/30 transition-colors" (click)="ask(i18n.t(q))">
                {{ i18n.t(q) }}
              </button>
            }
          </div>
        }
        <div class="flex flex-col gap-5">
          @for (t of turns(); track $index) {
            @if (t.role === 'user') {
              <div class="self-end max-w-[80%] rounded-2xl rounded-br-md bg-rail px-4 py-2.5 text-[14px] text-white">{{ t.content }}</div>
            } @else {
              <div class="flex flex-col gap-3 min-w-0">
                <div class="max-w-[90%] text-[14.5px] leading-relaxed whitespace-pre-line" [class.text-danger]="t.error">{{ t.content }}</div>
                @if (t.tools?.length) {
                  <div class="flex flex-wrap items-center gap-1.5 text-[12px] text-muted">
                    <lucide-icon [img]="DbIcon" [size]="13" />{{ i18n.t('chat.used') }}:
                    @for (u of t.tools; track $index) { <span class="code rounded bg-sunken px-1.5 py-0.5" [title]="u.arguments">{{ u.name }}</span> }
                  </div>
                }
                @for (table of t.tables; track $index) {
                  <details class="card overflow-hidden" [open]="table.rows.length <= 12">
                    <summary class="flex cursor-pointer items-center justify-between gap-3 px-4 py-2.5 text-[13px] font-medium">
                      {{ table.title }} <span class="text-muted font-normal">{{ i18n.t('chat.rows', { n: table.rows.length }) }}</span>
                    </summary>
                    <div class="max-h-[340px] overflow-auto border-t border-line">
                      <table class="tbl">
                        <thead><tr>@for (c of table.columns; track c) { <th>{{ c.replaceAll('_', ' ') }}</th> }</tr></thead>
                        <tbody>
                          @for (r of table.rows; track $index) {
                            <tr>@for (v of r; track $index) { <td [class.num]="isNumber(v)">{{ show(v) }}</td> }</tr>
                          }
                        </tbody>
                      </table>
                    </div>
                  </details>
                }
              </div>
            }
          }
          @if (busy()) {
            <div class="flex items-center gap-2 text-[13.5px] text-muted">
              <span class="dots"><i></i><i></i><i></i></span>{{ i18n.t('chat.thinking') }}
            </div>
          }
        </div>
      </div>

      <form class="sticky bottom-0 pb-5 pt-2 bg-paper" (ngSubmit)="ask(draft)">
        <div class="flex items-end gap-2 rounded-2xl border border-line-strong bg-surface p-2 focus-within:border-brand focus-within:ring-3 focus-within:ring-brand/15">
          <textarea
            class="flex-1 resize-none bg-transparent px-2 py-1.5 text-[14.5px] outline-none placeholder:text-faint max-h-40"
            rows="1"
            name="q"
            [(ngModel)]="draft"
            [placeholder]="i18n.t('chat.placeholder')"
            (keydown.enter)="enter($event)"
          ></textarea>
          <button class="btn btn-primary !h-9 !w-9 !p-0 rounded-xl" [disabled]="busy() || !draft.trim()" [attr.aria-label]="i18n.t('chat.send')">
            <lucide-icon [img]="SendIcon" [size]="17" />
          </button>
        </div>
      </form>
    </div>
  `,
  styles: `
    .dots { display: inline-flex; gap: 3px; }
    .dots i { width: 5px; height: 5px; border-radius: 50%; background: var(--color-brand); animation: b 1s infinite ease-in-out; }
    .dots i:nth-child(2) { animation-delay: .15s; } .dots i:nth-child(3) { animation-delay: .3s; }
    @keyframes b { 0%, 80%, 100% { opacity: .25; transform: translateY(0); } 40% { opacity: 1; transform: translateY(-3px); } }
  `,
})
export class AssistantPage {
  i18n = inject(I18n);
  private api = inject(Api);
  scroller = viewChild.required<ElementRef<HTMLElement>>('scroller');
  SparkIcon = Sparkles;
  SendIcon = ArrowUp;
  DbIcon = Database;
  ResetIcon = RotateCcw;
  examples: MessageKey[] = ['chat.ex1', 'chat.ex2', 'chat.ex3', 'chat.ex4', 'chat.ex5', 'chat.ex6'];
  turns = signal<Turn[]>([]);
  busy = signal(false);
  draft = '';

  async ask(question: string) {
    const q = question.trim();
    if (!q || this.busy()) return;
    this.draft = '';
    this.turns.update((t) => [...t, { role: 'user', content: q }]);
    this.busy.set(true);
    this.scroll();
    try {
      const history = this.turns().filter((t) => !t.error).map((t) => ({ role: t.role, content: t.content }));
      const res = await this.api.post<S['ChatAnswer']>('/ai/chat', { messages: history });
      this.turns.update((t) => [...t, { role: 'assistant', content: res.answer, tables: res.tables, tools: res.tools }]);
    } catch (e) {
      this.turns.update((t) => [...t, { role: 'assistant', content: ApiError.from(e).message, error: true }]);
    } finally {
      this.busy.set(false);
      this.scroll();
    }
  }

  /** Enter sends; Shift+Enter makes a new line. */
  enter(e: Event) {
    if ((e as KeyboardEvent).shiftKey) return;
    e.preventDefault();
    this.ask(this.draft);
  }

  isNumber(v: unknown) {
    return typeof v === 'number';
  }

  show(v: unknown) {
    if (v === null || v === undefined) return '—';
    if (typeof v === 'number') return this.i18n.number(v, 2);
    return String(v);
  }

  private scroll() {
    setTimeout(() => this.scroller().nativeElement.scrollTo({ top: 1e9, behavior: 'smooth' }));
  }
}
