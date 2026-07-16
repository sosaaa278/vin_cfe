import { Component, OnDestroy, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import Chart from 'chart.js/auto';
import * as XLSX from 'xlsx-js-style';
import { timeout } from 'rxjs/operators';
import { DashboardService } from '../services/dashboard.service';
import { AuthService } from '../services/auth.service';
import { DateRangeService } from '../services/date-range.service';
import { NavComponent } from '../shared/nav.component';
import { DateRangeBarComponent } from '../shared/date-range-bar.component';
import { saveWorkbook } from '../shared/excel-export';
import { Subscription } from 'rxjs';

// Tope de espera para el scraping en vivo (7 códigos secuenciales en el portal CFE).
// Si se supera, la petición falla con un mensaje claro en lugar de colgar la página.
const SCRAPE_TIMEOUT_MS = 240_000;

// ── Plugin: columna de fondo verde/roja por categoría (modo comparación) ─────────
const BG_COLUMNS_PLUGIN: any = {
  id: 'causasBgColumns',
  beforeDatasetsDraw(chart: any) {
    if (chart.config.type !== 'bar') return;
    const { ctx, chartArea } = chart;
    if (!chartArea) return;
    const colors: string[] = chart.options?.plugins?.bgColumns?.colors ?? [];
    if (!colors.length) return;
    const isHoriz = chart.options.indexAxis === 'y';
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
      if (isHoriz) {
        const top    = Math.min(...elements.map((e: any) => e.y - e.height / 2));
        const bottom = Math.max(...elements.map((e: any) => e.y + e.height / 2));
        ctx.fillRect(chartArea.left, top, chartArea.right - chartArea.left, bottom - top);
      } else {
        const left  = Math.min(...elements.map((e: any) => e.x - e.width  / 2));
        const right = Math.max(...elements.map((e: any) => e.x + e.width  / 2));
        ctx.fillRect(left, chartArea.top, right - left, chartArea.bottom - chartArea.top);
      }
      ctx.restore();
    });
  }
};

// ── Plugin: etiqueta de valor sobre cada barra ─────────────────────────────────
const BAR_DATALABELS: any = {
  id: 'causasBarLabels',
  afterDatasetsDraw(chart: any) {
    if (chart.config.type !== 'bar') return;
    const { ctx } = chart;
    const isHoriz = chart.options.indexAxis === 'y';
    chart.data.datasets.forEach((ds: any, di: number) => {
      const meta = chart.getDatasetMeta(di);
      if (meta.hidden) return;
      meta.data.forEach((el: any, idx: number) => {
        const val = ds.data[idx];
        if (!val) return;
        const txt = Number(val).toLocaleString('es-MX');
        ctx.save();
        ctx.fillStyle = '#1a202c';
        ctx.font = '600 10px "Segoe UI", sans-serif';
        if (isHoriz) {
          ctx.textAlign = 'left';
          ctx.textBaseline = 'middle';
          ctx.fillText(txt, el.x + 4, el.y);
        } else {
          ctx.textAlign = 'center';
          ctx.textBaseline = 'bottom';
          ctx.fillText(txt, el.x, el.y - 3);
        }
        ctx.restore();
      });
    });
  }
};

// ── Colores para pastel ────────────────────────────────────────────────────────
const PIE_COLORS = [
  'rgb(230, 57, 70)',   'rgb(0, 119, 255)',  'rgb(50, 205, 50)',
  'rgb(255, 200, 0)',   'rgb(128, 0, 128)',  'rgb(255, 102, 0)',
  'rgb(0, 206, 209)',   'rgb(255, 20, 147)', 'rgb(101, 67, 33)',
  'rgb(64, 64, 64)',    'rgb(0, 180, 120)',  'rgb(200, 100, 0)',
];

// ── Plugin: etiquetas dentro del pastel ───────────────────────────────────────
const PIE_DATALABELS: any = {
  id: 'causasPieDatalabels',
  afterDatasetsDraw(chart: any) {
    if (chart.config.type !== 'pie') return;
    const { ctx } = chart;
    const ds     = chart.data.datasets[0];
    const meta   = chart.getDatasetMeta(0);
    const labels = chart.data.labels as string[];
    const total  = (ds.data as number[]).reduce((a: number, b: number) => a + b, 0);

    meta.data.forEach((arc: any, i: number) => {
      const val = ds.data[i] as number;
      if (!val || total === 0 || val / total < 0.04) return;

      const midAngle = arc.startAngle + (arc.endAngle - arc.startAngle) / 2;
      const r  = arc.outerRadius * 0.62;
      const x  = arc.x + Math.cos(midAngle) * r;
      const y  = arc.y + Math.sin(midAngle) * r;
      const pct = ((val / total) * 100).toFixed(1) + '%';

      ctx.save();
      ctx.textAlign    = 'center';
      ctx.textBaseline = 'middle';
      ctx.shadowColor  = 'rgba(0,0,0,0.55)';
      ctx.shadowBlur   = 3;
      ctx.fillStyle    = '#ffffff';
      ctx.font         = 'bold 9px "Segoe UI", sans-serif';
      ctx.fillText(String(labels[i] ?? '').slice(0, 8), x, y - 7);
      ctx.fillText(pct, x, y + 7);
      ctx.restore();
    });
  }
};

