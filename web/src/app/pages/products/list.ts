import { Component, effect, inject, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { LucideAngularModule, Plus, Search } from 'lucide-angular';
import { Api, Page, S } from '../../core/api';
import { Auth } from '../../core/auth';
import { I18n } from '../../core/i18n';
import { Pager } from '../../shared/ui';

@Component({
  selector: 'app-products',
  imports: [FormsModule, RouterLink, LucideAngularModule, Pager],
  template: `
    <div class="page">
      <div class="page-head">
        <div>
          <div class="eyebrow">{{ i18n.t('nav.masterData') }}</div>
          <h1 class="h1 mt-1">{{ i18n.t('products.title') }}</h1>
          @if (data(); as d) {
            <p class="mt-1 text-muted">{{ i18n.t('products.subtitle', { n: i18n.number(totalAll()) }) }}</p>
          }
        </div>
        @if (auth.can('EditCatalog')) {
          <a class="btn btn-primary" routerLink="/products/new"><lucide-icon [img]="PlusIcon" [size]="16" />{{ i18n.t('products.new') }}</a>
        }
      </div>

      <div class="card overflow-hidden">
        <div class="flex flex-wrap gap-2.5 p-3 border-b border-line">
          <label class="relative flex-1 min-w-[220px]">
            <span class="pointer-events-none absolute inset-y-0 left-3 flex items-center text-faint"><lucide-icon [img]="SearchIcon" [size]="16" /></span>
            <input
              class="input pl-9"
              [placeholder]="i18n.t('products.search')"
              [ngModel]="q()"
              (ngModelChange)="search($event)"
              autofocus
            />
          </label>
          <select class="input w-auto min-w-[180px]" [ngModel]="category()" (ngModelChange)="category.set($event); page.set(1)">
            <option value="">{{ i18n.t('products.allCategories') }}</option>
            @for (c of categories(); track c.id) {
              <option [value]="c.id">{{ c.name }}</option>
            }
          </select>
        </div>

        <div class="overflow-x-auto">
          <table class="tbl">
            <thead>
              <tr>
                <th>{{ i18n.t('products.sku') }}</th>
                <th>{{ i18n.t('common.name') }}</th>
                <th class="hidden md:table-cell">{{ i18n.t('products.category') }}</th>
                <th class="hidden sm:table-cell">{{ i18n.t('products.unit') }}</th>
                <th class="text-right">{{ i18n.t('products.price') }}</th>
                <th class="hidden lg:table-cell">{{ i18n.t('products.batches') }}</th>
              </tr>
            </thead>
            <tbody [class.opacity-60]="loading()">
              @for (p of data()?.items ?? []; track p.id) {
                <tr class="clickable" (click)="router.navigate(['/products', p.id])">
                  <td class="code whitespace-nowrap">{{ p.sku }}</td>
                  <td>
                    <span class="font-medium">{{ p.name }}</span>
                    @if (!p.isActive) {
                      <span class="badge badge-muted ml-2">{{ i18n.t('common.inactive') }}</span>
                    }
                  </td>
                  <td class="hidden md:table-cell text-ink-2">{{ p.category ?? '—' }}</td>
                  <td class="hidden sm:table-cell text-ink-2">{{ p.baseUnit }}</td>
                  <td class="num">{{ i18n.money(p.salePrice, auth.currency()) }}</td>
                  <td class="hidden lg:table-cell">
                    @if (p.trackBatches) {
                      <span class="badge badge-signal">{{ i18n.t('products.tracked') }}</span>
                    }
                  </td>
                </tr>
              } @empty {
                @if (!loading()) {
                  <tr>
                    <td colspan="6" class="!py-14 text-center text-muted">
                      {{ q() ? i18n.t('products.empty', { q: q() }) : i18n.t('products.emptyAll') }}
                    </td>
                  </tr>
                }
              }
            </tbody>
          </table>
        </div>
        <app-pager [(page)]="page" [size]="size" [total]="data()?.total ?? 0" />
      </div>
    </div>
  `,
})
export class ProductsPage {
  i18n = inject(I18n);
  auth = inject(Auth);
  router = inject(Router);
  private api = inject(Api);

  PlusIcon = Plus;
  SearchIcon = Search;
  size = 25;

  q = signal('');
  category = signal('');
  page = signal(1);
  data = signal<Page<S['ProductListItem']> | null>(null);
  totalAll = signal(0);
  loading = signal(true);
  categories = signal<S['CategoryDto'][]>([]);
  private timer?: ReturnType<typeof setTimeout>;
  private request = 0;

  constructor() {
    this.api.get<S['CategoryDto'][]>('/categories').then((c) => this.categories.set(c));
    effect(() => {
      const query = { q: this.q(), categoryId: this.category(), page: this.page(), pageSize: this.size };
      untracked(() => this.load(query));
    });
  }

  search(value: string) {
    clearTimeout(this.timer);
    this.timer = setTimeout(() => {
      this.q.set(value);
      this.page.set(1);
    }, 200);
  }

  private async load(query: Record<string, string | number>) {
    const id = ++this.request;
    this.loading.set(true);
    try {
      const res = await this.api.get<Page<S['ProductListItem']>>('/products', query);
      if (id !== this.request) return; // a newer search has started
      this.data.set(res);
      if (!query['q'] && !query['categoryId']) this.totalAll.set(res.total);
    } finally {
      if (id === this.request) this.loading.set(false);
    }
  }
}
