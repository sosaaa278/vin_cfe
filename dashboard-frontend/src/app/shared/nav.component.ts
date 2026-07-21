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
        <a routerLink="/dashboard" routerLinkActive="active">Inconformidades</a>
        <a routerLink="/causas" routerLinkActive="active">Causas</a>
        <a routerLink="/imu" routerLinkActive="active">Por cada mil usuarios</a>
        <a routerLink="/inconformidades-meta" routerLinkActive="active">Inconformidades meta/real</a>
        <a routerLink="/colonias" routerLinkActive="active">Colonias</a>
      </div>
      <button class="btn-logout" (click)="auth.logout()">Cerrar sesión</button>
    </nav>
  `,
  styleUrls: ['./nav.component.css']
})
export class NavComponent {
  constructor(public auth: AuthService) {}
}
