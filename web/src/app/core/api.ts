import { HttpClient, HttpErrorResponse, HttpInterceptorFn, HttpParams } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { Router } from '@angular/router';
import { catchError, firstValueFrom, throwError } from 'rxjs';
import type { components } from './api-types';
import { I18n } from './i18n';

/** Types generated from the API's OpenAPI document (`npm run api:types`). */
export type S = components['schemas'];

export interface Page<T> {
  items: T[];
  total: number;
  pageNumber: number;
  pageSize: number;
}

/** The API's error body: a stable code, a message in the request's language, and field errors. */
export class ApiError extends Error {
  constructor(
    readonly status: number,
    readonly code: string,
    message: string,
    readonly fields: Record<string, string[]> = {},
  ) {
    super(message);
  }

  static from(e: unknown): ApiError {
    if (e instanceof ApiError) return e;
    if (e instanceof HttpErrorResponse) {
      const body = e.error ?? {};
      return new ApiError(e.status, body.code ?? 'server_error', body.detail ?? e.message, body.errors ?? {});
    }
    return new ApiError(0, 'network', String(e));
  }
}

/** Sends the UI language, and on a 401 (session ended elsewhere) goes back to sign-in. */
export const apiInterceptor: HttpInterceptorFn = (req, next) => {
  const i18n = inject(I18n);
  const router = inject(Router);
  return next(req.clone({ setHeaders: { 'Accept-Language': i18n.lang() } })).pipe(
    catchError((e: HttpErrorResponse) => {
      if (e.status === 401 && !req.url.endsWith('/auth/login') && !req.url.endsWith('/auth/me'))
        router.navigate(['/login'], { queryParams: { next: router.url } });
      return throwError(() => ApiError.from(e));
    }),
  );
};

type Query = Record<string, string | number | boolean | null | undefined>;

@Injectable({ providedIn: 'root' })
export class Api {
  private http = inject(HttpClient);

  get<T>(path: string, query?: Query): Promise<T> {
    return firstValueFrom(this.http.get<T>('/api' + path, { params: params(query) }));
  }
  post<T>(path: string, body: unknown): Promise<T> {
    return firstValueFrom(this.http.post<T>('/api' + path, body));
  }
  put<T>(path: string, body: unknown): Promise<T> {
    return firstValueFrom(this.http.put<T>('/api' + path, body));
  }
}

function params(q?: Query): HttpParams {
  let p = new HttpParams();
  for (const [k, v] of Object.entries(q ?? {})) if (v !== null && v !== undefined && v !== '') p = p.set(k, String(v));
  return p;
}

/**
 * Wraps a loader so only the latest call's result is kept: when a filter changes quickly, a slow
 * earlier response must not overwrite a newer one.
 */
export function latest<A extends unknown[], T>(load: (...args: A) => Promise<T>, apply: (value: T) => void) {
  let current = 0;
  return async (...args: A) => {
    const mine = ++current;
    const value = await load(...args);
    if (mine === current) apply(value);
  };
}
