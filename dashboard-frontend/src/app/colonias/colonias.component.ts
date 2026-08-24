import { ChangeDetectorRef, Component, OnDestroy, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import Chart from 'chart.js/auto';
import * as XLSX from 'xlsx-js-style';
import { timeout, finalize } from 'rxjs/operators';
import { DashboardService } from '../services/dashboard.service';
import { AuthService } from '../services/auth.service';
import { DateRangeService } from '../services/date-range.service';
import { NavComponent } from '../shared/nav.component';
import { DateRangeBarComponent } from '../shared/date-range-bar.component';
import { saveWorkbook } from '../shared/excel-export';
import { Subscription } from 'rxjs';

// Tope de espera para el scraping en vivo (igual que en causas).
const SCRAPE_TIMEOUT_MS = 240_000;

// ── Plugin: etiqueta de valor sobre cada barra (mismo patrón que causas/imu) ────
const BAR_DATALABELS_PLUGIN: any = {
  id: 'coloniasBarLabels',
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
        ctx.textAlign = 'center'; ctx.textBaseline = 'bottom';
        ctx.fillText(txt, el.x, el.y - 2);
        ctx.restore();
      });
    });
  }
};

// ── Plugin: columna de fondo verde/roja por colonia (modo comparación, igual que causas) ──
const COMPARE_BG_COLUMNS_PLUGIN: any = {
  id: 'coloniasCompareBgColumns',
  beforeDatasetsDraw(chart: any) {
    if (chart.config.type !== 'bar') return;
    const { ctx, chartArea } = chart;
    if (!chartArea) return;
    const colors: string[] = chart.options?.plugins?.bgColumns?.colors ?? [];
    if (!colors.length) return;
    const metas = (chart.data.datasets as any[])
      .map((_: any, di: number) => chart.getDatasetMeta(di))
      .filter((m: any) => !m.hidden);
    (chart.data.labels as any[]).forEach((_: any, idx: number) => {
      const color = colors[idx];
      if (!color) return;
      const elements: any[] = metas.map((m: any) => m.data[idx]).filter(Boolean);
      if (!elements.length) return;
      ctx.save();
      ctx.fillStyle = color;
      const left  = Math.min(...elements.map((e: any) => e.x - e.width  / 2));
      const right = Math.max(...elements.map((e: any) => e.x + e.width  / 2));
      ctx.fillRect(left, chartArea.top, right - left, chartArea.bottom - chartArea.top);
      ctx.restore();
    });
  }
};

// Colores aproximando la leyenda del portal CFE (Recibidas/Terminadas/Canceladas/Rechazadas/Pendientes).
const SERIES_COLORS: { [k: string]: { bg: string; border: string } } = {
  recibidas:  { bg: 'rgba(156,163,175,0.65)', border: 'rgb(107,114,128)' },  // gris
  terminadas: { bg: 'rgba(22,163,74,0.75)',   border: 'rgb(21,128,61)' },    // verde
  canceladas: { bg: 'rgba(217,70,239,0.70)',  border: 'rgb(162,28,175)' },   // magenta
  rechazadas: { bg: 'rgba(220,53,69,0.75)',   border: 'rgb(176,42,55)' },    // rojo
  pendientes: { bg: 'rgba(251,191,36,0.80)',  border: 'rgb(217,119,6)' },   // amarillo/oro
};

// Vista "Colonias" — detalle de solicitudes de servicio agrupado por colonia.
// Igual que IMU, es manual: el usuario elige Zona/Área/fechas y pulsa "Consultar".
// El agrupador del portal siempre se manda fijo en "Colonia" (no es elegible aquí).
@Component({
  selector: 'app-colonias',
  standalone: true,
  imports: [CommonModule, FormsModule, NavComponent, DateRangeBarComponent],
  templateUrl: './colonias.component.html',
  styleUrls: ['./colonias.component.css']
})
export class ColoniasComponent implements OnInit, OnDestroy {

  status: 'WAITING' | 'LOADING' | 'SUCCESS' | 'ERROR' = 'WAITING';
  errorMsg = '';

  columns: string[] = [];
  tableData: any[] = [];
  orderedCols: (string | undefined)[] = [];
  totalRow: { [k: string]: string } = {};

  // Con "Todas las áreas" el portal puede regresar miles de colonias — renderizar esa
  // cantidad de <tr> de un jalón congela el navegador (no es una excepción, es
  // simplemente demasiado DOM síncrono). tableData se queda completo (para el total y
  // el export a Excel), pero la tabla en pantalla solo pinta las primeras N (ya vienen
  // ordenadas por Recibidas descendente, así que son las más relevantes).
  readonly MAX_FILAS_VISIBLES = 300;
  get visibleTableData(): any[] {
    return this.tableData.slice(0, this.MAX_FILAS_VISIBLES);
  }

  chart: any = null; // misma gráfica/canvas para modo normal y modo comparación

  // ── Modal "desglose por colonia" (clic en una barra de la gráfica) ─────────────
  readonly INCONFORMIDAD_CODES = [
    { value: 'E01', label: 'Circuito fuera' },
    { value: 'E02', label: 'Ramal fuera' },
    { value: 'E03', label: 'Sector fuera' },
    { value: 'E04', label: 'Falso contacto de distribución' },
    { value: 'E05', label: 'Improcedente distribución' },
    { value: 'E06', label: 'Servicio importante fuera' },
    { value: 'E07', label: 'Reparación mayor' },
    { value: 'Q01', label: 'No luz' },
    { value: 'Q02', label: 'Falso contacto' },
    { value: 'Q03', label: 'Acometida averiada' },
    { value: 'Q04', label: 'Falla medidor' },
    { value: 'Q06', label: 'Improcedente medición' },
    { value: 'Q07', label: 'Deficiencia de voltaje' },
    { value: 'Q08', label: 'Medidor robado' },
    { value: 'QC2', label: 'Corte indebido' },
    { value: 'QC7', label: 'Reconexión tardía' },
  ];
  modalColoniaRow: any = null;       // fila clickeada; null = modal cerrado
  loadingInconformidades = false;
  inconformidadesErrorMsg = '';
  inconformidadesPorColonia: any[] | null = null; // caché de la respuesta completa (por consulta)
  private modalChart: any = null;

