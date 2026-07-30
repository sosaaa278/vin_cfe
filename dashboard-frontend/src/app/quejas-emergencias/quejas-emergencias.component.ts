import { Component, OnDestroy, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import Chart from 'chart.js/auto';
import * as XLSX from 'xlsx-js-style';
import { DashboardService, QuejasEmergenciasResponse } from '../services/dashboard.service';
import { AuthService } from '../services/auth.service';
import { DateRangeService } from '../services/date-range.service';
import { NavComponent } from '../shared/nav.component';
import { DateRangeBarComponent } from '../shared/date-range-bar.component';
import { saveWorkbook } from '../shared/excel-export';
import { Subscription } from 'rxjs';
import { timeout } from 'rxjs/operators';

// sisquem consulta en vivo otro sistema interno (SICOSS) y el propio portal advierte
// que puede tardar varios minutos — igual que SCRAPE_TIMEOUT_MS en colonias/causas,
// pero más generoso porque aquí el backend ya espera hasta 5 min por su cuenta.
const SCRAPE_TIMEOUT_MS = 300_000;

// Los 16 códigos de "Tipos de orden" que expone el portal sisquem. Son fijos (no
// vienen de un endpoint) — por defecto todos seleccionados, igual que el estado
// inicial real del multi-select del portal.
const TIPOS_ORDEN = ['E01', 'E02', 'E03', 'E04', 'E05', 'E06', 'E07',
                      'Q01', 'Q02', 'Q03', 'Q04', 'Q06', 'Q07', 'Q08', 'QC2', 'QC7'];

// Colores de línea: Emergencias (azul), Quejas (naranja) para las 3 primeras gráficas;
// Generadas/Pendientes/Atendidas (naranja/azul marino/verde) para "Totales", igual a la
// combinación de colores que usa el propio sisquem en su gráfica "Totales por hora".
const LINE_COLORS = {
  emergencias: { border: 'rgb(30,58,138)',  bg: 'rgba(30,58,138,0.12)' },
  quejas:      { border: 'rgb(234,88,12)',  bg: 'rgba(234,88,12,0.12)' },
  generadas:   { border: 'rgb(234,140,20)', bg: 'rgba(234,140,20,0.12)' },
  pendientes:  { border: 'rgb(20,30,70)',   bg: 'rgba(20,30,70,0.12)' },
  atendidas:   { border: 'rgb(22,163,74)',  bg: 'rgba(22,163,74,0.12)' },
};

interface ChartDef {
  id: string;
  titulo: string;
}

// Vista "Quejas y Emergencias" — monitoreo del sistema sisquem (distinto de
// cssnal.cfe.mx: es JSON puro, sin Playwright del lado del backend). A diferencia
// de las demás vistas, aquí Zona(s) y Tipos de orden son selección MÚLTIPLE real.
@Component({
  selector: 'app-quejas-emergencias',
  standalone: true,
  imports: [CommonModule, FormsModule, NavComponent, DateRangeBarComponent],
  templateUrl: './quejas-emergencias.component.html',
  styleUrls: ['./quejas-emergencias.component.css']
})
export class QuejasEmergenciasComponent implements OnInit, OnDestroy {

  status: 'WAITING' | 'LOADING' | 'SUCCESS' | 'ERROR' = 'WAITING';
  errorMsg = '';

  readonly TIPOS_ORDEN = TIPOS_ORDEN;

  zonas: { value: string; label: string }[] = [];
  zonasLoading = false;

  // Zona: selección ÚNICA (dropdown normal, igual que causas/imu/colonias) — a
  // diferencia de Tipos de orden, el usuario pidió que aquí solo se pueda elegir una
  // zona a la vez. "00000" = todas las zonas (mismo convenio que el resto de la app).
  selectedZona = '00000';
  // Tipos de orden: selección MÚLTIPLE real (checklist). Todos por defecto.
  selectedTipos: string[] = [...TIPOS_ORDEN];

  data: QuejasEmergenciasResponse | null = null;
  integridadOk = true;

  resumenEmergenciasCols: string[] = [];
  resumenQuejasCols: string[] = [];
  detalleEmergenciasCols: string[] = [];
  detalleQuejasCols: string[] = [];
  listadoCols: string[] = [];
  resumenEstadoEmergenciasCols: string[] = [];
  resumenMunicipioEmergenciasCols: string[] = [];
  resumenEstadoQuejasCols: string[] = [];
  resumenMunicipioQuejasCols: string[] = [];

  // Conteo de solicitudes pendientes por tipo de orden (E01, E02, ... ), derivado del
  // listado (columna "Tipo") en vez de pedir un endpoint nuevo — ya trae el código exacto
  // por fila. Solo se muestran los tipos actualmente seleccionados, en el orden fijo de
  // TIPOS_ORDEN (no el orden en que el usuario los fue marcando).
  kpiTipos: { tipo: string; count: number }[] = [];

  private charts: { [id: string]: any } = {};

  private readonly CHART_DEFS: ChartDef[] = [
    { id: 'qeChartPendientes', titulo: 'Pendientes' },
    { id: 'qeChartAtendidas',  titulo: 'Atendidas' },
    { id: 'qeChartGeneradas',  titulo: 'Generadas' },
    { id: 'qeChartTotales',    titulo: 'Totales por hora (Emergencias + Quejas)' },
  ];
  get chartDefs(): ChartDef[] { return this.CHART_DEFS; }

  private rangeSub?: Subscription;

  constructor(
    private dashboardService: DashboardService,
    private auth: AuthService,
    private dateRange: DateRangeService
  ) {
    this.zonas = this.auth.getZonas();

    let first = true;
    this.rangeSub = this.dateRange.range$.subscribe(() => {
      if (first) { first = false; return; }
      if (this.data) this.consultar();
    });
  }

  ngOnInit(): void {
    this.zonasLoading = true;
    this.dashboardService.getZonas().subscribe({
      next: zonas => {
        if (zonas?.length > 1) this.zonas = zonas;
        this.zonasLoading = false;
      },
      error: () => { this.zonasLoading = false; }
    });
  }

  ngOnDestroy(): void {
    this.rangeSub?.unsubscribe();
    Object.values(this.charts).forEach(c => c?.destroy());
  }

  toggleTipo(t: string): void {
    const i = this.selectedTipos.indexOf(t);
    if (i >= 0) this.selectedTipos.splice(i, 1);
    else this.selectedTipos.push(t);
  }

  seleccionarTodosTipos(): void {
    this.selectedTipos = [...TIPOS_ORDEN];
  }

  limpiarTipos(): void {
    this.selectedTipos = [];
  }

  consultar(): void {
    this.status = 'LOADING';
    this.errorMsg = '';

    const { desde, hasta } = this.dateRange.current;
    const zonas = this.selectedZona === '00000' ? [] : [this.selectedZona];
    this.dashboardService.getQuejasEmergencias(zonas, this.selectedTipos, desde, hasta)
      .pipe(timeout({ each: SCRAPE_TIMEOUT_MS }))
      .subscribe({
      next: resp => {
        // Igual que el propio portal (tablesorter sortList: [[6,1]] = columna Horas,
        // descendente) — el HTML crudo no viene garantizado en ese orden porque ese sort
        // lo aplica el JS del portal después de renderizar, no el servidor.
        if (resp?.listado?.length) {
          resp.listado = [...resp.listado].sort(
            (a, b) => (parseFloat(b['Horas']) || 0) - (parseFloat(a['Horas']) || 0)
          );
        }
        this.data = resp;
        this.integridadOk = resp?.integridadOk ?? true;
        this.resumenEmergenciasCols = this.colsOf(resp?.resumenEmergencias);
        this.resumenQuejasCols      = this.colsOf(resp?.resumenQuejas);
        this.detalleEmergenciasCols = this.colsOf(resp?.detalleEmergencias, true);
        this.detalleQuejasCols      = this.colsOf(resp?.detalleQuejas, true);
        this.listadoCols            = this.colsOf(resp?.listado);
        this.resumenEstadoEmergenciasCols    = this.colsOf(resp?.resumenEstadoEmergencias);
        this.resumenMunicipioEmergenciasCols = this.colsOf(resp?.resumenMunicipioEmergencias);
        this.resumenEstadoQuejasCols         = this.colsOf(resp?.resumenEstadoQuejas);
        this.resumenMunicipioQuejasCols      = this.colsOf(resp?.resumenMunicipioQuejas);
        this.kpiTipos = this.computeKpiTipos(resp?.listado);
        this.status = 'SUCCESS';
        this.renderCharts();
      },
      error: err => this.fail(err)
    });
  }

  // Columnas dinámicas a partir de la primera fila — los nombres exactos de columna
  // dependen de cómo sisquem componga el HTML de cada tabla (no confirmados al 100%
  // hasta la primera prueba real). Con excludeColorCols=true se omiten los campos
  // companion "<col>_color" (son solo para pintar celdas, no columnas visibles).
  private colsOf(rows: any[] | undefined, excludeColorCols = false): string[] {
    if (!rows || rows.length === 0) return [];
    const keys = Object.keys(rows[0]);
    return excludeColorCols ? keys.filter(k => !k.endsWith('_color')) : keys;
  }

  cellColor(row: any, col: string): string | null {
    return row?.[`${col}_color`] ?? null;
  }

  // Las tablas resumen (Zona/Estado/Municipio) ahora traen una fila final "Total" (viene
  // del <tfoot> real de sisquem) — se resalta igual que la fila Total del dashboard
  // principal, en vez de verse como una zona/estado más.
  isRowTotal(row: any, cols: string[]): boolean {
    const first = cols[0];
    return /^total$/i.test(String(row?.[first] ?? '').trim());
  }

  private computeKpiTipos(listado: any[] | undefined): { tipo: string; count: number }[] {
    const rows = listado ?? [];
    return this.selectedTipos
      .slice()
      .sort((a, b) => TIPOS_ORDEN.indexOf(a) - TIPOS_ORDEN.indexOf(b))
      .map(tipo => ({ tipo, count: rows.filter(r => r['Tipo'] === tipo).length }));
  }

  private fail(err: any): void {
    this.errorMsg = err?.name === 'TimeoutError'
      ? 'La consulta tardó demasiado (sisquem no respondió a tiempo). Vuelve a intentarlo.'
      : typeof err?.error === 'string'
        ? err.error
        : 'No se pudo cargar el reporte de Quejas y Emergencias. Verifica la conexión/VPN a la red de CFE.';
    this.status = 'ERROR';
    this.data = null;
  }

  // ── Gráficas ──────────────────────────────────────────────────────────────────

  private renderCharts(): void {
    const g = this.data?.graficas;
    if (!g || g.ejeX.length === 0) return;

    setTimeout(() => {
      this.destroyCharts();

      // "Pendientes" es una FOTO del backlog en ese momento (no un conteo de eventos) —
      // sumar sus horas dentro de un día inflaría el número (el mismo pendiente contaría
      // varias veces). Se agrupa por día tomando el ÚLTIMO valor del día, no la suma.
      const pend = this.prepareSeries(g.ejeX, [g.emergenciasPendientes, g.quejasPendientes], ['last', 'last']);
      this.buildLineChart('qeChartPendientes', 'Pendientes', pend.labels, [
        { label: 'Emergencias', data: pend.series[0], color: LINE_COLORS.emergencias },
        { label: 'Quejas',      data: pend.series[1], color: LINE_COLORS.quejas },
      ]);

      const aten = this.prepareSeries(g.ejeX, [g.emergenciasAtendidas, g.quejasAtendidas]);
      this.buildLineChart('qeChartAtendidas', 'Atendidas', aten.labels, [
        { label: 'Emergencias', data: aten.series[0], color: LINE_COLORS.emergencias },
        { label: 'Quejas',      data: aten.series[1], color: LINE_COLORS.quejas },
      ]);

      const gen = this.prepareSeries(g.ejeX, [g.emergenciasGeneradas, g.quejasGeneradas]);
      this.buildLineChart('qeChartGeneradas', 'Generadas', gen.labels, [
        { label: 'Emergencias', data: gen.series[0], color: LINE_COLORS.emergencias },
        { label: 'Quejas',      data: gen.series[1], color: LINE_COLORS.quejas },
      ]);

      // "Totales por hora (Emergencias + Quejas)" — 3 series (Generadas/Pendientes/
      // Atendidas), cada una sumando Emergencias + Quejas de esa métrica, agrupadas por
      // día igual que las otras 3 gráficas (Pendientes con 'last', las demás con 'sum').
      const totalesGeneradas  = g.emergenciasGeneradas.map((v, i) => v + (g.quejasGeneradas[i] ?? 0));
      const totalesPendientes = g.emergenciasPendientes.map((v, i) => v + (g.quejasPendientes[i] ?? 0));
      const totalesAtendidas  = g.emergenciasAtendidas.map((v, i) => v + (g.quejasAtendidas[i] ?? 0));
      const tot = this.prepareSeries(g.ejeX, [totalesGeneradas, totalesPendientes, totalesAtendidas], ['sum', 'last', 'sum']);
      this.buildLineChart('qeChartTotales', 'Totales por hora (Emergencias + Quejas)', tot.labels, [
        { label: 'Generadas',  data: tot.series[0], color: LINE_COLORS.generadas },
        { label: 'Pendientes', data: tot.series[1], color: LINE_COLORS.pendientes },
        { label: 'Atendidas',  data: tot.series[2], color: LINE_COLORS.atendidas },
      ]);
    }, 0);
  }

  // Con rangos largos, sisquem devuelve datos POR HORA (ej. 6 meses = miles de puntos) —
  // ilegible en una gráfica de línea. Si hay más de ~2 días de puntos se agrupan por
  // fecha (YYYY-MM-DD). modes[i]='sum' para conteos de eventos, 'last' para fotos de
  // backlog (Pendientes) — por defecto 'sum' si no se especifica para esa serie.
  private prepareSeries(
    ejeX: string[], series: number[][], modes: ('sum' | 'last')[] = []
  ): { labels: string[]; series: number[][] } {
    if (ejeX.length <= 48) return { labels: ejeX, series };

    const dayIndex = new Map<string, number>();
    const labels: string[] = [];
    const acc: number[][] = series.map(() => []);

    ejeX.forEach((ts, i) => {
      const day = ts.slice(0, 10);
      let idx = dayIndex.get(day);
      if (idx === undefined) {
        idx = labels.length;
        dayIndex.set(day, idx);
        labels.push(day);
        acc.forEach(arr => arr.push(0));
      }
      series.forEach((s, si) => {
        const v = s[i] ?? 0;
        const mode = modes[si] ?? 'sum';
        acc[si][idx] = mode === 'sum' ? acc[si][idx] + v : v;
      });
    });

    return { labels, series: acc };
  }

  private buildLineChart(
    canvasId: string, titulo: string, labels: string[],
    series: { label: string; data: number[]; color: { border: string; bg: string } }[]
  ): void {
    const canvas = document.getElementById(canvasId) as HTMLCanvasElement | null;
    if (!canvas) return;

    // Rangos largos traen miles de puntos por hora — sin puntos visibles (solo la línea)
    // y sin animación se mantiene fluido; el hover sigue funcionando por pointHitRadius.
    const dense = labels.length > 300;

    this.charts[canvasId] = new Chart(canvasId, {
      type: 'line',
      data: {
        labels,
        datasets: series.map(s => ({
          label: s.label,
          data: s.data,
          borderColor: s.color.border,
          backgroundColor: s.color.bg,
          fill: true,
          tension: 0.2,
          borderWidth: dense ? 1.25 : 2,
          pointRadius: dense ? 0 : 2,
          pointHitRadius: 6,
        }))
      },
      options: {
        responsive: true,
        maintainAspectRatio: false,
        animation: dense ? false : undefined,
        interaction: { mode: 'index' as const, intersect: false },
        plugins: {
          title: { display: true, text: titulo, font: { size: 12, weight: 'bold' } },
          legend: { position: 'top' as const, labels: { usePointStyle: true, padding: 8, boxWidth: 12, font: { size: 11 } } }
        },
        scales: {
          x: { ticks: { maxRotation: 60, minRotation: 30, font: { size: 8 }, autoSkip: true, maxTicksLimit: 40 }, grid: { display: false } },
          y: { beginAtZero: true, ticks: { font: { size: 10 } }, grid: { color: 'rgba(0,0,0,0.06)' } }
        }
      } as any
    });
  }

  private destroyCharts(): void {
    Object.values(this.charts).forEach(c => c?.destroy());
    this.charts = {};
  }

  // ── Exportar ──────────────────────────────────────────────────────────────────

  /** Exporta cualquiera de las tablas del reporte a un .xlsx — usado por el botón "Excel"
   * de cada tarjeta. Quita las columnas companion "<col>_color" (solo sirven para pintar
   * celdas en pantalla, no son datos que el usuario quiera ver en el archivo). */
  exportTableExcel(rows: any[] | null | undefined, filename: string): void {
    if (!rows || rows.length === 0) return;
    const clean = rows.map(row => {
      const out: any = {};
      Object.keys(row).forEach(k => { if (!k.endsWith('_color')) out[k] = row[k]; });
      return out;
    });
    const ws = XLSX.utils.json_to_sheet(clean);
    const wb = XLSX.utils.book_new();
    XLSX.utils.book_append_sheet(wb, ws, 'Datos');
    saveWorkbook(wb, filename);
  }
}
