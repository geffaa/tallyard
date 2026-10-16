import { Component, effect, inject, signal, untracked } from '@angular/core';
import { RouterLink } from '@angular/router';
import { LucideAngularModule, Plus } from 'lucide-angular';
import { Api, Page, S } from '../../core/api';
import { Auth } from '../../core/auth';
import { I18n } from '../../core/i18n';
import { Pager } from '../../shared/ui';

@Component({
  selector: 'app-adjustments',
  imports: [RouterLink, LucideAngularModule, Pager],
  template: `
    <div class="page">
      <div class="page-head">
        <div>
          <div class="eyebrow">{{ i18n.t('nav.inventory') }}</div>
          <h1 class="h1 mt-1">{{ i18n.t('adjustments.title') }}</h1>
          <p class="mt-1 text-muted">{{ i18n.t('adjustments.subtitle') }}</p>
        </div>
        @if (auth.can('AdjustStock')) {
          <a class="btn btn-primary" routerLink="/stock/adjust"><lucide-icon [img]="PlusIcon" [size]="16" />{{ i18n.t('adjustments.new') }}</a>
        }
      </div>
      <div class="card overflow-hidden">
        <div class="overflow-x-auto">
          <table class="tbl">
            <thead>
              <tr>
                <th>{{ i18n.t('stock.document') }}</th>
                <th>{{ i18n.t('adjust.reason') }}</th>
                <th>{{ i18n.t('adjust.warehouse') }}</th>
                <th class="text-right">{{ i18n.t('adjust.lines') }}</th>
                <th class="hidden md:table-cell">{{ i18n.t('stock.by') }}</th>
                <th>{{ i18n.t('stock.date') }}</th>
              </tr>
            </thead>
            <tbody>
              @for (o of data()?.items ?? []; track o.id) {
                <tr>
                  <td class="code">{{ o.number }}</td>
                  <td>
                    {{ o.reason ? i18n.t($any('reason.' + o.reason)) : '' }}
                    @if (o.note) { <div class="text-[12.5px] text-muted truncate max-w-[320px]">{{ o.note }}</div> }
                  </td>
                  <td class="code">{{ o.warehouse }}</td>
                  <td class="num">{{ i18n.number(o.lines) }}</td>
                  <td class="hidden md:table-cell text-ink-2">{{ o.user ?? i18n.t('audit.system') }}</td>
                  <td class="whitespace-nowrap text-[13px]" [title]="i18n.dateTime(o.createdAt)">{{ i18n.relative(o.createdAt) }}</td>
                </tr>
              } @empty {
                <tr><td colspan="6" class="!py-14 text-center text-muted">{{ i18n.t('adjustments.empty') }}</td></tr>
              }
            </tbody>
          </table>
        </div>
        <app-pager [(page)]="page" [size]="25" [total]="data()?.total ?? 0" />
      </div>
    </div>
  `,
})
export class AdjustmentsPage {
  i18n = inject(I18n);
  auth = inject(Auth);
  private api = inject(Api);
  PlusIcon = Plus;
  page = signal(1);
  data = signal<Page<S['OperationDto']> | null>(null);

  constructor() {
    effect(() => {
      const page = this.page();
      untracked(() => this.api.get<Page<S['OperationDto']>>('/stock/adjustments', { page }).then((d) => this.data.set(d)));
    });
  }
}
