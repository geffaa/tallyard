import { computed, inject, Injectable, signal } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { Api, S } from './api';

export type Me = S['MeResponse'];
export type Permission =
  | 'ManageUsers'
  | 'ManageWarehouses'
  | 'EditCatalog'
  | 'EditSuppliers'
  | 'EditCustomers'
  | 'ViewAudit'
  | 'AdjustStock'
  | 'TransferStock'
  | 'ViewCosts';

@Injectable({ providedIn: 'root' })
export class Auth {
  private api = inject(Api);
  readonly me = signal<Me | null>(null);
  readonly currency = computed(() => this.me()?.tenant.currency ?? 'USD');
  private loaded: Promise<Me | null> | null = null;

  /** Loads the signed-in user once per page load (the cookie is httpOnly, so ask the API). */
  load(): Promise<Me | null> {
    this.loaded ??= this.api.get<Me>('/auth/me').then(
      (me) => (this.me.set(me), me),
      () => null,
    );
    return this.loaded;
  }

  async login(workspace: string, email: string, password: string) {
    const me = await this.api.post<Me>('/auth/login', { workspace, email, password });
    this.me.set(me);
    this.loaded = Promise.resolve(me);
  }

  async logout() {
    await this.api.post('/auth/logout', {});
    this.me.set(null);
    this.loaded = null;
  }

  /** Hides what the user can't use; the API enforces the same rules. */
  can(p: Permission): boolean {
    return this.me()?.permissions.includes(p) ?? false;
  }
}

export const signedIn: CanActivateFn = async (_, state) => {
  const auth = inject(Auth);
  if (await auth.load()) return true;
  return inject(Router).createUrlTree(['/login'], { queryParams: { next: state.url } });
};

export const requires =
  (p: Permission): CanActivateFn =>
  async () => {
    const auth = inject(Auth);
    await auth.load();
    return auth.can(p) || inject(Router).createUrlTree(['/']);
  };
