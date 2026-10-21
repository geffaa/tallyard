import { Component, computed, inject, signal } from '@angular/core';
import { NavigationEnd, Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { filter } from 'rxjs';
import {
  ArrowLeftRight,
  Boxes,
  Building2,
  LogOut,
  LucideAngularModule,
  LucideIconData,
  Menu,
  Package,
  ScrollText,
  SlidersHorizontal,
  ClipboardList,
  RefreshCcw,
  ShieldCheck,
  ScanText,
  Sparkles,
  ReceiptText,
  Wallet,
  ChartColumn,
  ShoppingCart,
  Truck,
  Users,
  Warehouse,
  X,
} from 'lucide-angular';
import { Auth, Permission } from '../core/auth';
import { I18n, Lang } from '../core/i18n';
import { MessageKey } from '../core/messages';
import { Logo } from './logo';

interface NavItem {
  path: string;
  label: MessageKey;
  icon: LucideIconData;
  perm?: Permission;
  /** Highlight only on this exact path (its sub-pages have their own item). */
  exact?: boolean;
}

@Component({
  selector: 'app-shell',
  imports: [RouterOutlet, RouterLink, RouterLinkActive, LucideAngularModule, Logo],
  template: `
    <div class="min-h-dvh lg:pl-[248px]">
      <!-- Mobile top bar -->
      <header class="lg:hidden sticky top-0 z-30 flex items-center justify-between h-14 px-4 bg-rail text-white">
        <app-logo [compact]="true" />
        <button class="btn btn-ghost !text-white hover:!bg-rail-2" (click)="open.set(true)" [attr.aria-label]="i18n.t('nav.menu')">
          <lucide-icon [img]="MenuIcon" [size]="20" />
        </button>
      </header>

      @if (open()) {
        <div class="lg:hidden fixed inset-0 z-40 bg-ink/40" (click)="open.set(false)"></div>
      }

      <aside
        class="fixed inset-y-0 left-0 z-50 w-[248px] flex flex-col bg-rail text-rail-ink transition-transform lg:translate-x-0"
        [class.-translate-x-full]="!open()"
      >
        <div class="flex items-center justify-between h-16 px-5">
          <app-logo />
          <button class="lg:hidden text-rail-ink" (click)="open.set(false)" aria-label="Close">
            <lucide-icon [img]="XIcon" [size]="18" />
          </button>
        </div>

        <div class="mx-3 mb-3 rounded-lg bg-rail-2 px-3 py-2.5">
          <div class="text-[13px] font-semibold text-white truncate">{{ auth.me()?.tenant?.name }}</div>
          <div class="text-[11.5px] text-rail-ink/70 font-mono">{{ auth.me()?.tenant?.slug }}</div>
        </div>

        <nav class="flex-1 overflow-y-auto px-3 pb-4 text-[13.5px]">
          @for (section of sections(); track section.title) {
            <div class="mt-4 mb-1.5 px-2 text-[10.5px] font-semibold uppercase tracking-[0.1em] text-rail-ink/50">
              {{ i18n.t(section.title) }}
            </div>
            @for (item of section.items; track item.path) {
              <a
                  [routerLink]="item.path"
                  routerLinkActive="active"
                  [routerLinkActiveOptions]="{ exact: !!item.exact }"
                  class="nav-link group relative flex items-center gap-2.5 h-9 px-2.5 rounded-md hover:bg-rail-2 hover:text-white"
                >
                  <lucide-icon [img]="item.icon" [size]="16" />
                  {{ i18n.t(item.label) }}
                </a>
            }
          }
        </nav>

        <div class="border-t border-white/8 p-3">
          <div class="flex items-center gap-2.5 px-2 py-1.5">
            <div class="grid place-items-center size-8 rounded-full bg-signal text-rail text-[12px] font-bold">
              {{ initials() }}
            </div>
            <div class="min-w-0 flex-1">
              <div class="text-[13px] font-medium text-white truncate">{{ auth.me()?.name }}</div>
              <div class="text-[11.5px] text-rail-ink/70">{{ auth.me() ? i18n.t($any('role.' + auth.me()!.role)) : '' }}</div>
            </div>
            <button class="p-1.5 rounded-md hover:bg-rail-2 hover:text-white" (click)="logout()" [title]="i18n.t('nav.signOut')">
              <lucide-icon [img]="LogOutIcon" [size]="16" />
            </button>
          </div>
          <div class="mt-2 flex rounded-md bg-rail-2 p-0.5 text-[12px]" role="group" [attr.aria-label]="i18n.t('nav.language')">
            @for (l of langs; track l.code) {
              <button
                class="flex-1 h-7 rounded font-medium transition-colors"
                [class]="i18n.lang() === l.code ? 'bg-rail text-white' : 'text-rail-ink/70 hover:text-white'"
                (click)="i18n.setLang(l.code)"
              >
                {{ l.label }}
              </button>
            }
          </div>
        </div>
      </aside>

      <main class="min-w-0">
        <router-outlet />
      </main>
    </div>
  `,
  styles: `
    .nav-link.active { background: var(--color-rail-2); color: white; font-weight: 500; }
    .nav-link.active::before {
      content: ''; position: absolute; left: -12px; top: 8px; bottom: 8px; width: 3px;
      border-radius: 0 3px 3px 0; background: var(--color-signal);
    }
  `,
})
export class Shell {
  auth = inject(Auth);
  i18n = inject(I18n);
  private router = inject(Router);
  open = signal(false);

  MenuIcon = Menu;
  XIcon = X;
  LogOutIcon = LogOut;
  langs: { code: Lang; label: string }[] = [
    { code: 'en', label: 'English' },
    { code: 'id', label: 'Indonesia' },
  ];

  private all: { title: MessageKey; items: NavItem[] }[] = [
    { title: 'nav.ai', items: [{ path: '/assistant', label: 'nav.assistant', icon: Sparkles }] },
    {
      title: 'nav.inventory',
      items: [
        { path: '/stock', label: 'nav.inventory', icon: Boxes, exact: true },
        { path: '/stock/adjustments', label: 'nav.adjustments', icon: SlidersHorizontal },
        { path: '/transfers', label: 'nav.transfers', icon: ArrowLeftRight },
      ],
    },
    {
      title: 'nav.purchasing',
      items: [
        { path: '/purchasing', label: 'nav.orders', icon: ClipboardList, perm: 'ViewPurchasing', exact: true },
        { path: '/purchasing/reorder', label: 'nav.reorder', icon: RefreshCcw, perm: 'ViewPurchasing' },
        { path: '/purchasing/documents', label: 'nav.documents', icon: ScanText, perm: 'EditPurchaseOrders' },
      ],
    },
    {
      title: 'nav.masterData',
      items: [
        { path: '/products', label: 'nav.products', icon: Package },
        { path: '/warehouses', label: 'nav.warehouses', icon: Warehouse },
        { path: '/suppliers', label: 'nav.suppliers', icon: Truck },
        { path: '/customers', label: 'nav.customers', icon: Building2 },
      ],
    },
    {
      title: 'nav.sales',
      items: [
        { path: '/sales', label: 'nav.salesOrders', icon: ShoppingCart, perm: 'ViewSales' },
      ],
    },
    {
      title: 'nav.finance',
      items: [
        { path: '/invoices', label: 'nav.invoices', icon: ReceiptText, perm: 'ViewSalesPrices' },
        { path: '/payments', label: 'nav.payments', icon: Wallet, perm: 'ManageReceivables' },
        { path: '/receivables', label: 'nav.aging', icon: ChartColumn, perm: 'ViewSalesPrices' },
      ],
    },
    {
      title: 'nav.admin',
      items: [
        { path: '/users', label: 'nav.users', icon: Users, perm: 'ManageUsers' },
        { path: '/settings/approvals', label: 'nav.approvalRules', icon: ShieldCheck, perm: 'ManageUsers' },
        { path: '/audit', label: 'nav.audit', icon: ScrollText, perm: 'ViewAudit' },
      ],
    },
  ];

  sections = computed(() => {
    this.auth.me();
    return this.all
      .map((s) => ({ ...s, items: s.items.filter((i) => !i.perm || this.auth.can(i.perm)) }))
      .filter((s) => s.items.length > 0);
  });

  initials = computed(() =>
    (this.auth.me()?.name ?? '')
      .split(' ')
      .map((p) => p[0])
      .slice(0, 2)
      .join(''),
  );

  constructor() {
    // Close the mobile drawer after navigating.
    this.router.events.pipe(filter((e) => e instanceof NavigationEnd)).subscribe(() => this.open.set(false));
  }

  async logout() {
    await this.auth.logout();
    this.router.navigate(['/login']);
  }
}