  // Caché de findModalRow()/sortedModalCodes para la colonia actualmente abierta en el
  // modal. ANTES estos se recalculaban en getters llamados desde el template —
  // sortedModalCodes llama a findModalRow() (un .find() sobre TODA
  // inconformidadesPorColonia, que con "Todas las áreas" puede tener miles de filas), y
  // getModalRowValue() lo vuelve a llamar UNA VEZ POR CADA una de las 16 filas de la
  // tabla del modal — o sea 17 recorridos completos del arreglo en CADA ciclo de
  // detección de cambios de Angular (que puede dispararse muy seguido, ej. por los
  // frames de animación de Chart.js). Con un dataset grande eso congelaba la pestaña
  // entera. Ahora se calcula UNA sola vez por clic de colonia y se reusa.
  private modalRowCache: any | undefined;
  sortedModalCodesCache: { value: string; label: string }[] = [];

  // ── Filtro por métrica dentro del modal (Recibidas/Rechazadas/Pendientes/Cumplidas) ──
  // El backend ahora guarda las 4 métricas por código (antes solo Recibidas) — ver
  // MergeRows en GetColoniaInconformidadesAsync. "Otras" es lo que le falta a la suma de
  // los 16 códigos rastreados para cuadrar con el total real de la columna correspondiente
  // en la tabla principal (el portal tiene más tipos de inconformidad de los 16 que este
  // modal desglosa uno por uno).
  readonly METRIC_OPTIONS: { value: 'recibidas' | 'rechazadas' | 'pendientes' | 'cumplidas'; label: string }[] = [
    { value: 'recibidas',  label: 'Recibidas' },
    { value: 'rechazadas', label: 'Rechazadas' },
    { value: 'pendientes', label: 'Pendientes' },
    { value: 'cumplidas',  label: 'Cumplidas' },
  ];
  selectedMetric: 'recibidas' | 'rechazadas' | 'pendientes' | 'cumplidas' = 'recibidas';
  private otrasValue = 0;

  // ── Detalle en vivo de UNA inconformidad, dentro del modal (clic en E02..QC7) ──
  // Aparece debajo de la gráfica/resumen sin quitarlos — tabla completa (todas las
  // columnas) scrapeada en el momento, filtrada a ese código, agrupada por colonia
  // (misma Zona/Área/rango de fechas ya seleccionados arriba en el apartado).
  modalDetalleCodigo: string | null = null;
  modalDetalleLoading = false;
  modalDetalleErrorMsg = '';
  // El portal no permite filtrar a UNA colonia — se scrapea la tabla completa (todas
  // las colonias) para el código clickeado y se queda solo la fila que coincide con
  // la colonia ya abierta en el modal (mismo criterio que findModalRow()).
  modalDetalleRow: any | null = null;
  modalDetalleCols: string[] = [];
  // Cada clic en un código lanza un scrape en vivo que abre un navegador Chromium nuevo
  // en el backend (ver GetColoniasPorInconformidadAsync) y puede tardar varios minutos
  // con "Todas las áreas". Sin esta referencia, clics repetidos/otro código mientras uno
  // sigue en curso apilaban varios Chromium corriendo en paralelo en el servidor — eso
  // es lo que se sentía como "se traba" (consumo de RAM/CPU del servidor, no del navegador).
  private modalDetalleSub?: Subscription;

  // ── Comparación con el año anterior (igual patrón que causas.component.ts) ──
  prevData: any[] = [];   // año actual - 1
  compareYear = 0;        // 0 = sin comparación
  loadingOffset = 0;       // 1 = comparación cargando
  compareRows: { Clave: string; Descripcion: string; Anterior: number; Actual: number; Variacion: number }[] = [];

  get isCompareMode(): boolean {
    return this.compareYear > 0 && this.prevData.length > 0;
  }
  get compareTotals(): { anterior: number; actual: number; variacion: number } {
    const anterior = this.compareRows.reduce((s, r) => s + r.Anterior, 0);
    const actual   = this.compareRows.reduce((s, r) => s + r.Actual, 0);
    const variacion = anterior > 0
      ? Math.round(((actual - anterior) / anterior) * 10000) / 100
      : (actual > 0 ? actual * 100 : 0);
    return { anterior, actual, variacion };
  }

  // El año "actual" sale de la fecha "hasta" del rango elegido (igual que causas/imu) —
  // así el programa sigue funcionando en años futuros sin tocar código.
  get currentYear(): number {
    return Number(this.dateRange.current.hasta.slice(0, 4)) || new Date().getFullYear();
  }

