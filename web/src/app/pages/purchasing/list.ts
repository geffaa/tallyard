import { Component, effect, inject, OnInit, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { LucideAngularModule, Plus, Search } from 'lucide-angular';
import { Api, Page, S } from '../../core/api';
import { Auth } from '../../core/auth';
import { I18n } from '../../core/i18n';
import { MessageKey } from '../../core/messages';
import { Pager } from '../../shared/ui';

export const PO_BADGE: Record<string, string> = {
  Draft: 'badge-muted',
  PendingApproval: 'badge-signal',
  Approved: 'badge-brand',
  Sent: 'badge-brand',
  PartiallyReceived: 'badge-signal',
  Received: 'badge-ok',
  Closed: 'badge-muted',
  Cancelled: 'badge-muted',
};

type Tab = 'mine' | 'draft' | 'approval' | 'open' | 'done' | '';

@Component({
  selector: 'app-po-list',
  imports: [FormsModule, RouterLink, LucideAngularModule, Pager],
  template: `
    <div class="page">
      <div class="page-head">
        <div>
          <div class="eyebrow">{{ i18n.t('nav.purchasing') }}</div>
          <h1 class="h1 mt-1">{{ i18n.t('po.title') }}</h1>
          <p class="mt-1 text-muted">{{ i18n.t('po.subtitle') }}</p>
        </div>
        @if (auth.can('EditPurchaseOrders')) {
          <a class="btn btn-primary" routerLink="/purchasing/new"><lucide-icon [img]="PlusIcon" [size]="16" />{{ i18n.t('po.new') }}</a>
        }
      </div>

      <div class="card overflow-hidden">
        <div class="flex flex-wrap items-center gap-2.5 p-3 border-b border-line">
          <div class="flex flex-wrap rounded-lg border border-line-strong p-0.5 bg-sunken/50">
            @for (t of tabs; track t.value) {
              <button
                class="relative h-8 px-3 rounded-md text-[13px] font-medium"
                [class]="tab() === t.value ? 'bg-surface text-ink shadow-sm' : 'text-muted hover:text-ink'"
                (click)="tab.set(t.value); page.set(1)"
              >
                {{ i18n.t(t.label) }}
                @if (t.value === 'mine' && mineCount() > 0) {
                  <span class="ml-1 inline-grid place-items-center min-w-5 h-5 px-1 rounded-full bg-signal text-rail text-[11px] font-bold">{{ mineCount() }}</span>
                }
              </button>
            }
          </div>
          <label class="relative flex-1 min-w-[200px]">
            <span class="pointer-events-none absolute inset-y-0 left-3 flex items-center text-faint"><lucide-icon [img]="SearchIcon" [size]="16" /></span>
            <input class="input pl-9" [placeholder]="i18n.t('po.search')" [ngModel]="q()" (ngModelChange)="search($event)" />
          </label>
        </div>
        <div class="overflow-x-auto">
          <table class="tbl">
            <thead>
              <tr>
                <th>{{ i18n.t('po.number') }}</th>
                <th>{{ i18n.t('po.supplier') }}</th>
                <th>{{ i18n.t('po.deliverTo') }}</th>
                <th>{{ i18n.t('common.status') }}</th>
                <th class="hidden md:table-cell">{{ i18n.t('po.expected') }}</th>
                <th class="hidden lg:table-cell w-40">{{ i18n.t('po.received') }}</th>
                @if (auth.can('ViewCosts')) { <th class="text-right">{{ i18n.t('po.total') }}</th> }
              </tr>
            </thead>
            <tbody>
              @for (p of data()?.items ?? []; track p.id) {
                <tr class="clickable" (click)="router.navigate(['/purchasing', p.id])">
                  <td class="code whitespace-nowrap">
                    @if (p.awaitingMe) { <span class="inline-block size-2 rounded-full bg-signal mr-1.5" [title]="i18n.t('po.awaitingYou')"></span> }
                    {{ p.number }}
                  </td>
                  <td class="font-medium">{{ p.supplier }}</td>
                  <td class="code">{{ p.warehouse }}</td>
                  <td><span class="badge" [class]="badge[p.status]">{{ i18n.t($any('po.status.' + p.status)) }}</span></td>
                  <td class="hidden md:table-cell tabular-nums text-[13px]" [class.text-danger]="late(p)">{{ p.expectedOn ?? '—' }}</td>
                  <td class="hidden lg:table-cell">
                    @if (p.receivedPercent > 0) {
                      <div class="flex items-center gap-2">
                        <div class="h-1.5 flex-1 rounded-full bg-sunken overflow-hidden"><div class="h-full bg-ok" [style.width.%]="p.receivedPercent"></div></div>
                        <span class="text-[12px] text-muted tabular-nums w-9 text-right">{{ p.receivedPercent }}%</span>
                      </div>
                    }
                  </td>
                  @if (auth.can('ViewCosts')) { <td class="num">{{ p.total != null ? i18n.money(p.total, auth.currency()) : '' }}</td> }
                </tr>
              } @empty {
                <tr><td colspan="7" class="!py-14 text-center text-muted">{{ i18n.t('po.empty') }}</td></tr>
              }
            </tbody>
          </table>
        </div>
        <app-pager [(page)]="page" [size]="25" [total]="data()?.total ?? 0" />
      </div>
    </div>
  `,
})
export class PoListPage implements OnInit {
  i18n = inject(I18n);
  auth = inject(Auth);
  router = inject(Router);
  private api = inject(Api);
  PlusIcon = Plus;
  SearchIcon = Search;
  badge = PO_BADGE;
  tabs: { value: Tab; label: MessageKey }[] = [
    { value: 'mine', label: 'po.tab.mine' },
    { value: 'draft', label: 'po.tab.draft' },
    { value: 'approval', label: 'po.tab.approval' },
    { value: 'open', label: 'po.tab.open' },
    { value: 'done', label: 'po.tab.done' },
    { value: '', label: 'po.tab.all' },
  ];
  tab = signal<Tab>('open');
  q = signal('');
  page = signal(1);
  data = signal<Page<S['PoListItem']> | null>(null);
  mineCount = signal(0);
  private timer?: ReturnType<typeof setTimeout>;

  constructor() {
    effect(() => {
      const query = { status: this.tab(), q: this.q(), page: this.page() };
      untracked(() => this.api.get<Page<S['PoListItem']>>('/purchase-orders', query).then((d) => this.data.set(d)));
    });
  }

  async ngOnInit() {
    // Open on "needs my approval" when something is waiting for this person.
    const mine = await this.api.get<Page<S['PoListItem']>>('/purchase-orders', { status: 'mine' });
    this.mineCount.set(mine.total);
    if (mine.total > 0) this.tab.set('mine');
  }

  late(p: S['PoListItem']) {
    return !!p.expectedOn && ['Approved', 'Sent', 'PartiallyReceived'].includes(p.status) && new Date(p.expectedOn) < new Date();
  }

  search(v: string) {
    clearTimeout(this.timer);
    this.timer = setTimeout(() => (this.q.set(v), this.page.set(1)), 200);
  }
}
