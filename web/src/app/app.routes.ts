import { Routes } from '@angular/router';
import { requires, signedIn } from './core/auth';

export const routes: Routes = [
  { path: 'login', loadComponent: () => import('./pages/login').then((m) => m.LoginPage) },
  {
    path: '',
    canActivate: [signedIn],
    loadComponent: () => import('./layout/shell').then((m) => m.Shell),
    children: [
      { path: '', pathMatch: 'full', redirectTo: 'stock' },
      { path: 'products', loadComponent: () => import('./pages/products/list').then((m) => m.ProductsPage) },
      { path: 'products/new', loadComponent: () => import('./pages/products/form').then((m) => m.ProductForm) },
      { path: 'products/:id', loadComponent: () => import('./pages/products/form').then((m) => m.ProductForm) },
      { path: 'stock', loadComponent: () => import('./pages/stock/list').then((m) => m.StockPage) },
      {
        path: 'stock/adjust',
        canActivate: [requires('AdjustStock')],
        loadComponent: () => import('./pages/stock/adjust').then((m) => m.AdjustPage),
      },
      { path: 'stock/adjustments', loadComponent: () => import('./pages/stock/adjustments').then((m) => m.AdjustmentsPage) },
      { path: 'stock/:id', loadComponent: () => import('./pages/stock/detail').then((m) => m.StockDetailPage) },
      { path: 'transfers', loadComponent: () => import('./pages/transfers').then((m) => m.TransfersPage) },
      {
        path: 'transfers/new',
        canActivate: [requires('TransferStock')],
        loadComponent: () => import('./pages/transfers').then((m) => m.TransferNewPage),
      },
      { path: 'transfers/:id', loadComponent: () => import('./pages/transfers').then((m) => m.TransferDetailPage) },
      { path: 'warehouses', loadComponent: () => import('./pages/warehouses').then((m) => m.WarehousesPage) },
      {
        path: 'suppliers',
        data: { kind: 'suppliers' },
        loadComponent: () => import('./pages/partners').then((m) => m.PartnersPage),
      },
      {
        path: 'customers',
        data: { kind: 'customers' },
        loadComponent: () => import('./pages/partners').then((m) => m.PartnersPage),
      },
      {
        path: 'users',
        canActivate: [requires('ManageUsers')],
        loadComponent: () => import('./pages/users').then((m) => m.UsersPage),
      },
      {
        path: 'audit',
        canActivate: [requires('ViewAudit')],
        loadComponent: () => import('./pages/audit').then((m) => m.AuditPage),
      },
    ],
  },
  { path: '**', redirectTo: '' },
];