@Component({
  selector: 'app-causas',
  standalone: true,
  imports: [CommonModule, FormsModule, NavComponent, DateRangeBarComponent],
  templateUrl: './causas.component.html',
  styleUrls: ['./causas.component.css']
})
export class CausasComponent implements OnInit, OnDestroy {
  status: 'WAITING' | 'LOADING' | 'SUCCESS' | 'ERROR' = 'WAITING';
  tableData: any[] = [];
  columns: string[] = [];
  errorMessage = '';

  selectedCode  = 'E02';
  activeCode    = 'E02';
  allCausasData:   { [code: string]: any[] } = {};  // datos año actual
  prevCausasData:  { [code: string]: any[] } = {};  // datos año comparado (offset 1)
  prev2CausasData: { [code: string]: any[] } = {};  // datos 2 años atrás (modo 3 años)
  compareYear  = 0;   // 0 = sin comparación; >0 = año que se está comparando (offset 1)
  compare2Year = 0;   // 0 = modo 2 años; >0 = año adicional (offset 2)
  loadingOffset = 0;  // 1, 2, o 3: qué botón está cargando

  readonly CODES = [
    { value: 'E02', label: 'E02 — Ramal fuera' },
    { value: 'E03', label: 'E03 — Sector fuera' },
    { value: 'E04', label: 'E04 — Falso contacto distribución' },
    { value: 'E05', label: 'E05 — Improcedente distribución' },
    { value: 'E06', label: 'E06 — Servicio importante fuera' },
    { value: 'E07', label: 'E07 — Reparación mayor' },
    { value: 'Q07', label: 'Q07 — Deficiencia de voltaje' },
  ];

  paretoChart: any = null;
  paretoRows: any[] = [];
  paretoShown = 0;
  paretoTotal = 0;
  paretoPct   = 0;

  // Tablas comparativas PRECALCULADAS (evita recálculos en cada ciclo de Angular = congelamiento)
  compareRows: { Clave: string; Descripcion: string; Anterior2: number; Anterior: number; Actual: number; Variacion: number }[] = [];
  codeSummary: { code: string; label: string; anterior2: number; anterior: number; actual: number; variacion: number }[] = [];

  // Causas exclusivas de cada año (clave presente en un año pero no en el otro)
  soloAnterior: { Clave: string; Descripcion: string; valor: number }[] = [];
  soloActual:   { Clave: string; Descripcion: string; valor: number }[] = [];

  // El año "actual" sale de la fecha "hasta" del rango elegido por el usuario.
  get currentYear(): number {
    return Number(this.dateRange.current.hasta.slice(0, 4)) || new Date().getFullYear();
  }

  zonas: { value: string; label: string }[] = [];
  zonasLoading = false;
  selectedZona = '00000';

  get selectedZonaLabel(): string {
    return this.zonas.find(z => z.value === this.selectedZona)?.label ?? 'Todas las zonas';
  }

  chartType: 'bar' | 'line' | 'horizontalBar' | 'pie' = 'bar';
  pieYear = 0;  // año activo en el pastel; se inicializa al cargar la comparación

  private rangeSub?: Subscription;

  constructor(
    private dashboardService: DashboardService,
    public auth: AuthService,
    private router: Router,
    private dateRange: DateRangeService
  ) {
    this.zonas = this.auth.getZonas();
    let first = true;
    this.rangeSub = this.dateRange.range$.subscribe(() => {
      if (first) { first = false; return; }
      if (this.isAllMode) this.iniciarScrapingAll();
    });
  }

  ngOnInit(): void {
    this.zonasLoading = true;
    this.dashboardService.getZonas().subscribe({
      next: zonas => {
        if (zonas?.length > 1) this.zonas = zonas; // Solo actualiza si el backend tiene sub-zonas reales
        this.zonasLoading = false;
      },
      error: () => { this.zonasLoading = false; }
    });
  }

  ngOnDestroy(): void {
    if (this.paretoChart) this.paretoChart.destroy();
    this.rangeSub?.unsubscribe();
  }

  // ── Estado ─────────────────────────────────────────────────────────────────

  get totalRegistros(): number {
    return this.tableData.filter(r => !this.isRowTotal(r)).length;
  }

  isRowTotal(row: any): boolean {
    if (!row) return false;
    return Object.values(row).some(v => /^total$/i.test(String(v ?? '').trim()));
  }

  private parseNum(val: any): number {
    return Number(String(val ?? '0').replace(/[,\s%]/g, '').trim()) || 0;
  }

  /**
   * El portal de CFE devuelve la descripción como "CATEGORIA :: DETALLE" (ej.
   * "INCENDIO O EXPLOSION :: PAJAROS/ANIMALES"). Nos quedamos solo con el DETALLE
   * (lo que va después de "::"). Se aplica una vez, al recibir los datos, para que
   * tabla, gráfica, comparativos y Excel usen siempre la versión corta.
   */
  private stripDescPrefix(data: { [code: string]: any[] }): { [code: string]: any[] } {
    const result: { [code: string]: any[] } = {};
    for (const code of Object.keys(data)) {
      result[code] = (data[code] ?? []).map((row: any) => {
        const descCol = Object.keys(row).find(c => c.toUpperCase().includes('DESCRI'));
        if (!descCol) return row;
        const val = String(row[descCol] ?? '');
        const idx = val.indexOf('::');
        if (idx === -1) return row;
        return { ...row, [descCol]: val.slice(idx + 2).trim() };
      });
    }
    return result;
  }

