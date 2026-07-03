import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Router } from '@angular/router';
import { tap } from 'rxjs/operators';
import { environment } from '../../environments/environment';

@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly API = `${environment.apiUrl}/api/auth`;
  private readonly TOKEN_KEY = 'imu_token';

  constructor(private http: HttpClient, private router: Router) {}

  // ── Zones per division (sub-zones within each CFE division) ─────────────────
  static readonly ZONAS_POR_DIVISION: Record<string, { value: string; label: string }[]> = {
    'DA000': [{ value: '00000', label: 'Todas las zonas' }],                  // Baja California
    'DB000': [{ value: '00000', label: 'Todas las zonas' }],                  // Noroeste
    'DC000': [                                                                  // Norte
      { value: '00000', label: 'Todas las zonas' },
      { value: 'DC010', label: 'Chihuahua' },
      { value: 'DC020', label: 'Cuauhtémoc' },
      { value: 'DC040', label: 'Juárez' },
      { value: 'DC060', label: 'Delicias' },
      { value: 'DC140', label: 'Casas Grandes' },
      { value: 'DC220', label: 'Torreón' },
      { value: 'DC240', label: 'Parral' },
      { value: 'DC260', label: 'Durango' },
      { value: 'DC270', label: 'Gómez Palacio' },
    ],
    'DD000': [{ value: '00000', label: 'Todas las zonas' }],                  // Golfo Norte
    'DF000': [{ value: '00000', label: 'Todas las zonas' }],                  // Centro Occidente
    'DG000': [{ value: '00000', label: 'Todas las zonas' }],                  // Centro Sur
    'DJ000': [{ value: '00000', label: 'Todas las zonas' }],                  // Oriente
    'DK000': [{ value: '00000', label: 'Todas las zonas' }],                  // Sureste
    'DL000': [{ value: '00000', label: 'Todas las zonas' }],                  // VM Norte
    'DM000': [{ value: '00000', label: 'Todas las zonas' }],                  // VM Centro
    'DN000': [{ value: '00000', label: 'Todas las zonas' }],                  // VM Sur
    'DP000': [{ value: '00000', label: 'Todas las zonas' }],                  // Bajío
    'DU000': [{ value: '00000', label: 'Todas las zonas' }],                  // Golfo Centro
    'DV000': [{ value: '00000', label: 'Todas las zonas' }],                  // Centro Oriente
    'DW000': [{ value: '00000', label: 'Todas las zonas' }],                  // Peninsular
    'DX000': [{ value: '00000', label: 'Todas las zonas' }],                  // Jalisco
  };

  private decodeJwtPayload(token: string): any {
    // JWT usa base64URL (- y _ en vez de + y /). atob() requiere base64 estándar.
    const b64url = token.split('.')[1];
    const b64    = b64url.replace(/-/g, '+').replace(/_/g, '/');
    const padded = b64 + '=='.slice(0, (4 - b64.length % 4) % 4);
    return JSON.parse(atob(padded));
  }

  getDivision(): string {
    const token = this.getToken();
    if (!token) return '';
    try {
      return this.decodeJwtPayload(token)['division'] ?? '';
    } catch { return ''; }
  }

  getZonas(): { value: string; label: string }[] {
    const div = this.getDivision();
    return AuthService.ZONAS_POR_DIVISION[div] ?? [{ value: '00000', label: 'Todas las zonas' }];
  }

  login(rpe: string, password: string) {
    return this.http.post<{ token: string; expiry: string }>(
      `${this.API}/login`,
      { rpe, password }
    ).pipe(
      tap(res => localStorage.setItem(this.TOKEN_KEY, res.token))
    );
  }

  logout(): void {
    localStorage.removeItem(this.TOKEN_KEY);
    this.router.navigate(['/login']);
  }

  getToken(): string | null {
    return localStorage.getItem(this.TOKEN_KEY);
  }

  isLoggedIn(): boolean {
    const token = this.getToken();
    if (!token) return false;
    try {
      return this.decodeJwtPayload(token).exp * 1000 > Date.now();
    } catch {
      return false;
    }
  }
}
