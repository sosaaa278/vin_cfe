import { Routes } from '@angular/router';
import { authGuard } from './guards/auth.guard';

export const routes: Routes = [
  { path: '', redirectTo: 'login', pathMatch: 'full' },
  {
    path: 'login',
    loadComponent: () =>
      import('./login/login.component').then(m => m.LoginComponent)
  },
  {
    path: 'dashboard',
    loadComponent: () =>
      import('./dashboard/dashboard.component').then(m => m.DashboardComponent),
    canActivate: [authGuard]
  },
  {
    path: 'causas',
    loadComponent: () =>
      import('./causas/causas.component').then(m => m.CausasComponent),
    canActivate: [authGuard]
  },
  {
    path: 'imu',
    loadComponent: () =>
      import('./imu/imu.component').then(m => m.ImuComponent),
    canActivate: [authGuard]
  },
  { path: '**', redirectTo: 'login' }
];
