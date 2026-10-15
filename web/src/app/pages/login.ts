import { Component, inject, input, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router } from '@angular/router';
import { ApiError } from '../core/api';
import { Auth } from '../core/auth';
import { I18n } from '../core/i18n';
import { Logo } from '../layout/logo';

const DEMO = [
  { email: 'owner@harborpine.test', role: 'Owner' },
  { email: 'purchasing@harborpine.test', role: 'Purchasing' },
  { email: 'warehouse@harborpine.test', role: 'Warehouse' },
  { email: 'sales@harborpine.test', role: 'Sales' },
  { email: 'finance@harborpine.test', role: 'Finance' },
] as const;

@Component({
  selector: 'app-login',
  imports: [ReactiveFormsModule, Logo],
  template: `
    <div class="min-h-dvh grid lg:grid-cols-[1fr_minmax(0,560px)]">
      <!-- Brand panel: a stylised rack of stock levels -->
      <section class="relative hidden lg:flex flex-col justify-between overflow-hidden bg-rail p-12 text-white">
        <app-logo />
        <div class="relative z-10 max-w-[480px]">
          <h1 class="text-[34px] leading-[1.15] font-semibold tracking-[-0.02em]">{{ i18n.t('login.side.title') }}</h1>
          <p class="mt-4 text-[15px] leading-relaxed text-rail-ink">{{ i18n.t('login.side.body') }}</p>
        </div>
        <div class="relative z-10 grid grid-cols-3 gap-3 max-w-[520px]" aria-hidden="true">
          @for (w of racks; track w.code) {
            <div class="rounded-lg bg-rail-2 p-3">
              <div class="flex justify-between text-[11px] font-mono text-rail-ink/70">
                <span>{{ w.code }}</span><span>{{ w.fill }}%</span>
              </div>
              <div class="mt-2.5 flex items-end gap-1 h-14">
                @for (h of w.bars; track $index) {
                  <div class="flex-1 rounded-sm" [style.height.%]="h" [class]="h < 25 ? 'bg-signal' : 'bg-brand'"></div>
                }
              </div>
            </div>
          }
        </div>
        <div class="absolute -right-24 -top-24 size-[420px] rounded-full bg-brand/25 blur-3xl"></div>
      </section>

      <section class="flex flex-col justify-center px-6 py-10 sm:px-14 bg-paper">
        <div class="lg:hidden mb-10"><app-logo [dark]="true" /></div>
        <div class="w-full max-w-[400px] mx-auto lg:mx-0">
          <h2 class="text-[22px] font-semibold tracking-[-0.01em]">{{ i18n.t('login.title') }}</h2>
          <p class="mt-1 text-muted">{{ i18n.t('app.tagline') }}</p>

          <form class="mt-8 flex flex-col gap-4" [formGroup]="form" (ngSubmit)="submit()">
            <label class="field">
              <span class="label">{{ i18n.t('login.workspace') }}</span>
              <input class="input input-mono" formControlName="workspace" autocomplete="organization" />
            </label>
            <label class="field">
              <span class="label">{{ i18n.t('login.email') }}</span>
              <input class="input" type="email" formControlName="email" autocomplete="username" />
            </label>
            <label class="field">
              <span class="label">{{ i18n.t('login.password') }}</span>
              <input class="input" type="password" formControlName="password" autocomplete="current-password" />
            </label>
            @if (error()) {
              <div class="rounded-lg bg-danger-soft px-3 py-2 text-[13px] text-danger">{{ error() }}</div>
            }
            <button class="btn btn-primary h-10 mt-1" [disabled]="busy() || form.invalid">{{ i18n.t('login.submit') }}</button>
          </form>

          <div class="mt-8 rounded-xl border border-line bg-surface p-4">
            <div class="eyebrow">{{ i18n.t('login.demo') }}</div>
            <div class="mt-2.5 grid grid-cols-1 sm:grid-cols-2 gap-1.5">
              @for (d of demo; track d.email) {
                <button
                  type="button"
                  class="flex flex-col items-start rounded-lg px-2.5 py-1.5 text-left hover:bg-sunken"
                  [class.bg-brand-soft]="form.value.email === d.email"
                  (click)="pick(d.email)"
                >
                  <span class="text-[13px] font-medium">{{ i18n.t($any('role.' + d.role)) }}</span>
                  <span class="text-[11.5px] text-muted font-mono truncate max-w-full">{{ d.email }}</span>
                </button>
              }
            </div>
            <p class="hint mt-2.5">
              {{ i18n.t('login.demoHint', { password: 'tallyard123' }) }} ·
              <button type="button" class="underline hover:text-ink" (click)="i18n.setLang(i18n.lang() === 'en' ? 'id' : 'en')">
                {{ i18n.lang() === 'en' ? 'Bahasa Indonesia' : 'English' }}
              </button>
            </p>
          </div>
        </div>
      </section>
    </div>
  `,
})
export class LoginPage {
  i18n = inject(I18n);
  private auth = inject(Auth);
  private router = inject(Router);
  next = input<string>();

  demo = DEMO;
  racks = [
    { code: 'CHI', fill: 72, bars: [80, 64, 92, 18, 70, 85] },
    { code: 'DAL', fill: 58, bars: [55, 40, 72, 66, 22, 48] },
    { code: 'ATL', fill: 81, bars: [90, 76, 84, 95, 60, 12] },
  ];

  form = inject(FormBuilder).nonNullable.group({
    workspace: ['harborpine', Validators.required],
    email: ['owner@harborpine.test', Validators.required],
    password: ['tallyard123', Validators.required],
  });
  busy = signal(false);
  error = signal('');

  pick(email: string) {
    this.form.patchValue({ email, password: 'tallyard123', workspace: 'harborpine' });
  }

  async submit() {
    this.busy.set(true);
    this.error.set('');
    try {
      const { workspace, email, password } = this.form.getRawValue();
      await this.auth.login(workspace, email, password);
      this.router.navigateByUrl(this.next() || '/');
    } catch (e) {
      this.error.set(ApiError.from(e).message);
    } finally {
      this.busy.set(false);
    }
  }
}