  // Orden fijo de columnas, calcado del encabezado real del portal (Sec, Clave,
  // Descripcion, Recibidas, Rechazadas[4], Canceladas[2], Terminadas[4], Pendientes[5],
  // Con optico, Reiterativas[2]). Cada entrada se resuelve contra los nombres de columna
  // reales (compuestos por el parser del backend) por coincidencia difusa.
  private readonly LEAF_DEFS: { kind: 'text' | 'count' | 'pct'; label: string; match: (u: string) => boolean; pctSourceMatch?: (u: string) => boolean }[] = [
    { kind: 'text',  label: 'Sec',                      match: u => u === 'SEC' },
    { kind: 'text',  label: 'Clave',                     match: u => u.includes('CLAVE') },
    { kind: 'text',  label: 'Descripción',                match: u => u.includes('DESCRIP') },
    { kind: 'count', label: 'Recibidas',                  match: u => u.includes('RECIBIDA') },
    { kind: 'count', label: 'Rechazadas — Total',          match: u => u.includes('RECHAZADA') && u.includes('TOTAL') },
    { kind: 'count', label: 'Rechazadas — Vencidas',       match: u => u.includes('RECHAZADA') && u.includes('VENCIDA') },
    { kind: 'count', label: 'Rechazadas — En tiempo',      match: u => u.includes('RECHAZADA') && u.includes('TIEMPO') },
    { kind: 'pct',   label: 'Rechazadas — %',              match: u => u.includes('RECHAZADA') && u.includes('%'), pctSourceMatch: u => u.includes('RECHAZADA') && u.includes('TOTAL') },
    { kind: 'count', label: 'Canceladas — Total',          match: u => u.includes('CANCELADA') && u.includes('TOTAL') },
    { kind: 'pct',   label: 'Canceladas — %',              match: u => u.includes('CANCELADA') && u.includes('%'), pctSourceMatch: u => u.includes('CANCELADA') && u.includes('TOTAL') },
    { kind: 'count', label: 'Terminadas — Total',          match: u => u.includes('TERMINADA') && u.includes('TOTAL') },
    { kind: 'count', label: 'Terminadas — No cumplidas',   match: u => u.includes('TERMINADA') && u.includes('NO CUMPLID') },
    { kind: 'count', label: 'Terminadas — Cumplidas',      match: u => u.includes('TERMINADA') && u.includes('CUMPLID') && !u.includes('NO CUMPLID') },
    { kind: 'pct',   label: 'Terminadas — %',              match: u => u.includes('TERMINADA') && u.includes('%'), pctSourceMatch: u => u.includes('TERMINADA') && u.includes('TOTAL') },
    { kind: 'count', label: 'Pendientes — Total',          match: u => u.includes('PENDIENTE') && u.includes('TOTAL') },
    { kind: 'count', label: 'Pendientes — Vencidas',       match: u => u.includes('PENDIENTE') && u.includes('VENCIDA') },
    { kind: 'count', label: 'Pendientes — Por vencer',     match: u => u.includes('PENDIENTE') && u.includes('VENCER') },
    { kind: 'count', label: 'Pendientes — En tiempo',      match: u => u.includes('PENDIENTE') && u.includes('TIEMPO') },
    { kind: 'pct',   label: 'Pendientes — %',              match: u => u.includes('PENDIENTE') && u.includes('%'), pctSourceMatch: u => u.includes('PENDIENTE') && u.includes('TOTAL') },
    { kind: 'count', label: 'Con óptico',                  match: u => u.includes('OPTICO') },
    { kind: 'count', label: 'Reiterativas — Total',        match: u => u.includes('REITERATIVA') && u.includes('TOTAL') },
    { kind: 'pct',   label: 'Reiterativas — %',            match: u => u.includes('REITERATIVA') && u.includes('%'), pctSourceMatch: u => u.includes('REITERATIVA') && u.includes('TOTAL') },
  ];

  /** Versión "bonita" del detalle en vivo del modal: en vez de los nombres de columna
   * crudos y compuestos que trae el scraper (ej. "Resueltas registradas en el periodo
   * Rechazadas Total"), usa las mismas etiquetas cortas y el mismo orden que ya usa la
   * tabla principal de Colonias (LEAF_DEFS) — resueltas contra las columnas reales de
   * ESTA fila puntual, sin tocar this.columns/this.orderedCols (que son de la tabla
   * principal, no de este detalle). Trae TODAS las columnas (Sec..Reiterativas), igual
   * que la fila completa de la tabla principal — pedido explícito: ver la fila entera
   * de esa colonia para ese código, no solo un resumen de 4 campos. */
  get modalDetalleFields(): { label: string; value: string }[] {
    if (!this.modalDetalleRow || this.modalDetalleCols.length === 0) return [];
    const fields: { label: string; value: string }[] = [
      { label: 'Tipo', value: this.modalDetalleCodigo ?? '' }
    ];
    for (const def of this.LEAF_DEFS) {
      const col = this.modalDetalleCols.find(c => def.match(c.toUpperCase()));
      if (col) fields.push({ label: def.label, value: String(this.modalDetalleRow[col] ?? '') });
    }
    return fields;
  }

  zonas: { value: string; label: string }[] = [];
  areas: { value: string; label: string }[] = [{ value: '00000', label: 'Todas las áreas' }];
  zonasLoading = false;
  areasLoading = false;

  selectedZona = '00000';
  selectedArea = '00000';

  private rangeSub?: Subscription;