  // Muestra todas las columnas del scraper en su orden natural, excepto Grafica.
  get orderedColumns(): string[] {
    return this.columns.filter(c => !c.toUpperCase().includes('GRAF'));
  }

  get isAllMode(): boolean {
    return Object.keys(this.allCausasData).length > 0;
  }

  get isCompareMode(): boolean {
    return this.compareYear > 0 && Object.keys(this.prevCausasData).length > 0;
  }

  get is3YearMode(): boolean {
    return this.isCompareMode && this.compare2Year > 0 && Object.keys(this.prev2CausasData).length > 0;
  }

  codeRows(code: string): any[] {
    return this.allCausasData[code] || [];
  }

  // ── Comparación año anterior ───────────────────────────────────────────────


  getCompareRows(code: string): { Clave: string; Descripcion: string; Anterior2: number; Anterior: number; Actual: number; Variacion: number }[] {
    const curr:  any[] = this.allCausasData[code]   ?? [];
    const prev:  any[] = this.prevCausasData[code]  ?? [];
    const prev2: any[] = this.is3YearMode ? (this.prev2CausasData[code] ?? []) : [];
    if (!curr.length && !prev.length && !prev2.length) return [];

    const all      = [...curr, ...prev, ...prev2];
    const countCol = this.detectCountCol(all);
    const claveCol = this.detectCol(all, 'CLAVE');
    const descCol  = this.detectCol(all, 'DESCRI');

    const currMap  = new Map<string, any>(curr.map((r: any)  => [String(r[claveCol] ?? ''), r]));
    const prevMap  = new Map<string, any>(prev.map((r: any)  => [String(r[claveCol] ?? ''), r]));
    const prev2Map = new Map<string, any>(prev2.map((r: any) => [String(r[claveCol] ?? ''), r]));
    const allClaves = new Set<string>([
      ...curr.map((r: any)  => String(r[claveCol] ?? '')),
      ...prev.map((r: any)  => String(r[claveCol] ?? '')),
      ...prev2.map((r: any) => String(r[claveCol] ?? ''))
    ]);

    return Array.from(allClaves)
      .filter(k => k)
      .map(clave => {
        const c        = currMap.get(clave);
        const p        = prevMap.get(clave);
        const p2       = prev2Map.get(clave);
        const currVal  = this.parseNum(c?.[countCol]);
        const prevVal  = this.parseNum(p?.[countCol]);
        const prev2Val = this.parseNum(p2?.[countCol]);
        const variacion = prevVal > 0
          ? Math.round(((currVal - prevVal) / prevVal) * 10000) / 100
          : (currVal > 0 ? currVal * 100 : 0);
        return {
          Clave:       clave,
          Descripcion: String(c?.[descCol] || p?.[descCol] || p2?.[descCol] || ''),
          Anterior2:   prev2Val,
          Anterior:    prevVal,
          Actual:      currVal,
          Variacion:   variacion
        };
      })
      .sort((a, b) => b.Actual - a.Actual);
  }

  getCodeSummary(): { code: string; label: string; anterior2: number; anterior: number; actual: number; variacion: number }[] {
    return this.summarizeByCode(
      this.allCausasData,
      this.prevCausasData,
      this.is3YearMode ? this.prev2CausasData : {}
    );
  }

  /** Igual que getCodeSummary() pero parametrizado (evita duplicar la lógica de agregación). */
  private summarizeByCode(
    curr: { [code: string]: any[] }, prev: { [code: string]: any[] }, prev2: { [code: string]: any[] }
  ): { code: string; label: string; anterior2: number; anterior: number; actual: number; variacion: number }[] {
    return this.CODES.map(c => {
      const currRows  = (curr[c.value]  ?? []).filter((r: any) => !this.isRowTotal(r));
      const prevRows  = (prev[c.value]  ?? []).filter((r: any) => !this.isRowTotal(r));
      const prev2Rows = (prev2[c.value] ?? []).filter((r: any) => !this.isRowTotal(r));
      const countCol   = this.detectCountCol([...currRows, ...prevRows, ...prev2Rows]);
      const totalCurr  = currRows.reduce((s: number,  r: any) => s + this.parseNum(r[countCol]), 0);
      const totalPrev  = prevRows.reduce((s: number,  r: any) => s + this.parseNum(r[countCol]), 0);
      const totalPrev2 = prev2Rows.reduce((s: number, r: any) => s + this.parseNum(r[countCol]), 0);
      const variacion = totalPrev > 0
        ? Math.round(((totalCurr - totalPrev) / totalPrev) * 10000) / 100
        : (totalCurr > 0 ? totalCurr * 100 : 0);
      return { code: c.value, label: c.label, anterior2: totalPrev2, anterior: totalPrev, actual: totalCurr, variacion };
    });
  }

