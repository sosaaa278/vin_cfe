import { Component } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { AuthService } from '../services/auth.service';

@Component({
  selector: 'app-login',
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: './login.component.html',
  styleUrls: ['./login.component.css']
})
export class LoginComponent {
  rpe      = '';
  password = '';
  error    = '';
  loading  = false;

  constructor(private auth: AuthService, private router: Router) {}

  login(): void {
    if (!this.rpe || !this.password) {
      this.error = 'Completa todos los campos';
      return;
    }
    this.loading = true;
    this.error   = '';
    this.auth.login(this.rpe, this.password).subscribe({
      next: () => this.router.navigate(['/dashboard']),
      error: () => {
        this.error   = 'Usuario o contraseña incorrectos';
        this.loading = false;
      }
    });
  }
}