  constructor(
    private dashboardService: DashboardService,
    private auth: AuthService,
    private dateRange: DateRangeService,
    private cdr: ChangeDetectorRef
  ) {
    this.zonas = this.auth.getZonas();

    // Igual que IMU/causas: si el usuario ya consultó y cambia el rango de fechas
    // global, re-consultamos. Ignoramos la primera emisión (la inicial).
    let first = true;
    this.rangeSub = this.dateRange.range$.subscribe(() => {
      if (first) { first = false; return; }
      if (this.tableData.length > 0) this.consultar();
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
    this.cargarAreas();
  }

  ngOnDestroy(): void {
    this.rangeSub?.unsubscribe();
    this.modalDetalleSub?.unsubscribe();
    if (this.modalChart) this.modalChart.destroy();
  }

  get selectedZonaLabel(): string {
    return this.zonas.find(z => z.value === this.selectedZona)?.label ?? 'Todas las zonas';
  }
  get selectedAreaLabel(): string {
    return this.areas.find(a => a.value === this.selectedArea)?.label ?? 'Todas las áreas';
  }

  // Las áreas anidan bajo la zona seleccionada (igual que zona anida bajo división) —
  // al cambiar de zona hay que re-consultar las áreas disponibles y resetear la selección.
  // Recibe el valor nuevo por (ngModelChange) en vez de leer this.selectedZona: mezclar
  // [(ngModel)] con (change) no garantiza el orden, y cargarAreas() podía terminar
  // pidiendo las áreas de la zona ANTERIOR (bug ya visto en producción).
  onZonaChange(nuevaZona: string): void {
    this.selectedZona = nuevaZona;
    this.selectedArea = '00000';
    this.cargarAreas();
  }

  private cargarAreas(): void {
    this.areasLoading = true;
    this.dashboardService.getAreas(this.selectedZona).subscribe({
      next: areas => {
        this.areas = areas?.length > 0 ? areas : [{ value: '00000', label: 'Todas las áreas' }];
        this.areasLoading = false;
      },
      error: () => { this.areasLoading = false; }
    });
  }

  // ── Consulta ───────────────────────────────────────────────────────────────

  consultar(): void {
    this.status = 'LOADING';
    this.errorMsg = '';
    // Una consulta nueva invalida cualquier comparación previa (zona/área/fechas cambiaron).
    this.prevData = [];
    this.compareYear = 0;
    this.loadingOffset = 0;
    this.compareRows = [];
    // Nuevos filtros invalidan el caché del desglose por colonia (usado por el modal).
    this.inconformidadesPorColonia = null;
    this.closeModal();

    const { desde, hasta } = this.dateRange.current;
    this.dashboardService.getColonias(this.selectedZona, this.selectedArea, desde, hasta).subscribe({
      next: data => {
        const raw = data ?? [];
        this.columns = raw.length > 0 ? Object.keys(raw[0]) : [];
        this.resolveOrderedCols();
        // Descartamos cualquier fila TOTAL que venga del portal — calculamos la nuestra
        // (ver computeTotalRow) para garantizar que los % queden bien (razón, no suma).
        this.tableData = raw.filter((r: any) => !this.isTotal(r));
        this.sortByRecibidasDesc();
        this.computeTotalRow();
        this.status = 'SUCCESS';
        this.renderChart();
      },
      error: err => this.fail(err)
    });
  }

  // ── Comparar con el año anterior (igual patrón que causas, sin el offset de 2 años) ──
  iniciarComparacionAnio(): void {
    if (this.tableData.length === 0) {
      this.status = 'ERROR';
      this.errorMsg = 'Primero presiona "Consultar" para cargar los datos del año actual.';
      return;
    }

    this.status = 'LOADING';
    this.errorMsg = '';
    this.loadingOffset = 1;
    const targetYear = this.currentYear - 1;

    const { desde, hasta } = this.dateRange.current;
    const shiftYear = (d: string) => `${Number(d.slice(0, 4)) - 1}${d.slice(4)}`;

    this.dashboardService.getColonias(this.selectedZona, this.selectedArea, shiftYear(desde), shiftYear(hasta))
      .pipe(timeout({ each: SCRAPE_TIMEOUT_MS }))
      .subscribe({
        next: data => {
          this.prevData = (data ?? []).filter((r: any) => !this.isTotal(r));
          this.compareYear = targetYear;
          this.loadingOffset = 0;
          this.status = 'SUCCESS';
          this.recomputeCompareRows();
          this.renderChart();
        },
        error: (err) => {
          this.compareYear = 0;
          this.loadingOffset = 0;
          this.status = 'ERROR';
          this.errorMsg = err?.name === 'TimeoutError'
            ? `La consulta de ${targetYear} tardó demasiado (el portal de CFE no respondió a tiempo). Vuelve a intentarlo.`
            : (err?.error ? `Error: ${err.error}` : `Error al obtener datos de ${targetYear}.`);
        }
      });
  }

  // Compara por "Recibidas" (la métrica principal de este reporte), emparejando
  // colonias por Clave entre los años consultados — mismo patrón que getCompareRows()
  // en causas.component.ts, adaptado a una lista plana (colonias no tiene "códigos").
  private recomputeCompareRows(): void {
    if (!this.isCompareMode) { this.compareRows = []; return; }

    const claveCol = this.claveCol;
    const descCol  = this.descCol;
    const recibidasCol = this.findCol('RECIBIDA');
    if (!claveCol || !recibidasCol) { this.compareRows = []; return; }

    const curr = this.tableData;
    const prev = this.prevData;

    const currMap = new Map<string, any>(curr.map(r => [String(r[claveCol] ?? ''), r]));
    const prevMap = new Map<string, any>(prev.map(r => [String(r[claveCol] ?? ''), r]));
    const allClaves = new Set<string>([...currMap.keys(), ...prevMap.keys()]);

    this.compareRows = Array.from(allClaves)
      .filter(k => k)
      .map(clave => {
        const c = currMap.get(clave), p = prevMap.get(clave);
        const currVal = this.parseNum(c?.[recibidasCol]);
        const prevVal = this.parseNum(p?.[recibidasCol]);
        const variacion = prevVal > 0
          ? Math.round(((currVal - prevVal) / prevVal) * 10000) / 100
          : (currVal > 0 ? currVal * 100 : 0);
        return {
          Clave: clave,
          Descripcion: String(c?.[descCol ?? ''] || p?.[descCol ?? ''] || ''),
          Anterior: prevVal,
          Actual: currVal,
          Variacion: variacion
        };
      })
      .sort((a, b) => b.Actual - a.Actual);
  }

  // Gráfica comparativa: Top 10 colonias (por Recibidas del año actual), barras por
  // año con fondo rojo/verde según si subió o bajó — mismo patrón que causas.
  // Usa el MISMO canvas que la gráfica normal (renderChart la reemplaza, no agrega una nueva).
  private renderCompareYearsChart(): void {
    if (this.compareRows.length === 0) {
      if (this.chart) { this.chart.destroy(); this.chart = null; }
      return;
    }

    const top10  = this.compareRows.slice(0, 10); // ya viene ordenado por Actual desc
    const labels = top10.map(r => r.Descripcion || r.Clave);

    const datasets: any[] = [
      {
        label: String(this.compareYear),
        data: top10.map(r => r.Anterior),
        backgroundColor: 'rgba(156,163,175,0.65)', borderColor: 'rgb(107,114,128)', borderWidth: 1, order: 1
      },
      {
        label: String(this.currentYear),
        data: top10.map(r => r.Actual),
        backgroundColor: 'rgba(59,130,246,0.78)', borderColor: 'rgb(37,99,235)', borderWidth: 1, order: 0
      }
    ];

    const bgColors = top10.map(r =>
      r.Actual > r.Anterior ? 'rgba(220,53,69,0.10)' :
      r.Actual < r.Anterior ? 'rgba(40,167,69,0.10)' :
                              'rgba(108,117,125,0.05)'
    );

    setTimeout(() => {
      if (this.chart) { this.chart.destroy(); this.chart = null; }
      this.cdr.detectChanges();
      const canvasEl = document.getElementById('coloniasChart');
      if (!(canvasEl instanceof HTMLCanvasElement)) return;

      this.chart = new Chart(canvasEl, {
        type: 'bar',
        data: { labels, datasets },
        options: {
          responsive: true,
          maintainAspectRatio: false,
          interaction: { mode: 'index' as const, intersect: false },
          plugins: {
            bgColumns: { colors: bgColors },
            title: {
              display: true,
              text: `Top 10 colonias por Recibidas — ${this.compareYear} vs ${this.currentYear}`,
              font: { size: 12, weight: 'bold' }
            },
            legend: { position: 'top' as const, labels: { usePointStyle: true, padding: 8, boxWidth: 12, font: { size: 11 } } }
          },
          scales: {
            x: { ticks: { maxRotation: 90, minRotation: 45, font: { size: 9 } }, grid: { display: false } },
            y: { beginAtZero: true, ticks: { font: { size: 10 } }, grid: { color: 'rgba(0,0,0,0.06)' } }
          }
        },
        plugins: [COMPARE_BG_COLUMNS_PLUGIN, BAR_DATALABELS_PLUGIN]
      } as any);
    }, 0);
  }

  // Resuelve cada columna esperada (LEAF_DEFS, en el orden real del portal) contra los
  // nombres de columna compuestos que llegan del backend.
  private resolveOrderedCols(): void {
    this.orderedCols = this.LEAF_DEFS.map(def =>
      this.columns.find(c => def.match(c.toUpperCase()))
    );
  }

  // Fila Total: suma simple para conteos, pero los % se RECALCULAN como razón contra
  // Recibidas (no se suman los % de cada colonia) — fórmula confirmada contra el
  // reporte real del portal (ej. Reiterativas % = suma(Reiterativas Total) / suma(Recibidas) * 100).
  private computeTotalRow(): void {
    const recibidasCol = this.orderedCols[3];
    if (!recibidasCol) { this.totalRow = {}; return; }

    const recibidasSum = this.tableData.reduce((s, r) => s + this.parseNum(r[recibidasCol]), 0);
    const total: { [k: string]: string } = {};

    this.LEAF_DEFS.forEach((def, i) => {
      const col = this.orderedCols[i];
      if (!col) return;
      if (def.kind === 'text') {
        total[col] = i === 2 ? 'Total' : '';
      } else if (def.kind === 'count') {
        const sum = this.tableData.reduce((s, r) => s + this.parseNum(r[col]), 0);
        total[col] = sum.toLocaleString('es-MX');
      } else {
        const srcCol = this.columns.find(c => def.pctSourceMatch!(c.toUpperCase()));
        const srcSum = srcCol ? this.tableData.reduce((s, r) => s + this.parseNum(r[srcCol]), 0) : 0;
        const pct = recibidasSum > 0 ? (srcSum / recibidasSum) * 100 : 0;
        total[col] = pct.toFixed(2);
      }
    });

    this.totalRow = total;
  }

  // Ordena las colonias de mayor a menor por "Recibidas".
  private sortByRecibidasDesc(): void {
    const recibidasCol = this.findCol('RECIBIDA');
    if (!recibidasCol) return;
    this.tableData.sort((a, b) => this.parseNum(b[recibidasCol]) - this.parseNum(a[recibidasCol]));
  }

  private fail(err: any): void {
    this.errorMsg = typeof err?.error === 'string'
      ? err.error
      : 'No se pudo cargar el reporte de colonias. Verifica la conexión/VPN a la red de CFE.';
    this.status = 'ERROR';
  }

  // ── Helpers de columnas ──────────────────────────────────────────────────────
  // Los nombres exactos de columna dependen de cómo el parser compone los
  // encabezados de dos niveles del portal (ej. podría salir "RECHAZADAS TOTAL").
  // Por eso resolvemos por coincidencia difusa en vez de nombres exactos.

  private findCol(...keywords: string[]): string | undefined {
    return this.columns.find(c => keywords.every(k => c.toUpperCase().includes(k.toUpperCase())));
  }

  get claveCol(): string | undefined { return this.findCol('CLAVE'); }
  get descCol(): string | undefined { return this.findCol('DESCRIP'); }

  isTotal(row: any): boolean {
    const v = String(row[this.claveCol ?? ''] ?? row[this.descCol ?? ''] ?? '').trim().toUpperCase();
    return v === 'TOTAL';
  }

  private parseNum(v: any): number {
    return Number(String(v ?? '0').replace(/,/g, '').replace(/%/g, '')) || 0;
  }

  private rowLabel(row: any): string {
    return String(row[this.descCol ?? ''] ?? row[this.claveCol ?? ''] ?? '').trim();
  }

  // Normaliza texto para comparar Clave/Descripcion entre dos scrapes independientes
  // (resumen combinado vs detalle en vivo por codigo): colapsa espacios -- incluye el
  // caracter   (nbsp) que HtmlAgilityPack deja al decodificar &nbsp; -- quita espacios
  // extremos y compara sin distinguir mayusculas. Sin esto, diferencias triviales de
  // espaciado hacian que nunca se encontrara la fila de la colonia (el "no aparece la
  // tablita" reportado).
  private normStr(v: any): string {
    return String(v ?? '')
      .replace(/ /g, ' ')
      .replace(/\s+/g, ' ')
      .trim()
      .toUpperCase();
  }

  // ── Gráfica ───────────────────────────────────────────────────────────────────

  private renderChart(): void {
    // En modo comparación, esta MISMA gráfica se reemplaza por el Top 10 multi-año
    // (no se agrega una gráfica aparte) — igual que en causas.
    if (this.isCompareMode) {
      this.renderCompareYearsChart();
      return;
    }

    // Usamos orderedCols (ya resuelto por resolveOrderedCols()/LEAF_DEFS, la misma
    // fuente que llena la tabla "Detalle") en vez de volver a resolver columnas aquí
    // con findCol(): dos resoluciones independientes contra el mismo this.columns
    // podían divergir (bug real: la gráfica graficaba columnas de "Rechazadas"/"%"
    // en vez de los Totales de Terminadas/Canceladas/Rechazadas/Pendientes). Los
    // índices son fijos según el orden de LEAF_DEFS: 3=Recibidas, 4=Rechazadas Total,
    // 8=Canceladas Total, 10=Terminadas Total, 14=Pendientes Total.
    const recibidasCol  = this.orderedCols[3];
    const rechazadasCol = this.orderedCols[4];
    const canceladasCol = this.orderedCols[8];
    const terminadasCol = this.orderedCols[10];
    const pendientesCol = this.orderedCols[14];

    // tableData ya viene ordenado por Recibidas de mayor a menor (sortByRecibidasDesc) —
    // la gráfica solo muestra las primeras 10, igual que el "Top" del portal.
    const dataRows = this.tableData.slice(0, 10);
    const labels   = dataRows.map(r => this.rowLabel(r));

    const series: { key: string; col: string | undefined }[] = [
      { key: 'recibidas',  col: recibidasCol },
      { key: 'terminadas', col: terminadasCol },
      { key: 'canceladas', col: canceladasCol },
      { key: 'rechazadas', col: rechazadasCol },
      { key: 'pendientes', col: pendientesCol },
    ];

    const labelNames: { [k: string]: string } = {
      recibidas: 'Recibidas', terminadas: 'Terminadas', canceladas: 'Canceladas',
      rechazadas: 'Rechazadas', pendientes: 'Pendientes'
    };

    const datasets = series
      .filter(s => s.col)
      .map(s => ({
        label: labelNames[s.key],
        data: dataRows.map(r => this.parseNum(r[s.col!])),
        backgroundColor: SERIES_COLORS[s.key].bg,
        borderColor: SERIES_COLORS[s.key].border,
        borderWidth: 1
      }));

    setTimeout(() => {
      if (this.chart) { this.chart.destroy(); this.chart = null; }
      if (datasets.length === 0 || labels.length === 0) return;

      // Igual que en renderModalChart(): garantiza que el *ngIf de la sección de gráfica
      // ya haya pintado el <canvas> antes de buscarlo, para no toparse con "can't acquire
      // context from the given item" si el DOM todavía no se actualizó.
      this.cdr.detectChanges();
      const canvasEl = document.getElementById('coloniasChart');
      if (!(canvasEl instanceof HTMLCanvasElement)) return;

      this.chart = new Chart(canvasEl, {
        type: 'bar',
        data: { labels, datasets },
        options: {
          responsive: true,
          maintainAspectRatio: false,
          interaction: { mode: 'index' as const, intersect: false },
          onClick: (_evt: any, elements: any[]) => {
            if (elements.length > 0) this.onColoniaClick(dataRows[elements[0].index]);
          },
          onHover: (evt: any, elements: any[]) => {
            const target = evt?.native?.target as HTMLElement | undefined;
            if (target) target.style.cursor = elements.length > 0 ? 'pointer' : 'default';
          },
          plugins: {
            title: {
              display: true,
              text: `Top 10 colonias por Recibidas — ${this.selectedZonaLabel}, ${this.selectedAreaLabel}`,
              font: { size: 12, weight: 'bold' }
            },
            legend: { position: 'top' as const, labels: { usePointStyle: true, padding: 8, boxWidth: 12, font: { size: 11 } } }
          },
          scales: {
            x: { ticks: { maxRotation: 90, minRotation: 45, font: { size: 9 } }, grid: { display: false } },
            y: { beginAtZero: true, ticks: { font: { size: 10 } }, grid: { color: 'rgba(0,0,0,0.06)' } }
          }
        },
        plugins: [BAR_DATALABELS_PLUGIN]
      } as any);
    }, 0);
  }

  // ── Modal "desglose por colonia" ────────────────────────────────────────────
  // El portal no permite filtrar el reporte a una sola colonia (cveColonia solo
  // tiene "Todas"), así que el backend scrapea, una vez por consulta, TODAS las
  // colonias para cada código E02-E07 y las combina — por eso el primer clic
  // siempre trae el desglose completo (se cachea en inconformidadesPorColonia) y
  // los clics siguientes sobre otras colonias son instantáneos.

  onColoniaClick(row: any): void {
    this.modalColoniaRow = row;
    this.selectedMetric = 'recibidas';
    this.resetModalDetalle();
    if (this.inconformidadesPorColonia) {
      this.computeModalCaches();
      this.renderModalChart();
      return;
    }

    this.loadingInconformidades = true;
    this.inconformidadesErrorMsg = '';
    const { desde, hasta } = this.dateRange.current;
    this.dashboardService.getColoniaInconformidades(this.selectedZona, this.selectedArea, desde, hasta)
      .pipe(timeout({ each: SCRAPE_TIMEOUT_MS }))
      .subscribe({
        next: data => {
          this.inconformidadesPorColonia = data ?? [];
          this.loadingInconformidades = false;
          this.computeModalCaches();
          this.renderModalChart();
        },
        error: err => {
          // ANTES esto dejaba inconformidadesPorColonia en [] sin avisar nada — la tabla
          // se veía "correcta" pero con todo en 0, indistinguible de un resultado real
          // vacío. Además [] es truthy en JS, así que el siguiente clic en OTRA colonia
          // entraba por la rama de caché y jamás reintentaba el scrape. Ahora se deja en
          // null (para que el próximo clic sí reintente) y se muestra el error real.
          this.inconformidadesPorColonia = null;
          this.loadingInconformidades = false;
          this.inconformidadesErrorMsg = err?.error || err?.message || 'No se pudo obtener el desglose de esta colonia.';
        }
      });
  }

  // Calcula UNA vez (por clic de colonia, o al cambiar el filtro de métrica) la fila
  // combinada, el orden de códigos y el residual "Otras" — ver el comentario junto a
  // modalRowCache sobre por qué esto ya NO se hace en un getter.
  private computeModalCaches(): void {
    this.modalRowCache = this.findModalRow();
    const sorted = [...this.INCONFORMIDAD_CODES].sort(
      (a, b) => this.getModalRowValue(b.value) - this.getModalRowValue(a.value)
    );

    const totalColoniaCol = this.metricTotalCol();
    const totalColonia = totalColoniaCol ? this.parseNum(this.modalColoniaRow?.[totalColoniaCol]) : 0;
    const sumaCodigos = sorted.reduce((s, c) => s + this.getModalRowValue(c.value), 0);
    this.otrasValue = Math.max(0, totalColonia - sumaCodigos);

    this.sortedModalCodesCache = this.otrasValue > 0
      ? [...sorted, { value: 'OTRAS', label: 'Otras (no clasificadas)' }]
      : sorted;
  }

  // Columna de la tabla principal (orderedCols, misma resolución que llena "Detalle") que
  // trae el TOTAL real de la colonia para la métrica actualmente seleccionada — índices
  // fijos según LEAF_DEFS: 3=Recibidas, 4=Rechazadas Total, 12=Terminadas Cumplidas,
  // 14=Pendientes Total.
  private metricTotalCol(): string | undefined {
    switch (this.selectedMetric) {
      case 'rechazadas': return this.orderedCols[4];
      case 'pendientes': return this.orderedCols[14];
      case 'cumplidas':  return this.orderedCols[12];
      default:           return this.orderedCols[3];
    }
  }

  // Sufijo de clave que el backend agregó por código para cada métrica (ver MergeRows en
  // GetColoniaInconformidadesAsync) — Recibidas no lleva sufijo (item[code] tal cual, por
  // compatibilidad con lo que ya existía).
  private metricKey(code: string): string {
    switch (this.selectedMetric) {
      case 'rechazadas': return `${code}_RECHAZADAS`;
      case 'pendientes': return `${code}_PENDIENTES`;
      case 'cumplidas':  return `${code}_CUMPLIDAS`;
      default:           return code;
    }
  }

  onMetricFilterChange(metric: 'recibidas' | 'rechazadas' | 'pendientes' | 'cumplidas'): void {
    if (this.selectedMetric === metric) return;
    this.selectedMetric = metric;
    this.computeModalCaches();
    this.renderModalChart();
  }

  closeModal(): void {
    if (this.modalChart) { this.modalChart.destroy(); this.modalChart = null; }
    this.modalColoniaRow = null;
    this.modalRowCache = undefined;
    this.sortedModalCodesCache = [];
    this.selectedMetric = 'recibidas';
    this.otrasValue = 0;
    this.inconformidadesErrorMsg = '';
    this.resetModalDetalle();
  }

  private resetModalDetalle(): void {
    this.modalDetalleSub?.unsubscribe();
    this.modalDetalleSub = undefined;
    this.modalDetalleCodigo = null;
    this.modalDetalleLoading = false;
    this.modalDetalleErrorMsg = '';
    this.modalDetalleRow = null;
    this.modalDetalleCols = [];
  }

  /** Clic en una inconformidad (E02..QC7) dentro del modal — scrapea en vivo la tabla
   * completa de ESE código, agrupada por TODAS las colonias (el portal no permite
   * filtrar a una sola), y de esa tabla se queda solo la fila que corresponde a la
   * colonia que ya está abierta en el modal. Clic de nuevo en el mismo código lo cierra. */
  onInconformidadClick(code: string): void {
    // "Otras" es un residual calculado en el cliente (total real de la colonia menos la
    // suma de los 16 códigos rastreados) — no es un código real que el portal reconozca,
    // así que no tiene detalle en vivo que consultar.
    if (code === 'OTRAS') return;
    if (this.modalDetalleCodigo === code) {
      this.resetModalDetalle();
      return;
    }
    // Ya hay un scrape en curso (otro código o el mismo) — ignora el clic en vez de
    // apilar otro navegador Chromium en el backend. El usuario tiene que esperar a que
    // termine (o falle) el actual antes de pedir otro código.
    if (this.modalDetalleLoading) return;

    this.modalDetalleCodigo = code;
    this.modalDetalleLoading = true;
    this.modalDetalleErrorMsg = '';
    this.modalDetalleRow = null;
    this.modalDetalleCols = [];

    // Buscados por nombre difuso (no asumimos "Clave"/"Descripcion" exactos — el
    // scraper compone los nombres de columna y pueden variar) contra la fila de
    // colonia ya abierta en el modal.
    const claveBuscada = this.normStr(this.modalColoniaRow?.[this.claveCol ?? '']);
    const descBuscada  = this.normStr(this.modalColoniaRow?.[this.descCol ?? '']);

    const { desde, hasta } = this.dateRange.current;
    this.modalDetalleSub?.unsubscribe();
    this.modalDetalleSub = this.dashboardService.getColoniaInconformidadDetalle(this.selectedZona, this.selectedArea, desde, hasta, code)
      .pipe(
        timeout({ each: SCRAPE_TIMEOUT_MS }),
        finalize(() => { this.modalDetalleLoading = false; }) // siempre se apaga, pase lo que pase
      )
      .subscribe({
        next: rows => {
          try {
            const list = rows ?? [];
            const first = list[0] ?? {};
            const rowClaveCol = Object.keys(first).find(c => c.toUpperCase().includes('CLAVE'));
            const rowDescCol  = Object.keys(first).find(c => c.toUpperCase().includes('DESCRIP'));
            const match = list.find((r: any) =>
              (claveBuscada && rowClaveCol && this.normStr(r[rowClaveCol]) === claveBuscada) ||
              (descBuscada && rowDescCol && this.normStr(r[rowDescCol]) === descBuscada)
            );
            this.modalDetalleRow = match ?? null;
            this.modalDetalleCols = match ? Object.keys(match) : [];
          } catch {
            this.modalDetalleRow = null;
            this.modalDetalleCols = [];
            this.modalDetalleErrorMsg = 'No se pudo interpretar la respuesta del portal.';
          }
        },
        error: err => {
          this.modalDetalleErrorMsg = err?.error || 'No se pudo consultar el detalle de esta inconformidad.';
        }
      });
  }

  private findModalRow(): any | undefined {
    if (!this.inconformidadesPorColonia || !this.modalColoniaRow) return undefined;
    const clave = this.normStr(this.modalColoniaRow[this.claveCol ?? '']);
    const desc  = this.normStr(this.modalColoniaRow[this.descCol ?? '']);
    return this.inconformidadesPorColonia.find(r =>
      (clave && this.normStr(r['Clave']) === clave) ||
      (desc && this.normStr(r['Descripcion']) === desc)
    );
  }

  getModalRowValue(code: string): number {
    if (code === 'OTRAS') return this.otrasValue;
    return this.modalRowCache ? this.parseNum(this.modalRowCache[this.metricKey(code)]) : 0;
  }

  private renderModalChart(): void {
    const sorted = this.sortedModalCodesCache;
    const labels = sorted.map(c => c.value);
    const data   = sorted.map(c => this.getModalRowValue(c.value));
    const metricLabel = this.METRIC_OPTIONS.find(m => m.value === this.selectedMetric)?.label ?? 'Recibidas';

    setTimeout(() => {
      if (this.modalChart) { this.modalChart.destroy(); this.modalChart = null; }

      // onColoniaClick() llega aquí desde el handler onClick de Chart.js (no un (click) de
      // Angular), y a veces desde la rama de caché — se fuerza el ciclo de detección de
      // cambios ANTES de buscar el canvas para garantizar que el *ngIf del modal ya haya
      // pintado el <canvas> en el DOM (si no, document.getElementById lo encuentra null o
      // Chart.js tira "can't acquire context from the given item").
      this.cdr.detectChanges();
      const canvas = document.getElementById('coloniaDesgloseChart');
      if (!(canvas instanceof HTMLCanvasElement)) return;

      try {
        this.modalChart = new Chart(canvas, {
          type: 'bar',
          data: {
            labels,
            datasets: [{
              label: metricLabel,
              data,
              backgroundColor: 'rgba(59,130,246,0.78)',
              borderColor: 'rgb(37,99,235)',
              borderWidth: 1
            }]
          },
          options: {
            responsive: true,
            maintainAspectRatio: false,
            plugins: {
              legend: { display: false },
              title: { display: true, text: metricLabel, font: { size: 11, weight: 'bold' }, padding: { bottom: 6 } }
            },
            scales: {
              x: { ticks: { maxRotation: 90, minRotation: 45, font: { size: 9 } }, grid: { display: false } },
              y: { beginAtZero: true, ticks: { precision: 0 } }
            }
          },
          plugins: [BAR_DATALABELS_PLUGIN]
        } as any);
      } catch (err) {
        console.warn('No se pudo dibujar la gráfica del modal de colonias', err);
        this.modalChart = null;
      }
    }, 0);
  }

  // ── Exportar ──────────────────────────────────────────────────────────────────

  exportChartImage(): void {
    if (!this.chart) return;
    const canvas = this.chart.canvas;
    if (!canvas) return;
    const off = document.createElement('canvas');
    off.width = canvas.width; off.height = canvas.height;
    const offCtx = off.getContext('2d')!;
    offCtx.fillStyle = '#ffffff';
    offCtx.fillRect(0, 0, off.width, off.height);
    offCtx.drawImage(canvas, 0, 0);
    const link = document.createElement('a');
    link.href = off.toDataURL('image/png');
    link.download = `colonias_grafica_${this.selectedZona}_${this.selectedArea}.png`;
    link.click();
  }

  exportExcel(): void {
    const ws = XLSX.utils.json_to_sheet(this.tableData);
    const wb = XLSX.utils.book_new();
    XLSX.utils.book_append_sheet(wb, ws, 'Colonias');
    saveWorkbook(wb, `colonias_${this.selectedZona}_${this.selectedArea}.xlsx`);
  }

  // Exporta la tabla comparativa con las celdas de "Actual"/"Variación" en rojo/verde,
  // igual que exportCompareExcel() en causas.component.ts.
  exportCompareExcel(): void {
    if (!this.compareRows.length) return;
    const red = 'FFFEE2E2', green = 'FFD1FAE5';
    const HEADER_S = {
      fill: { patternType: 'solid', fgColor: { rgb: 'FF1E293B' } },
      font: { bold: true, color: { rgb: 'FFFFFFFF' }, sz: 11 },
      alignment: { horizontal: 'center', vertical: 'center' }
    };
    const y0 = String(this.compareYear), y1 = String(this.currentYear);
    const t  = this.compareTotals;
    const headers = ['Clave', 'Descripción', y0, y1, 'Variación %'];

    const ws: any = {};
    const enc = (r: number, c: number) => XLSX.utils.encode_cell({ r, c });
    headers.forEach((h, c) => ws[enc(0, c)] = { v: h, t: 's', s: HEADER_S });

    const writeRow = (r: number, cells: { v: any; t: 's' | 'n'; rgb?: string | null; align?: string; bold?: boolean }[]) => {
      cells.forEach((cell, c) => {
        ws[enc(r, c)] = {
          v: cell.v, t: cell.t,
          s: {
            fill: cell.rgb ? { patternType: 'solid', fgColor: { rgb: cell.rgb } } : undefined,
            font: { sz: 11, bold: !!cell.bold },
            alignment: { horizontal: cell.align ?? 'center', vertical: 'center' }
          }
        };
      });
    };

    this.compareRows.forEach((r, ri) => {
      writeRow(ri + 1, [
        { v: r.Clave, t: 's', align: 'left' },
        { v: r.Descripcion, t: 's', align: 'left' },
        { v: r.Anterior, t: 'n' },
        { v: r.Actual, t: 'n', rgb: r.Actual > r.Anterior ? red : r.Actual < r.Anterior ? green : null },
        { v: `${r.Variacion}%`, t: 's', rgb: r.Variacion > 0 ? red : r.Variacion < 0 ? green : null }
      ]);
    });

    const totalRowIdx = this.compareRows.length + 1;
    writeRow(totalRowIdx, [
      { v: 'TOTAL', t: 's', bold: true, align: 'left' },
      { v: '', t: 's', bold: true },
      { v: t.anterior, t: 'n', bold: true },
      { v: t.actual, t: 'n', bold: true },
      { v: `${t.variacion}%`, t: 's', bold: true }
    ]);

    ws['!ref'] = XLSX.utils.encode_range({ s: { c: 0, r: 0 }, e: { c: headers.length - 1, r: totalRowIdx } });

    const wb = XLSX.utils.book_new();
    XLSX.utils.book_append_sheet(wb, ws, 'Comparacion');
    saveWorkbook(wb, `colonias_comparacion_${this.selectedZona}_${this.selectedArea}.xlsx`);
  }
}