  private summarizeTotals(rows: { anterior2: number; anterior: number; actual: number; variacion: number }[]) {
    const anterior2 = rows.reduce((s, r) => s + r.anterior2, 0);
    const anterior  = rows.reduce((s, r) => s + r.anterior, 0);
    const actual    = rows.reduce((s, r) => s + r.actual, 0);
    const variacion = anterior > 0
      ? Math.round(((actual - anterior) / anterior) * 10000) / 100
      : (actual > 0 ? actual * 100 : 0);
    return { anterior2, anterior, actual, variacion };
  }

  private detectCountCol(rows: any[]): string {
    if (!rows.length) return '';
    const cols = Object.keys(rows[0]);
    return cols.find(c => c.toUpperCase().includes('CAUSA') && !c.includes('%'))
        ?? cols.find(c => !c.includes('%') && !/^(Sec|Clave|Descri)/i.test(c.trim()))
        ?? '';
  }

  private detectCol(rows: any[], keyword: string): string {
    if (!rows.length) return keyword;
    return Object.keys(rows[0]).find(c => c.toUpperCase().includes(keyword.toUpperCase())) ?? keyword;
  }

  // ── Detección de columnas ──────────────────────────────────────────────────

  private findCountColumn(): string {
    return (
      this.columns.find(c => c.toUpperCase().includes('CAUSA') && !c.includes('%')) ??
      this.columns.find(c => !c.includes('%') && !/^(SEC|NO\.?|CLAVE)/i.test(c.trim())) ??
      ''
    );
  }

  private findLabelColumn(): string {
    return (
      this.columns.find(c => c.toUpperCase().includes('CLAVE')) ??
      this.columns.find(c => c.toUpperCase().includes('SEC')) ??
      this.columns[0] ?? ''
    );
  }

  private findDescColumn(): string {
    return this.columns.find(c => c.toUpperCase().includes('DESCRI')) ?? '';
  }

  // ── Cálculo Pareto 80/20 ───────────────────────────────────────────────────

  private computePareto(): void {
    const countCol = this.findCountColumn();
    if (!countCol) { this.paretoRows = []; return; }

    const rows = this.tableData
      .filter(r => !this.isRowTotal(r))
      .map(r => ({ ...r, _val: this.parseNum(r[countCol]) }))
      .filter(r => r._val > 0)
      .sort((a, b) => b._val - a._val);

    const total = rows.reduce((s, r) => s + r._val, 0);
    let cumulative = 0;
    const result: any[] = [];

    for (const row of rows) {
      cumulative += row._val;
      result.push(row);
      if (total > 0 && cumulative / total >= 0.80) break;
    }

    this.paretoRows  = result;
    this.paretoShown = result.length;
    this.paretoTotal = rows.length;
    this.paretoPct   = total > 0 ? Math.round((cumulative / total) * 100) : 0;
  }

  // ── Scraping año actual (2026) ────────────────────────────────────────────

  iniciarScrapingAll(): void {
    this.status         = 'LOADING';
    this.errorMessage   = '';
    this.tableData      = [];
    this.columns        = [];
    this.paretoRows     = [];
    this.allCausasData   = {};
    this.prevCausasData  = {};
    this.prev2CausasData = {};
    this.compareYear     = 0;
    this.compare2Year    = 0;
    this.loadingOffset   = 0;
    this.pieYear         = 0;
    this.codeSummary     = [];
    this.compareRows     = [];
    if (this.paretoChart) { this.paretoChart.destroy(); this.paretoChart = null; }

    const { desde, hasta } = this.dateRange.current;
    this.dashboardService.getCausasAll(undefined, this.selectedZona, desde, hasta)
      .pipe(timeout({ each: SCRAPE_TIMEOUT_MS }))
      .subscribe({
        next: (data) => {
          this.allCausasData = this.stripDescPrefix(data ?? {});
          const first = this.CODES.find(c => (data[c.value] ?? []).length > 0);
          this.switchCode(first?.value ?? this.CODES[0].value);
          this.status = 'SUCCESS';
          // El scraping acaba de poblar el caché de zonas en el backend; refrescamos el dropdown
          this.dashboardService.getZonas().subscribe({
            next: zonas => { if (zonas?.length > 1) this.zonas = zonas; },
            error: () => {}
          });
        },
        error: (err) => this.handleScrapeError(err, this.currentYear)
      });
  }

  // ── Comparar con un año anterior (escalable: usa offset dinámico) ─────────
  // yearOffset=1 → currentYear-1 (ej. 2025); yearOffset=2 → currentYear-2 (ej. 2024)
  // El año actual viene de la fecha "hasta" del selector → el año siguiente funciona solo.

