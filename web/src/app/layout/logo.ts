import { Component, input } from '@angular/core';

/** The mark is a "T" drawn like a pallet rack: a beam on a post, with two pallets on the floor. */
@Component({
  selector: 'app-logo',
  template: `
    <div class="flex items-center gap-2.5">
      <svg [attr.width]="compact() ? 26 : 30" [attr.height]="compact() ? 26 : 30" viewBox="0 0 32 32" aria-hidden="true">
        <rect width="32" height="32" rx="7" fill="#0e5e5e" />
        <path d="M9 11h14M16 11v12" stroke="#f2a541" stroke-width="3.2" stroke-linecap="round" />
        <path d="M9 23h4M19 23h4" stroke="#fff" stroke-width="2.4" stroke-linecap="round" opacity=".7" />
      </svg>
      <span class="text-[17px] font-semibold tracking-[-0.01em]" [class]="dark() ? 'text-ink' : 'text-white'">Tallyard</span>
    </div>
  `,
})
export class Logo {
  compact = input(false);
  dark = input(false);
}
