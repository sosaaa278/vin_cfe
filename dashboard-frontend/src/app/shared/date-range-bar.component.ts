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
  styles: [`
    .range-bar {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 1rem;
      background: #eafaf1;
      border: 1px solid #b7e4c7;
      border-left: 6px solid #006341;
      border-radius: 8px;
      padding: .75rem 1rem;
      margin-bottom: 1.25rem;
    }
    .range-bar__head { display: flex; align-items: center; gap: .5rem; }
    .range-bar__icon { font-size: 1.25rem; }
    .range-bar__title {
      font-weight: 700;
      color: #064e3b;
      font-size: 1rem;
    }
    .range-bar__fields {
      display: flex;
      align-items: flex-end;
      gap: .6rem;
      flex-wrap: wrap;
    }
    .range-bar__fields label {
      display: flex;
      flex-direction: column;
      font-size: .8rem;
      font-weight: 600;
      color: #1f4d3a;
      line-height: 1.2;
    }
    .range-bar__fields input[type="date"] {
      margin-top: 3px;
      border: 1px solid #9ccab0;
      border-radius: 6px;
      padding: .35rem .5rem;
      font-size: .9rem;
    }
    .range-bar__apply {
      background: #006341;
      color: #fff;
      border: none;
      padding: .5rem 1.2rem;
      border-radius: 6px;
      font-size: .9rem;
      font-weight: 700;
      cursor: pointer;
    }
    .range-bar__apply:disabled { opacity: .5; cursor: not-allowed; }
    .range-bar__apply:not(:disabled):hover { background: #00875a; }
    .range-bar__current {
      margin-left: auto;
      font-size: .85rem;
      color: #1f4d3a;
      background: #fff;
      border: 1px dashed #9ccab0;
      border-radius: 6px;
      padding: .35rem .7rem;
    }
  `]
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