  iniciarComparacionAnio(yearOffset: number): void {
    if (!this.isAllMode) {
      this.status       = 'ERROR';
      this.errorMessage = 'Primero presiona "Consultar" para cargar los datos del año actual.';
      return;
    }

    this.status        = 'LOADING';
    this.errorMessage  = '';
    this.loadingOffset = yearOffset;
    const targetYear   = this.currentYear - yearOffset;

    const { desde, hasta } = this.dateRange.current;
    const shiftYear = (d: string) => `${Number(d.slice(0, 4)) - yearOffset}${d.slice(4)}`;

    // offset=1 → siempre slot primario.
    // offset=2 → slot secundario si el primario ya tiene datos; si no, usa el primario.
    const useSlot2 = yearOffset === 2 && this.compareYear > 0;

    this.dashboardService.getCausasAll(undefined, this.selectedZona, shiftYear(desde), shiftYear(hasta))
      .pipe(timeout({ each: SCRAPE_TIMEOUT_MS }))
      .subscribe({
        next: (data) => {
          if (useSlot2) {
            this.prev2CausasData = this.stripDescPrefix(data ?? {});
            this.compare2Year    = targetYear;
          } else {
            this.prevCausasData = this.stripDescPrefix(data ?? {});
            this.compareYear    = targetYear;
          }
          this.pieYear       = this.currentYear;
          this.loadingOffset = 0;
          this.status        = 'SUCCESS';
          this.switchCode(this.activeCode);
        },
        error: (err) => {
          if (useSlot2) {
            this.compare2Year = 0;
          } else {
            this.compareYear  = 0;
          }
          this.loadingOffset = 0;
          this.handleScrapeError(err, targetYear);
        }
      });
  }

  /** Mensaje de error unificado; distingue el timeout de un error del servidor. */
  private handleScrapeError(err: any, year: number): void {
    this.status = 'ERROR';
    if (err?.name === 'TimeoutError') {
      this.errorMessage =
        `La consulta de ${year} tardó demasiado (el portal de CFE no respondió a tiempo). ` +
        `Vuelve a intentarlo en un momento.`;
      return;
    }
    const detail = err?.error ?? err?.message ?? '';
    this.errorMessage = detail ? `Error: ${detail}` : `Error al obtener datos de ${year}.`;
  }

  switchCode(code: string): void {
    this.activeCode = code;
    const data = this.allCausasData[code] ?? [];
    this.columns   = data.length > 0 ? Object.keys(data[0]) : [];
    this.tableData = data;
    this.paretoRows = [];
    // Precalcula las tablas comparativas UNA vez (no en cada ciclo de cambios de Angular).
    this.recomputeCompareTables();
    if (this.paretoChart) { this.paretoChart.destroy(); this.paretoChart = null; }
    if (data.length > 0) {
      this.computePareto();
      setTimeout(() => this.buildParetoChart(), 150);
    }
  }

  /**
   * Recalcula y cachea las tablas comparativas. Antes la plantilla llamaba a
   * getCodeSummary()/getCompareRows() en cada binding, lo que las recalculaba en
   * cada ciclo de detección de cambios y congelaba la página al comparar.
   */
  private recomputeCompareTables(): void {
    if (this.isCompareMode) {
      this.codeSummary = this.getCodeSummary();
      this.compareRows = this.getCompareRows(this.activeCode);
      // Causas que solo aparecen en un año (derivado de compareRows).
      this.soloAnterior = this.compareRows
        .filter(r => r.Anterior > 0 && r.Actual === 0)
        .map(r => ({ Clave: r.Clave, Descripcion: r.Descripcion, valor: r.Anterior }));
      this.soloActual = this.compareRows
        .filter(r => r.Actual > 0 && r.Anterior === 0)
        .map(r => ({ Clave: r.Clave, Descripcion: r.Descripcion, valor: r.Actual }));
    } else {
      this.codeSummary  = [];
      this.compareRows  = [];
      this.soloAnterior = [];
      this.soloActual   = [];
    }
  }

  // ── Construcción de gráfica ────────────────────────────────────────────────

  onChartTypeChange(type: 'bar' | 'line' | 'horizontalBar' | 'pie'): void {
    this.chartType = type;
    if (this.paretoRows.length > 0) setTimeout(() => this.buildParetoChart(), 50);
  }

  onPieYearChange(year: number): void {
    this.pieYear = year;
    if (this.paretoRows.length > 0) setTimeout(() => this.buildParetoChart(), 50);
  }

