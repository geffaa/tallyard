import { Component, computed, inject, OnInit, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { LucideAngularModule } from 'lucide-angular';
import { Api, S } from '../core/api';
import { Auth } from '../core/auth';
import { I18n } from '../core/i18n';

/** Tiles that mean something is waiting; they turn amber when non-zero. */
const ATTENTION = new Set(['reorder', 'expiring', 'po_approval', 'po_late', 'on_hold', 'overdue_receivables', 'to_ship']);

@Component({
  selector: 'app-home',
  imports: [RouterLink, LucideAngularModule],
  template: `
    <div class="page">
      <div class="page-head">
        <div>
          <div class="eyebrow">{{ auth.me()?.tenant?.name }}</div>
          <h1 class="h1 mt-1">{{ i18n.t('home.title', { name: firstName() }) }}</h1>
          <p class="mt-1 text-muted">{{ i18n.t('home.subtitle') }}</p>
        </div>
      </div>

      <div class="grid gap-3 grid-cols-2 lg:grid-cols-4 mb-5">
        @for (t of d()?.tiles ?? []; track t.key) {
          <a [routerLink]="t.link" class="card px-4 py-3.5 transition-colors hover:border-line-strong"
             [class.!border-signal]="attention(t)" [class.bg-signal-soft]="attention(t)">
            <div class="text-[12.5px] text-muted leading-snug">{{ i18n.t($any('tile.' + t.key)) }}</div>
            <div class="mt-1.5 text-[24px] font-semibold tabular-nums tracking-tight" [class.text-signal-ink]="attention(t)">
              {{ t.kind === 'money' ? i18n.money(t.value, auth.currency()) : i18n.number(t.value) }}
            </div>
          </a>
        }
      </div>

      <div class="grid gap-5 lg:grid-cols-[minmax(0,3fr)_minmax(0,2fr)]">
        <section class="card">
          <div class="card-head">
            <h2 class="card-title">{{ i18n.t(d()?.valueKind === 'money' ? 'home.sales' : 'home.salesUnits') }}</h2>
            <span class="text-[13px] text-muted tabular-nums">
              @if (hover(); as h) { {{ h.day }} · <span class="text-ink font-medium">{{ fmt(value(h)) }}</span> }
              @else { {{ fmt(total()) }} }
            </span>
          </div>
          <!-- One series, one hue: no legend; the header names it and shows the hovered day. -->
          <div class="px-5 pt-4 pb-3">
            <svg class="w-full h-48 overflow-visible" [attr.viewBox]="'0 0 ' + W + ' ' + H" preserveAspectRatio="none" role="img"
                 [attr.aria-label]="i18n.t('home.sales')" (mouseleave)="hover.set(null)">
              @for (g of grid(); track g) {
                <line [attr.x1]="0" [attr.x2]="W" [attr.y1]="g.y" [attr.y2]="g.y" stroke="var(--color-line)" stroke-width="1" vector-effect="non-scaling-stroke" />
              }
              @for (b of bars(); track b.day) {
                <!-- generous hit target, thin mark -->
                <rect [attr.x]="b.x - b.gap / 2" y="0" [attr.width]="b.w + b.gap" [attr.height]="H" fill="transparent" (mouseenter)="hover.set(b.point)" />
                <path [attr.d]="b.path" [attr.fill]="hover()?.day === b.day ? 'var(--color-brand-hover)' : 'var(--color-brand)'" pointer-events="none" />
              }
            </svg>
            <div class="mt-1.5 flex justify-between text-[11.5px] text-faint tabular-nums">
              <span>{{ d()?.sales?.[0]?.day }}</span><span>{{ d()?.sales?.at(-1)?.day }}</span>
            </div>
          </div>
        </section>

        <section class="card">
          <div class="card-head"><h2 class="card-title">{{ i18n.t('home.top') }}</h2></div>
          <ol class="p-5 flex flex-col gap-3">
            @for (t of d()?.topProducts ?? []; track t.label) {
              <li>
                <div class="flex justify-between gap-3 text-[13px]">
                  <span class="truncate">{{ t.label }}</span>
                  <span class="tabular-nums text-ink-2 shrink-0">{{ fmt(t.value) }}</span>
                </div>
                <div class="mt-1 h-1.5 rounded-full bg-sunken overflow-hidden">
                  <div class="h-full rounded-full bg-brand" [style.width.%]="share(t.value)"></div>
                </div>
              </li>
            } @empty {
              <li class="text-muted text-[13px]">{{ i18n.t('home.none') }}</li>
            }
          </ol>
        </section>
      </div>
    </div>
  `,
})
export class HomePage implements OnInit {
  i18n = inject(I18n);
  auth = inject(Auth);
  private api = inject(Api);
  readonly W = 600;
  readonly H = 180;
  d = signal<S['Dashboard'] | null>(null);
  hover = signal<S['DailyPoint'] | null>(null);
  firstName = computed(() => this.auth.me()?.name.split(' ')[0] ?? '');

  value = (p: S['DailyPoint']) => (this.d()?.valueKind === 'money' ? p.revenue : p.units);
  total = computed(() => (this.d()?.sales ?? []).reduce((s, p) => s + this.value(p), 0));
  private max = computed(() => Math.max(1, ...(this.d()?.sales ?? []).map((p) => this.value(p))));

  /** Bars with a 4px rounded top, anchored square to the baseline, 2px gaps. */
  bars = computed(() => {
    const pts = this.d()?.sales ?? [];
    const gap = 2;
    const w = this.W / Math.max(1, pts.length) - gap;
    return pts.map((p, i) => {
      const h = Math.max(0, (this.value(p) / this.max()) * (this.H - 4));
      const x = i * (w + gap);
      const r = Math.min(4, w / 2, h);
      const y = this.H - h;
      const path = h <= 0 ? '' : `M${x},${this.H} V${y + r} Q${x},${y} ${x + r},${y} H${x + w - r} Q${x + w},${y} ${x + w},${y + r} V${this.H} Z`;
      return { day: p.day, x, w, gap, path, point: p };
    });
  });
  grid = computed(() => [0.25, 0.5, 0.75].map((f) => ({ y: this.H - f * (this.H - 4) })));

  async ngOnInit() {
    this.d.set(await this.api.get<S['Dashboard']>('/dashboard'));
  }

  attention(t: S['DashboardTile']) {
    return ATTENTION.has(t.key) && t.value > 0;
  }

  share(v: number) {
    const top = this.d()?.topProducts?.[0]?.value ?? 0;
    return top ? (v / top) * 100 : 0;
  }

  fmt(v: number) {
    return this.d()?.valueKind === 'money' ? this.i18n.money(v, this.auth.currency()) : this.i18n.number(v);
  }
}
