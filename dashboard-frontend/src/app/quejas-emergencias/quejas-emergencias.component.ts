import { Component, OnDestroy, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import Chart from 'chart.js/auto';
import * as XLSX from 'xlsx-js-style';
import { DashboardService } from '../services/dashboard.service';
import { NavComponent } from '../shared/nav.component';
import { saveWorkbook } from '../shared/excel-export';
import { timeout, finalize } from 'rxjs/operators';
import { Subscription } from 'rxjs';

// SICOSS Distribución consulta en vivo otro sistema interno — igual que
// SCRAPE_TIMEOUT_MS en colonias/causas.
const SCRAPE_TIMEOUT_MS = 300_000;

// Etiqueta de valor sobre cada barra — necesario porque en "Por tipo de inconformidad"
// las barras pueden tener escalas MUY distintas (ej. 1200 Pendientes vs 3 En atención) y
// la barra chica se vería vacía sin esto; el número siempre queda legible encima.
const BAR_DATALABELS_PLUGIN: any = {
  id: 'sicossBarLabels',
  afterDatasetsDraw(chart: any) {
    if (chart.config.type !== 'bar') return;
    const { ctx } = chart;
    (chart.data.datasets as any[]).forEach((ds: any, di: number) => {
      const meta = chart.getDatasetMeta(di);
      if (meta.hidden) return;
      meta.data.forEach((el: any, idx: number) => {
        const val = ds.data[idx];
        if (val == null || val === 0) return;
        const txt = Number(val).toLocaleString('es-MX');
        ctx.save();
        ctx.fillStyle = '#1a202c';
        ctx.font = '600 9px "Segoe UI", sans-serif';
        ctx.textAlign = 'center';
        ctx.textBaseline = 'bottom';
        ctx.fillText(txt, el.x, el.y - 2);
        ctx.restore();
      });
    });
  }
};

// Vista "Quejas y Emergencias" — hoy solo consume SICOSS Distribución
// (http://10.4.14.1/cgi-bin/sicossweb/solicitudes/pendctosa.cgi), un sistema en vivo sin
// rango de fechas (siempre es el estado "ahora mismo").
@Component({
  selector: 'app-quejas-emergencias',
  standalone: true,
  imports: [CommonModule, FormsModule, NavComponent],
  templateUrl: './quejas-emergencias.component.html',
  styleUrls: ['./quejas-emergencias.component.css']
})
export class QuejasEmergenciasComponent implements OnInit, OnDestroy {

  // ── SICOSS Distribución — solicitudes pendientes por zona/centro ───────────────
  sicossZonas: { value: string; label: string }[] = [];
  sicossCentros: { value: string; label: string }[] = [];
  sicossZona = '';
  sicossCen = '';
  sicossStatus: 'WAITING' | 'LOADING' | 'SUCCESS' | 'ERROR' = 'WAITING';
  sicossErrorMsg = '';
  sicossData: any[] = [];
  sicossPorStatus: { status: string; count: number }[] = [];
  sicossPorTipo: { tipo: string; pendientes: number; enAtencion: number; porAsignar: number; total: number }[] = [];
  private sicossChart: any = null;
  private sicossStatusChart: any = null;
  private sicossTipoChart: any = null;

  // ── Modal "detalle de solicitud" (clic en una fila de "Solicitudes por tipo") ──
  // Bajo demanda: se pide solo al hacer clic, no junto con la tabla completa — mismo
  // patrón que el detalle en vivo del modal de Colonias (evita disparar N peticiones
  // extra, una por solicitud, cada vez que se carga la tabla).
  modalSolicitudRow: any = null;      // fila clickeada; null = modal cerrado
  detalleLoading = false;
  detalleErrorMsg = '';
  detalleMovimientos: any[] = [];
  detalleServicios: any[] = [];
  detalleMovimientosCols: string[] = [];
  detalleServiciosCols: string[] = [];
  private detalleSub?: Subscription;

  constructor(private dashboardService: DashboardService) {}

  ngOnInit(): void {
    this.dashboardService.getSicossZonas().subscribe({
      next: zonas => { this.sicossZonas = zonas ?? []; },
      error: () => { this.sicossZonas = []; }
    });
  }

  ngOnDestroy(): void {
    this.destroySicossCharts();
    this.detalleSub?.unsubscribe();
  }

  /** Clic en una solicitud de la tabla "detalle" — trae bitácora de movimientos (quién
   * preasignó y a qué cuadrilla de CFE, ej. "Cuad:C2223" en Observaciones) y bitácora de
   * servicios (historial en esa dirección, incluye contratista cuando "Atendido por" dice
   * algo como "CONTT - CONTRATISTA CCC1"). Clic de nuevo en la misma solicitud lo cierra. */
  onSolicitudClick(row: any): void {
    if (this.modalSolicitudRow === row) {
      this.closeDetalleModal();
      return;
    }
    // Ya hay un detalle en curso — ignora el clic en vez de apilar otra petición
    // (mismo criterio que colonias tras el problema de scrapes acumulados).
    if (this.detalleLoading) return;

    this.modalSolicitudRow = row;
    this.detalleErrorMsg = '';
    this.detalleMovimientos = [];
    this.detalleServicios = [];
    this.detalleMovimientosCols = [];
    this.detalleServiciosCols = [];
    this.detalleLoading = true;

    const folio = String(row['Solicitud'] ?? '').trim();
    this.detalleSub?.unsubscribe();
    this.detalleSub = this.dashboardService.getSicossDetalle(folio)
      .pipe(finalize(() => { this.detalleLoading = false; }))
      .subscribe({
        next: data => {
          this.detalleMovimientos = data?.Movimientos ?? [];
          this.detalleServicios = data?.Servicios ?? [];
          this.detalleMovimientosCols = this.detalleMovimientos[0] ? Object.keys(this.detalleMovimientos[0]) : [];
          this.detalleServiciosCols = this.detalleServicios[0] ? Object.keys(this.detalleServicios[0]) : [];
        },
        error: err => {
          this.detalleErrorMsg = err?.error || 'No se pudo consultar el detalle de esta solicitud.';
        }
      });
  }

  closeDetalleModal(): void {
    this.detalleSub?.unsubscribe();
    this.modalSolicitudRow = null;
    this.detalleLoading = false;
    this.detalleErrorMsg = '';
    this.detalleMovimientos = [];
    this.detalleServicios = [];
    this.detalleMovimientosCols = [];
    this.detalleServiciosCols = [];
  }

  private destroySicossCharts(): void {
    if (this.sicossChart) { this.sicossChart.destroy(); this.sicossChart = null; }
    if (this.sicossStatusChart) { this.sicossStatusChart.destroy(); this.sicossStatusChart = null; }
    if (this.sicossTipoChart) { this.sicossTipoChart.destroy(); this.sicossTipoChart = null; }
  }

  sicossOnZonaChange(zona: string): void {
    this.sicossZona = zona;
    this.sicossCen = '';
    this.sicossCentros = [];
    this.sicossData = [];
    this.sicossPorStatus = [];
    this.sicossPorTipo = [];
    this.sicossStatus = 'WAITING';
    this.destroySicossCharts();
    if (!zona) return;

    this.dashboardService.getSicossCentros(zona).subscribe({
      next: centros => { this.sicossCentros = centros ?? []; },
      error: () => { this.sicossCentros = []; }
    });
  }

  sicossGenerar(): void {
    if (!this.sicossZona || !this.sicossCen) return;

    this.sicossStatus = 'LOADING';
    this.sicossErrorMsg = '';
    this.dashboardService.getSicossPendientes(this.sicossZona, this.sicossCen)
      .pipe(timeout({ each: SCRAPE_TIMEOUT_MS }))
      .subscribe({
        next: data => {
          this.sicossData = data ?? [];
          this.sicossPorStatus = this.computeSicossPorStatus();
          this.sicossPorTipo = this.computeSicossPorTipo();
          this.sicossStatus = 'SUCCESS';
          this.renderSicossChart();
          this.renderSicossStatusChart();
          this.renderSicossTipoChart();
        },
        error: err => {
          this.sicossStatus = 'ERROR';
          this.sicossErrorMsg = err?.error || 'No se pudo consultar SICOSS Distribución.';
        }
      });
  }

  // El texto real que manda el portal es "1 - Pendiente", "2 - EnAtencion",
  // "W - PreAsignada" — clasificamos por el primer caracter (el código), no por el
  // texto completo, para no depender de espacios/mayúsculas exactas.
  private sicossStatusLabel(raw: string): 'Pendientes' | 'En atención' | 'Por asignar' | 'Otro' {
    const c = (raw || '').trim().charAt(0).toUpperCase();
    if (c === '1') return 'Pendientes';
    if (c === '2') return 'En atención';
    if (c === 'W') return 'Por asignar';
    return 'Otro';
  }

  private computeSicossPorStatus(): { status: string; count: number }[] {
    const counts = new Map<string, number>();
    for (const row of this.sicossData) {
      const label = this.sicossStatusLabel(row['Status']);
      counts.set(label, (counts.get(label) ?? 0) + 1);
    }
    const order = ['Pendientes', 'En atención', 'Por asignar', 'Otro'];
    return order
      .filter(s => counts.has(s))
      .map(status => ({ status, count: counts.get(status)! }));
  }

  private computeSicossPorTipo(): { tipo: string; pendientes: number; enAtencion: number; porAsignar: number; total: number }[] {
    const map = new Map<string, { pendientes: number; enAtencion: number; porAsignar: number }>();
    for (const row of this.sicossData) {
      const tipo = row['Tipo'] || '';
      if (!map.has(tipo)) map.set(tipo, { pendientes: 0, enAtencion: 0, porAsignar: 0 });
      const entry = map.get(tipo)!;
      const label = this.sicossStatusLabel(row['Status']);
      if (label === 'Pendientes') entry.pendientes++;
      else if (label === 'En atención') entry.enAtencion++;
      else if (label === 'Por asignar') entry.porAsignar++;
    }
    return Array.from(map.entries())
      .map(([tipo, v]) => ({ tipo, ...v, total: v.pendientes + v.enAtencion + v.porAsignar }))
      .sort((a, b) => b.total - a.total);
  }

  private renderSicossChart(): void {
    const counts = new Map<string, number>();
    for (const row of this.sicossData) {
      const tipo = row['Tipo'] || '';
      counts.set(tipo, (counts.get(tipo) ?? 0) + 1);
    }
    const labels = Array.from(counts.keys()).sort((a, b) => counts.get(b)! - counts.get(a)!);
    const data = labels.map(l => counts.get(l)!);

    setTimeout(() => {
      if (this.sicossChart) { this.sicossChart.destroy(); this.sicossChart = null; }
      const canvas = document.getElementById('sicossChart');
      if (!canvas || labels.length === 0) return;

      this.sicossChart = new Chart('sicossChart', {
        type: 'bar',
        data: {
          labels,
          datasets: [{
            label: 'Solicitudes',
            data,
            backgroundColor: 'rgba(22,163,74,0.75)',
            borderColor: 'rgb(21,128,61)',
            borderWidth: 1
          }]
        },
        options: {
          responsive: true,
          maintainAspectRatio: false,
          plugins: { legend: { display: false } },
          scales: { y: { beginAtZero: true, ticks: { precision: 0 } } }
        },
        plugins: [BAR_DATALABELS_PLUGIN]
      } as any);
    }, 0);
  }

  private renderSicossStatusChart(): void {
    setTimeout(() => {
      if (this.sicossStatusChart) { this.sicossStatusChart.destroy(); this.sicossStatusChart = null; }
      const canvas = document.getElementById('sicossStatusChart');
      if (!canvas || this.sicossPorStatus.length === 0) return;

      const colors: { [k: string]: string } = {
        'Pendientes': 'rgba(251,191,36,0.85)',
        'En atención': 'rgba(22,163,74,0.80)',
        'Por asignar': 'rgba(156,163,175,0.80)',
        'Otro': 'rgba(108,117,125,0.60)',
      };

      this.sicossStatusChart = new Chart('sicossStatusChart', {
        type: 'bar',
        data: {
          labels: this.sicossPorStatus.map(s => s.status),
          datasets: [{
            label: 'Solicitudes',
            data: this.sicossPorStatus.map(s => s.count),
            backgroundColor: this.sicossPorStatus.map(s => colors[s.status] ?? 'rgba(108,117,125,0.6)'),
            borderWidth: 1
          }]
        },
        options: {
          responsive: true,
          maintainAspectRatio: false,
          plugins: { legend: { display: false } },
          scales: { y: { beginAtZero: true, ticks: { precision: 0 } } }
        },
        plugins: [BAR_DATALABELS_PLUGIN]
      } as any);
    }, 0);
  }

  private renderSicossTipoChart(): void {
    setTimeout(() => {
      if (this.sicossTipoChart) { this.sicossTipoChart.destroy(); this.sicossTipoChart = null; }
      const canvas = document.getElementById('sicossTipoChart');
      if (!canvas || this.sicossPorTipo.length === 0) return;

      this.sicossTipoChart = new Chart('sicossTipoChart', {
        type: 'bar',
        data: {
          labels: this.sicossPorTipo.map(t => t.tipo),
          datasets: [
            { label: 'Pendientes',   data: this.sicossPorTipo.map(t => t.pendientes), backgroundColor: 'rgba(251,191,36,0.85)' },
            { label: 'En atención',  data: this.sicossPorTipo.map(t => t.enAtencion), backgroundColor: 'rgba(22,163,74,0.80)' },
            { label: 'Por asignar',  data: this.sicossPorTipo.map(t => t.porAsignar), backgroundColor: 'rgba(156,163,175,0.80)' },
          ]
        },
        options: {
          responsive: true,
          maintainAspectRatio: false,
          plugins: { legend: { position: 'top' as const } },
          scales: {
            x: { stacked: false },
            y: { beginAtZero: true, ticks: { precision: 0 } }
          }
        },
        plugins: [BAR_DATALABELS_PLUGIN]
      } as any);
    }, 0);
  }

  // ── Exportar ──────────────────────────────────────────────────────────────────

  /** Exporta cualquier gráfica (por id de canvas) a PNG con fondo blanco — Chart.js
   * deja el fondo transparente por defecto y se ve negro al abrir el PNG. */
  exportChartImage(canvasId: string, filename: string): void {
    const canvas = document.getElementById(canvasId) as HTMLCanvasElement | null;
    if (!canvas) return;
    const off = document.createElement('canvas');
    off.width = canvas.width;
    off.height = canvas.height;
    const offCtx = off.getContext('2d')!;
    offCtx.fillStyle = '#ffffff';
    offCtx.fillRect(0, 0, off.width, off.height);
    offCtx.drawImage(canvas, 0, 0);
    const link = document.createElement('a');
    link.href = off.toDataURL('image/png');
    link.download = filename;
    link.click();
  }

  exportTableExcel(rows: any[] | null | undefined, filename: string): void {
    if (!rows || rows.length === 0) return;
    const ws = XLSX.utils.json_to_sheet(rows);
    const wb = XLSX.utils.book_new();
    XLSX.utils.book_append_sheet(wb, ws, 'Datos');
    saveWorkbook(wb, filename);
  }
}