  private buildParetoChart(): void {
    if (!this.paretoRows.length) return;

    const labelCol = this.findLabelColumn();
    const descCol  = this.findDescColumn();

    const keys   = this.paretoRows.map(r => String(r[labelCol] ?? '').trim());
    const descs  = this.paretoRows.map(r => descCol ? String(r[descCol] ?? '').trim() : '');
    const values = this.paretoRows.map(r => r._val);

    // Etiquetas multilinea: [clave, descripcion] para barras/línea
    const barLabels: (string | string[])[] = keys.map((k, i) => {
      const d = descs[i];
      if (!d) return k;
      const shortDesc = d.length > 22 ? d.slice(0, 21) + '…' : d;
      return [k, shortDesc];
    });

    // Modo comparación: valores del año anterior y (en modo 3 años) de 2 años atrás
    let valuesPrev:  number[] = [];
    let valuesPrev2: number[] = [];
    let bgColors: string[] = [];
    if (this.isCompareMode) {
      const prev: any[] = this.prevCausasData[this.activeCode] ?? [];
      const allRows = [...this.tableData, ...prev];
      const prevClaveCol = this.detectCol(allRows, 'CLAVE');
      const prevCountCol = this.detectCountCol(allRows);
      const prevMap = new Map<string, number>(
        prev.map((r: any) => [String(r[prevClaveCol] ?? '').trim(), this.parseNum(r[prevCountCol])])
      );
      valuesPrev = keys.map(k => prevMap.get(k) ?? 0);

      if (this.is3YearMode) {
        const prev2: any[] = this.prev2CausasData[this.activeCode] ?? [];
        const allRows2 = [...this.tableData, ...prev2];
        const prev2ClaveCol = this.detectCol(allRows2, 'CLAVE');
        const prev2CountCol = this.detectCountCol(allRows2);
        const prev2Map = new Map<string, number>(
          prev2.map((r: any) => [String(r[prev2ClaveCol] ?? '').trim(), this.parseNum(r[prev2CountCol])])
        );
        valuesPrev2 = keys.map(k => prev2Map.get(k) ?? 0);
      }

      bgColors = values.map((vCurr, i) => {
        const vPrev = valuesPrev[i];
        if (vCurr > vPrev) return 'rgba(220,53,69,0.09)';
        if (vCurr < vPrev) return 'rgba(40,167,69,0.09)';
        return 'rgba(108,117,125,0.05)';
      });
    }

    if (this.paretoChart) this.paretoChart.destroy();

    const titleText = `Pareto 80/20 — ${this.paretoShown} de ${this.paretoTotal} causas = ${this.paretoPct}% del total`;

    const tooltipTitle = (items: any[]) => {
      const idx = items[0]?.dataIndex;
      return descs[idx ?? 0] || keys[idx ?? 0];
    };

    // ── PASTEL ────────────────────────────────────────────────────────────────
    if (this.chartType === 'pie') {
      const activePieYear = this.isCompareMode ? this.pieYear : this.currentYear;
      const pieValues  = (this.is3YearMode && activePieYear === this.compare2Year) ? valuesPrev2
                       : (this.isCompareMode && activePieYear === this.compareYear) ? valuesPrev
                       : values;
      const pieLabels  = keys.map((k, i) => descs[i] ? `${k} — ${descs[i]}` : k);
      const colors     = keys.map((_, i) => PIE_COLORS[i % PIE_COLORS.length]);
      const totalPie   = pieValues.reduce((a, b) => a + b, 0);
      const pieTitle   = `${titleText}${this.isCompareMode ? ` — ${activePieYear}` : ''}`;
      this.paretoChart = new Chart('causasChart', {
        type: 'pie',
        data: {
          labels: pieLabels,
          datasets: [{
            data: pieValues,
            backgroundColor: colors,
            borderColor: '#ffffff',
            borderWidth: 2,
            hoverOffset: 8
          }]
        },
        options: {
          responsive: true,
          maintainAspectRatio: false,
          plugins: {
            title: { display: true, text: pieTitle, font: { size: 14, weight: 'bold' }, padding: { bottom: 12 } },
            legend: { position: 'right' as const },
            tooltip: {
              callbacks: {
                label: (ctx: any) => {
                  const pct = totalPie > 0 ? ((ctx.parsed / totalPie) * 100).toFixed(1) : '0.0';
                  const lbl = descs[ctx.dataIndex] || ctx.label;
                  return `  ${lbl}: ${ctx.parsed.toLocaleString()} (${pct}%)`;
                }
              }
            }
          }
        },
        plugins: [PIE_DATALABELS]
      } as any);
      return;
    }

    // ── BARRAS / LÍNEA / BARRAS HORIZONTALES ──────────────────────────────────
    const isHorizontal = this.chartType === 'horizontalBar';
    const isLine       = this.chartType === 'line';
    const resolvedType = isHorizontal ? 'bar' : this.chartType;

    // Per-bar border color: red if actual > prev, green if actual < prev
    const barBorderColor: any = (!isLine && this.isCompareMode)
      ? values.map((vCurr, i) => {
          const vPrev = valuesPrev[i] ?? 0;
          if (vCurr > vPrev) return 'rgb(220, 53, 69)';
          if (vCurr < vPrev) return 'rgb(40, 167, 69)';
          return 'rgb(107, 114, 128)';
        })
      : 'rgb(37, 99, 235)';

    const barBorderWidth: any = (!isLine && this.isCompareMode)
      ? values.map((vCurr, i) => (vCurr !== (valuesPrev[i] ?? 0) ? 2.5 : 1))
      : (isLine ? 2.5 : 1);

    const datasets: any[] = [{
      label: String(this.currentYear),
      data: values,
      backgroundColor: isLine ? 'rgba(59, 130, 246, 0.10)' : 'rgba(59, 130, 246, 0.78)',
      borderColor: barBorderColor,
      borderWidth: barBorderWidth,
      tension: 0.35,
      fill: isLine,
      pointRadius:          isLine ? 5 : undefined,
      pointHoverRadius:     isLine ? 8 : undefined,
      pointBackgroundColor: isLine ? 'rgb(37, 99, 235)' : undefined,
    }];

    if (this.isCompareMode && !isLine) {
      datasets.push({
        label: String(this.compareYear),
        data: valuesPrev,
        backgroundColor: 'rgba(156, 163, 175, 0.65)',
        borderColor: 'rgb(107, 114, 128)',
        borderWidth: 1,
      });
      if (this.is3YearMode) {
        datasets.push({
          label: String(this.compare2Year),
          data: valuesPrev2,
          backgroundColor: 'rgba(251, 146, 60, 0.65)',
          borderColor: 'rgb(234, 88, 12)',
          borderWidth: 1,
        });
      }
    }

    const config: any = {
      type: resolvedType,
      data: { labels: barLabels, datasets },
      options: {
        responsive: true,
        maintainAspectRatio: false,
        plugins: {
          title: { display: true, text: titleText, font: { size: 14, weight: 'bold' }, padding: { bottom: 12 } },
          legend: { position: 'top' as const },
          tooltip: { callbacks: { title: tooltipTitle } },
          ...(this.isCompareMode && bgColors.length ? { bgColumns: { colors: bgColors } } : {})
        },
        scales: {
          x: { ticks: { maxRotation: 45, minRotation: 0 }, grid: { display: false } },
          y: { beginAtZero: true, grid: { color: 'rgba(0,0,0,0.06)' } }
        }
      },
      plugins: isLine ? [] : (this.isCompareMode ? [BG_COLUMNS_PLUGIN, BAR_DATALABELS] : [BAR_DATALABELS])
    };

    if (isHorizontal) {
      config.options.indexAxis = 'y';
      config.options.scales = {
        x: { beginAtZero: true, grid: { color: 'rgba(0,0,0,0.06)' } },
        y: { grid: { display: false } }
      };
    }

    this.paretoChart = new Chart('causasChart', config);
  }

