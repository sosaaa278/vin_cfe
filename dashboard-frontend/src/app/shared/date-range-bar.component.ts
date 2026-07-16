import { Component, OnDestroy } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Subscription } from 'rxjs';
import { DateRangeService } from '../services/date-range.service';

/**
 * Barra prominente para elegir el rango de fechas (Desde / Hasta) que usará el
 * scraping. Se coloca arriba de cada vista (Dashboard, Causas, IMU) para que sea
 * lo primero que vea el usuario al entrar. Escribe el rango en el DateRangeService
 * global; cada vista reacciona a ese cambio.
 */
@Component({
  selector: 'app-date-range-bar',
  standalone: true,
  imports: [CommonModule, FormsModule],
  template: `
    <section class="range-bar">
      <div class="range-bar__head">
        <span class="range-bar__icon">📅</span>
        <span class="range-bar__title">Rango de fechas a consultar</span>
      </div>

      <div class="range-bar__fields">
        <label>Desde
          <input type="date" [(ngModel)]="desde" [max]="hasta">
        </label>
        <label>Hasta
          <input type="date" [(ngModel)]="hasta" [min]="desde">
        </label>
        <button class="range-bar__apply"
                (click)="aplicar()"
                [disabled]="!desde || !hasta || desde > hasta">
          Aplicar
        </button>
      </div>

      <div class="range-bar__current">
        Mostrando:
        <strong>{{ applied.desde }}</strong> →
        <strong>{{ applied.hasta }}</strong>
      </div>
    </section>
  `,
  styleUrls: ['./date-range-bar.component.css']
})
export class DateRangeBarComponent implements OnDestroy {
  desde = '';
  hasta = '';
  applied = { desde: '', hasta: '' };

  private sub?: Subscription;

  constructor(private dateRange: DateRangeService) {
    // Sincroniza los inputs y el texto "Mostrando" con el rango global actual.
    this.sub = this.dateRange.range$.subscribe(r => {
      this.desde = r.desde;
      this.hasta = r.hasta;
      this.applied = { ...r };
    });
  }

  ngOnDestroy(): void {
    this.sub?.unsubscribe();
  }

  aplicar(): void {
    if (!this.desde || !this.hasta || this.desde > this.hasta) return;
    this.dateRange.set(this.desde, this.hasta);
  }
}
