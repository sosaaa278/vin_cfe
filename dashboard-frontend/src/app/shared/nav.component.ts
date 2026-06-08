import { Component } from '@angular/core';
import { RouterLink, RouterLinkActive } from '@angular/router';
import { AuthService } from '../services/auth.service';

// Barra de pestañas compartida por todas las vistas internas.
// Resalta la pestaña activa y ofrece el botón de cerrar sesión.
@Component({
  selector: 'app-nav',
  standalone: true,
  imports: [RouterLink, RouterLinkActive],
  template: `
    <nav class="app-tabs">
      <div class="tabs-links">
        <a routerLink="/dashboard" routerLinkActive="active">Dashboard</a>
        <a routerLink="/causas" routerLinkActive="active">Causas</a>
        <a routerLink="/imu" routerLinkActive="active">Por cada mil usuarios</a>
      </div>
      <button class="btn-logout" (click)="auth.logout()">Cerrar sesión</button>
    </nav>
  `,
  styles: [`
    .app-tabs {
      display: flex;
      justify-content: space-between;
      align-items: center;
      background: #006341;          /* verde CFE */
      padding: 0 1rem;
      border-radius: 8px;
      margin-bottom: 1.25rem;
    }
    .tabs-links { display: flex; gap: .25rem; }
    .tabs-links a {
      color: #d7ecdf;
      text-decoration: none;
      padding: .85rem 1.1rem;
      font-weight: 600;
      font-size: .95rem;
      border-bottom: 3px solid transparent;
    }
    .tabs-links a:hover { color: #fff; }
    .tabs-links a.active {
      color: #fff;
      border-bottom-color: #fff;
    }
    .btn-logout {
      background: transparent;
      border: 1px solid #d7ecdf;
      color: #d7ecdf;
      padding: .35rem .8rem;
      border-radius: 6px;
      font-size: .85rem;
      cursor: pointer;
    }
    .btn-logout:hover { background: rgba(255,255,255,.12); color: #fff; }
  `]
})
export class NavComponent {
  constructor(public auth: AuthService) {}
}