  // ── Exportar ───────────────────────────────────────────────────────────────

  exportChart(): void {
    const canvas = document.getElementById('causasChart') as HTMLCanvasElement;
    if (!canvas) return;
    const off = document.createElement('canvas');
    off.width = canvas.width; off.height = canvas.height;
    const offCtx = off.getContext('2d')!;
    offCtx.fillStyle = '#ffffff';
    offCtx.fillRect(0, 0, off.width, off.height);
    offCtx.drawImage(canvas, 0, 0);
    const link = document.createElement('a');
    link.href = off.toDataURL('image/png');
    link.download = 'pareto_causas.png';
    link.click();
  }

  volver(): void {
    this.router.navigate(['/dashboard']);
  }

  // ── Totales de las tablas ───────────────────────────────────────────────────

  get tableCountColumn(): string { return this.detectCountCol(this.tableData); }
  get hasPortalTotalRow(): boolean { return this.tableData.some(r => this.isRowTotal(r)); }

  /** Valor de la fila TOTAL para la tabla completa: 'TOTAL' en la 1ª col, suma en la col de conteo. */
  tableFooterValue(col: string, index: number): string {
    if (index === 0) return 'TOTAL';
    if (col === this.tableCountColumn) {
      const sum = this.tableData
        .filter(r => !this.isRowTotal(r))
        .reduce((s, r) => s + this.parseNum(r[col]), 0);
      return sum.toLocaleString('es-MX');
    }
    return '';
  }

  get compareTotals(): { anterior2: number; anterior: number; actual: number; variacion: number } {
    const anterior2 = this.compareRows.reduce((s, r) => s + r.Anterior2, 0);
    const anterior  = this.compareRows.reduce((s, r) => s + r.Anterior, 0);
    const actual    = this.compareRows.reduce((s, r) => s + r.Actual, 0);
    const variacion = anterior > 0
      ? Math.round(((actual - anterior) / anterior) * 10000) / 100
      : (actual > 0 ? actual * 100 : 0);
    return { anterior2, anterior, actual, variacion };
  }

  get codeSummaryTotals(): { anterior2: number; anterior: number; actual: number; variacion: number } {
    return this.summarizeTotals(this.codeSummary);
  }

  // ── Exportar a Excel con color ──────────────────────────────────────────────

  /** Escribe una hoja con estilos: encabezado oscuro, relleno verde/rojo y fila TOTAL. */
  private exportColored(
    filename: string,
    sheet: string,
    headers: string[],
    matrix: { v: any; t: 's' | 'n'; rgb?: string | null; align?: string; bold?: boolean }[][]
  ): void {
    const wb = XLSX.utils.book_new();
    const ws: any = {};
    const enc = (r: number, c: number) => XLSX.utils.encode_cell({ r, c });
    const HEADER_S = {
      fill: { patternType: 'solid', fgColor: { rgb: 'FF1E293B' } },
      font: { bold: true, color: { rgb: 'FFFFFFFF' }, sz: 11 },
      alignment: { horizontal: 'center', vertical: 'center' }
    };
    headers.forEach((h, c) => ws[enc(0, c)] = { v: h, t: 's', s: HEADER_S });
    matrix.forEach((cells, ri) => {
      const r = ri + 1;
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
    });
    ws['!ref']  = XLSX.utils.encode_range({ s: { c: 0, r: 0 }, e: { c: headers.length - 1, r: matrix.length } });
    ws['!cols'] = headers.map((_, i) => (i === 1 ? { wch: 40 } : { wch: 14 }));
    XLSX.utils.book_append_sheet(wb, ws, sheet);
    saveWorkbook(wb, filename);
  }

