import { Injectable } from '@angular/core';
import { BehaviorSubject } from 'rxjs';

/** Rango de fechas elegido por el usuario para el scraping. Formato 'yyyy-MM-dd'. */
export interface DateRange {
  desde: string;
  hasta: string;
}

/**
 * Guarda el rango de fechas (desde/hasta) que el usuario elige en la barra superior.
 * Es global (providedIn: 'root') y lo comparten Dashboard, Causas e IMU.
 * Se persiste en localStorage para que sobreviva a recargas de la página.
 */
@Injectable({ providedIn: 'root' })
export class DateRangeService {
  private readonly KEY = 'scrapeDateRange';

  private readonly _range$ = new BehaviorSubject<DateRange>(this.load());
  /** Emite el rango actual y cada cambio posterior. */
  readonly range$ = this._range$.asObservable();

  /** Rango por defecto: 1 de enero → 4 de mayo del año en curso. */
  private defaultRange(): DateRange {
    const y = new Date().getFullYear();
    return { desde: `${y}-01-01`, hasta: `${y}-05-04` };
  }

  private load(): DateRange {
    try {
      const raw = localStorage.getItem(this.KEY);
      if (raw) {
        const r = JSON.parse(raw);
        if (r?.desde && r?.hasta) return r;
      }
    } catch { /* localStorage no disponible: usamos el default */ }
    return this.defaultRange();
  }

  /** Valor actual sin suscribirse. */
  get current(): DateRange {
    return this._range$.value;
  }

  /** Actualiza el rango y notifica a todos los suscriptores. */
  set(desde: string, hasta: string): void {
    const range: DateRange = { desde, hasta };
    try { localStorage.setItem(this.KEY, JSON.stringify(range)); } catch { /* ignore */ }
    this._range$.next(range);
  }
}