  /** Exporta la tabla de comparación del código activo (con color y fila TOTAL). */
  exportCompareExcel(): void {
    if (!this.compareRows.length) return;
    const red = 'FFFEE2E2', green = 'FFD1FAE5';
    const y2 = String(this.compare2Year), y0 = String(this.compareYear), y1 = String(this.currentYear);
    const t  = this.compareTotals;

    if (this.is3YearMode) {
      const matrix: any[][] = this.compareRows.map(r => ([
        { v: r.Clave,           t: 's', align: 'left' },
        { v: r.Descripcion,     t: 's', align: 'left' },
        { v: r.Anterior2,       t: 'n' },
        { v: r.Anterior,        t: 'n' },
        { v: r.Actual,          t: 'n', rgb: r.Actual > r.Anterior ? red : r.Actual < r.Anterior ? green : null },
        { v: `${r.Variacion}%`, t: 's', rgb: r.Variacion > 0 ? red : r.Variacion < 0 ? green : null },
      ]));
      matrix.push([
        { v: 'TOTAL', t: 's', bold: true, align: 'left' },
        { v: '', t: 's', bold: true },
        { v: t.anterior2, t: 'n', bold: true },
        { v: t.anterior,  t: 'n', bold: true },
        { v: t.actual,    t: 'n', bold: true },
        { v: `${t.variacion}%`, t: 's', bold: true },
      ]);
      this.exportColored(`comparacion_causas_${this.activeCode}.xlsx`, `Comparacion ${this.activeCode}`,
        ['Clave', 'Descripción', y2, y0, y1, 'Variación %'], matrix);
    } else {
      const matrix: any[][] = this.compareRows.map(r => ([
        { v: r.Clave,           t: 's', align: 'left' },
        { v: r.Descripcion,     t: 's', align: 'left' },
        { v: r.Anterior,        t: 'n' },
        { v: r.Actual,          t: 'n', rgb: r.Actual > r.Anterior ? red : r.Actual < r.Anterior ? green : null },
        { v: `${r.Variacion}%`, t: 's', rgb: r.Variacion > 0 ? red : r.Variacion < 0 ? green : null },
      ]));
      matrix.push([
        { v: 'TOTAL', t: 's', bold: true, align: 'left' },
        { v: '', t: 's', bold: true },
        { v: t.anterior, t: 'n', bold: true },
        { v: t.actual,   t: 'n', bold: true },
        { v: `${t.variacion}%`, t: 's', bold: true },
      ]);
      this.exportColored(`comparacion_causas_${this.activeCode}.xlsx`, `Comparacion ${this.activeCode}`,
        ['Clave', 'Descripción', y0, y1, 'Variación %'], matrix);
    }
  }

  /** Exporta el resumen comparativo de todos los códigos (con color y fila TOTAL). */
  exportResumenExcel(): void {
    if (!this.codeSummary.length) return;
    const red = 'FFFEE2E2', green = 'FFD1FAE5';
    const y2 = String(this.compare2Year), y0 = String(this.compareYear), y1 = String(this.currentYear);
    const t  = this.codeSummaryTotals;

    if (this.is3YearMode) {
      const matrix: any[][] = this.codeSummary.map(r => ([
        { v: r.code,            t: 's', align: 'left' },
        { v: r.label,           t: 's', align: 'left' },
        { v: r.anterior2,       t: 'n' },
        { v: r.anterior,        t: 'n' },
        { v: r.actual,          t: 'n', rgb: r.actual > r.anterior ? red : r.actual < r.anterior ? green : null },
        { v: `${r.variacion}%`, t: 's', rgb: r.variacion > 0 ? red : r.variacion < 0 ? green : null },
      ]));
      matrix.push([
        { v: 'TOTAL', t: 's', bold: true, align: 'left' },
        { v: '', t: 's', bold: true },
        { v: t.anterior2, t: 'n', bold: true },
        { v: t.anterior,  t: 'n', bold: true },
        { v: t.actual,    t: 'n', bold: true },
        { v: `${t.variacion}%`, t: 's', bold: true },
      ]);
      this.exportColored('resumen_causas.xlsx', 'Resumen',
        ['Código', 'Descripción', y2, y0, y1, 'Variación %'], matrix);
    } else {
      const matrix: any[][] = this.codeSummary.map(r => ([
        { v: r.code,            t: 's', align: 'left' },
        { v: r.label,           t: 's', align: 'left' },
        { v: r.anterior,        t: 'n' },
        { v: r.actual,          t: 'n', rgb: r.actual > r.anterior ? red : r.actual < r.anterior ? green : null },
        { v: `${r.variacion}%`, t: 's', rgb: r.variacion > 0 ? red : r.variacion < 0 ? green : null },
      ]));
      matrix.push([
        { v: 'TOTAL', t: 's', bold: true, align: 'left' },
        { v: '', t: 's', bold: true },
        { v: t.anterior, t: 'n', bold: true },
        { v: t.actual,   t: 'n', bold: true },
        { v: `${t.variacion}%`, t: 's', bold: true },
      ]);
      this.exportColored('resumen_causas.xlsx', 'Resumen',
        ['Código', 'Descripción', y0, y1, 'Variación %'], matrix);
    }
  }

  exportExcel(): void {
    const ws = XLSX.utils.json_to_sheet(this.tableData);
    const wb = XLSX.utils.book_new();
    XLSX.utils.book_append_sheet(wb, ws, 'Causas');
    saveWorkbook(wb, 'causas.xlsx');
  }
}
