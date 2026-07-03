import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import Chart from 'chart.js/auto';
import * as XLSX from 'xlsx-js-style';
import { DashboardService } from '../services/dashboard.service';
import { AuthService } from '../services/auth.service';
import { DateRangeService } from '../services/date-range.service';
import { NavComponent } from '../shared/nav.component';
import { saveWorkbook } from '../shared/excel-export';
import { Subscription } from 'rxjs';

// ── Plugin: columna de fondo verde/roja por categoría (rojo si 2026 > 2025) ──────
const BG_COLUMNS_PLUGIN: any = {
  id: 'imuBgColumns',
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
        const left  = Math.min(...elements.map((e: any) => e.x - e.width / 2));
        const right = Math.max(...elements.map((e: any) => e.x + e.width / 2));
        ctx.fillRect(left, chartArea.top, right - left, chartArea.bottom - chartArea.top);
      }
      ctx.restore();
    });
  }
};

// ── Plugin: etiqueta de valor sobre cada barra ─────────────────────────────────
const BAR_DATALABELS_PLUGIN: any = {
  id: 'imuBarLabels',
  afterDatasetsDraw(chart: any) {
    if (chart.config.type !== 'bar') return;
    const { ctx } = chart;
    const isHoriz = chart.options.indexAxis === 'y';
    (chart.data.datasets as any[]).forEach((ds: any, di: number) => {
      const meta = chart.getDatasetMeta(di);
      if (meta.hidden) return;
      meta.data.forEach((el: any, idx: number) => {
        const val = ds.data[idx];
        if (val == null || val === 0) return;
        const txt = Number(val).toLocaleString('es-MX');
        ctx.save();
        ctx.fillStyle = '#1a202c';
        ctx.font = '600 10px "Segoe UI", sans-serif';
        if (isHoriz) {
          ctx.textAlign = 'left';  ctx.textBaseline = 'middle';
          ctx.fillText(txt, el.x + 4, el.y);
        } else {
          ctx.textAlign = 'center'; ctx.textBaseline = 'bottom';
          ctx.fillText(txt, el.x, el.y - 3);
        }
        ctx.restore();
      });
    });
  }
};

const PIE_COLORS = [
  'rgb(230, 57, 70)',  'rgb(0, 119, 255)',  'rgb(50, 205, 50)',  'rgb(255, 200, 0)',
  'rgb(128, 0, 128)',  'rgb(255, 102, 0)',  'rgb(0, 206, 209)',  'rgb(255, 20, 147)',
  'rgb(101, 67, 33)',  'rgb(64, 64, 64)',   'rgb(0, 180, 120)',  'rgb(200, 100, 0)',
];

// Vista "Inconformidades por cada mil usuarios" (IMU, MENSUAL).
// El scraping NO corre al entrar: el usuario elige zona y mes y pulsa "Consultar".
// Luego puede "Comparar con" el año anterior: grafica ambos años y colorea la tabla.
@Component({
  selector: 'app-imu',
  standalone: true,
  imports: [CommonModule, FormsModule, NavComponent],
  templateUrl: './imu.component.html',
  styleUrls: ['./imu.component.css']
})
export class ImuComponent implements OnInit {

  status: 'WAITING' | 'LOADING' | 'SUCCESS' | 'ERROR' = 'WAITING';
  errorMsg = '';

  columns: string[] = [];
  rows: any[] = [];       // año actual
  prevRows: any[] = [];   // año anterior (al comparar)
  compareMode = false;

  chart: any = null;
  chart2: any = null;   // segundo pastel (año anterior) cuando se compara
  chartType: 'bar' | 'line' | 'horizontalBar' | 'pie' = 'bar';

  // El año del reporte IMU sale de la fecha "hasta" del rango global.
  get currentYear(): number {
    return Number(this.dateRange.current.hasta.slice(0, 4)) || new Date().getFullYear();
  }

  zonas: { value: string; label: string }[] = [];
  zonasLoading = false;

  readonly MESES = [
    { value: '01', label: 'Enero' },      { value: '02', label: 'Febrero' },
    { value: '03', label: 'Marzo' },      { value: '04', label: 'Abril' },
    { value: '05', label: 'Mayo' },       { value: '06', label: 'Junio' },
    { value: '07', label: 'Julio' },      { value: '08', label: 'Agosto' },
    { value: '09', label: 'Septiembre' }, { value: '10', label: 'Octubre' },
    { value: '11', label: 'Noviembre' },  { value: '12', label: 'Diciembre' },
  ];

  selectedZona = '00000';
  selectedMes  = '01';

  private rangeSub?: Subscription;

  constructor(
    private dashboardService: DashboardService,
    private auth: AuthService,
    private dateRange: DateRangeService
  ) {
    this.zonas = this.auth.getZonas();
    // El mes inicial = mes de la fecha "hasta" del rango global.
    this.selectedMes = this.dateRange.current.hasta.slice(5, 7);

    // IMU es manual (no scrapea al entrar). Si el usuario ya consultó y luego
    // cambia el rango, actualizamos mes/año y recargamos. Ignoramos la 1ª emisión.
    let first = true;
    this.rangeSub = this.dateRange.range$.subscribe(r => {
      this.selectedMes = r.hasta.slice(5, 7);
      if (first) { first = false; return; }
      if (this.rows.length > 0) this.consultar();
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
    this.rangeSub?.unsubscribe();
  }

  get selectedZonaLabel(): string {
    return this.zonas.find(z => z.value === this.selectedZona)?.label ?? 'Todas las zonas';
  }
  get selectedMesLabel(): string {
    return this.MESES.find(m => m.value === this.selectedMes)?.label ?? '';
  }

  // ── Consultas ──────────────────────────────────────────────────────────────

  consultar(): void {
    this.compareMode = false;
    this.prevRows = [];
    this.status = 'LOADING';
    this.errorMsg = '';
    this.dashboardService.getImu(this.selectedZona, this.selectedMes, this.currentYear).subscribe({
      next: data => {
        this.rows = data ?? [];
        this.columns = this.rows.length > 0 ? Object.keys(this.rows[0]) : [];
        this.status = 'SUCCESS';
        this.renderChart();
      },
      error: err => this.fail(err)
    });
  }

  compararConAnterior(): void {
    if (this.rows.length === 0) return;
    this.status = 'LOADING';
    this.errorMsg = '';
    this.dashboardService.getImu(this.selectedZona, this.selectedMes, this.currentYear - 1).subscribe({
      next: data => {
        this.prevRows = data ?? [];
        this.compareMode = true;
        this.status = 'SUCCESS';
        this.renderChart();
      },
      error: err => this.fail(err)
    });
  }

  private fail(err: any): void {
    this.errorMsg = typeof err?.error === 'string'
      ? err.error
      : 'No se pudo cargar el reporte. Verifica la conexión/VPN a la red de CFE.';
    this.status = 'ERROR';
  }

  onChartTypeChange(t: 'bar' | 'line' | 'horizontalBar' | 'pie'): void {
    this.chartType = t;
    if (this.rows.length > 0) this.renderChart();
  }

  // ── Helpers ──────────────────────────────────────────────────────────────────

  isTotal(row: any): boolean {
    const v = String(row['AREA'] ?? row['CVE'] ?? '').trim().toLowerCase();
    return v === 'total';
  }
  private parseNum(v: any): number {
    return Number(String(v ?? '0').replace(/,/g, '')) || 0;
  }
  private rowLabel(row: any): string {
    return String(row['AREA'] ?? row['CVE'] ?? '').trim();
  }

  // Las columnas CVE y AREA son texto; el resto son numéricas (comparables).
  isNumericCol(col: string): boolean {
    return col !== 'CVE' && col !== 'AREA';
  }

  // Columna del total general usada para la gráfica.
  private totalColumn(cols: string[]): string | null {
    return cols.find(c => c.trim() === 'TOTAL GENERAL TOTAL')
        ?? cols.filter(c => c.toUpperCase().includes('TOTAL')).pop()
        ?? null;
  }

  // Columnas de la tabla del año anterior (2025).
  get prevColumns(): string[] {
    return this.prevRows.length > 0 ? Object.keys(this.prevRows[0]) : this.columns;
  }

  // Valor del año anterior para una fila/columna (busca por nombre de área).
  get2025Cell(row: any, col: string): number {
    const found = this.prevRows.find(r => this.rowLabel(r) === this.rowLabel(row));
    return found ? this.parseNum(found[col]) : 0;
  }

  // Compara una celda 2026 vs 2025: 'up' (subió), 'down' (bajó) o null.
  private cellCompare(row: any, col: string): 'up' | 'down' | null {
    if (!this.isNumericCol(col)) return null;
    const v26 = this.parseNum(row[col]);
    const v25 = this.get2025Cell(row, col);
    if (v26 > v25) return 'up';
    if (v26 < v25) return 'down';
    return null;
  }

  // Color de celda para la tabla comparativa: rojo si subió, verde si bajó.
  cellStyle(row: any, col: string): { [k: string]: string } {
    const c = this.cellCompare(row, col);
    if (c === 'up')   return { 'background-color': 'rgba(220,53,69,0.28)' };
    if (c === 'down') return { 'background-color': 'rgba(40,167,69,0.28)' };
    return {};
  }

  // ── Gráfica ───────────────────────────────────────────────────────────────────

  private renderChart(): void {
    const col = this.totalColumn(this.columns);
    if (!col) return;

    const dataRows   = this.rows.filter(r => !this.isTotal(r));
    const labels     = dataRows.map(r => this.rowLabel(r));
    const values2026 = dataRows.map(r => this.parseNum(r[col]));
    const values2025 = this.compareMode
      ? dataRows.map(r => this.get2025Cell(r, col))
      : dataRows.map(() => 0);

    setTimeout(() => {
      if (this.chart)  { this.chart.destroy();  this.chart  = null; }
      if (this.chart2) { this.chart2.destroy(); this.chart2 = null; }

      // Pastel comparando: dos pasteles lado a lado (año actual y anterior).
      if (this.chartType === 'pie' && this.compareMode) {
        this.chart  = new Chart('imuChart',  this.buildPieConfig(labels, values2026, this.currentYear));
        this.chart2 = new Chart('imuChart2', this.buildPieConfig(labels, values2025, this.currentYear - 1));
        return;
      }

      this.chart = new Chart('imuChart', this.buildChartConfig(labels, values2025, values2026));
    }, 0);
  }

  // Configuración de un pastel (un solo año).
  private buildPieConfig(labels: string[], values: number[], year: number): any {
    const colors = labels.map((_, i) => PIE_COLORS[i % PIE_COLORS.length]);
    const total  = values.reduce((a, b) => a + b, 0);
    return {
      type: 'pie',
      data: { labels, datasets: [{ data: values, backgroundColor: colors, borderColor: '#fff', borderWidth: 2 }] },
      options: {
        responsive: true,
        maintainAspectRatio: false,
        plugins: {
          title: { display: true, text: `${year}`, font: { size: 12, weight: 'bold' } },
          legend: { position: 'right' as const, labels: { boxWidth: 10, font: { size: 9 } } },
          tooltip: { callbacks: { label: (c: any) => {
            const pct = total > 0 ? ((c.parsed / total) * 100).toFixed(1) : '0.0';
            return `  ${c.label}: ${c.parsed.toLocaleString()} (${pct}%)`;
          } } }
        }
      }
    };
  }

  private buildChartConfig(labels: string[], values2025: number[], values2026: number[]): any {
    const titleBase = `IMU total por zona — ${this.selectedMesLabel} ${this.currentYear}`
                    + (this.compareMode ? ` vs ${this.currentYear - 1}` : '');

    // ── PASTEL (un solo año, sin comparar) ──────────────────────────────────────
    if (this.chartType === 'pie') {
      return this.buildPieConfig(labels, values2026, this.currentYear);
    }

    // ── BARRAS / LÍNEA / BARRAS HORIZONTALES ────────────────────────────────────
    const isHorizontal = this.chartType === 'horizontalBar';
    const isLine       = this.chartType === 'line';
    const resolvedType = isHorizontal ? 'bar' : this.chartType;

    // Fondo de columna rojo/verde según la condición (solo al comparar).
    const bgFills = this.compareMode ? values2026.map((v, i) =>
      v > values2025[i] ? 'rgba(220, 53, 69, 0.10)' :
      v < values2025[i] ? 'rgba(40, 167, 69, 0.10)' :
                          'rgba(108, 117, 125, 0.05)'
    ) : [];

    const datasets: any[] = [];
    if (this.compareMode) {
      datasets.push({
        label: `${this.currentYear - 1}`,
        data: values2025,
        backgroundColor: isLine ? 'rgba(107,114,128,0.10)' : 'rgba(156,163,175,0.65)',
        borderColor: 'rgb(107,114,128)',
        borderWidth: isLine ? 2 : 1,
        tension: 0.35, fill: isLine,
        pointRadius: isLine ? 4 : undefined, pointHoverRadius: isLine ? 7 : undefined,
        order: 1
      });
    }
    datasets.push({
      label: `${this.currentYear}`,
      data: values2026,
      backgroundColor: isLine ? 'rgba(59,130,246,0.10)' : 'rgba(59,130,246,0.78)',
      borderColor: isLine ? 'rgba(59,130,246,1)' : 'rgb(37,99,235)',
      borderWidth: isLine ? 2.5 : 1,
      tension: 0.35, fill: isLine,
      pointRadius: isLine ? 5 : undefined, pointHoverRadius: isLine ? 8 : undefined,
      order: 0
    });

    const config: any = {
      type: resolvedType,
      data: { labels, datasets },
      options: {
        responsive: true,
        maintainAspectRatio: false,   // respeta la altura fija de .chart-box (360px)
        interaction: { mode: 'index' as const, intersect: false },
        plugins: {
          bgColumns: isLine ? {} : { colors: bgFills },
          title: { display: true, text: titleBase, font: { size: 12, weight: 'bold' } },
          legend: { position: 'top' as const, labels: { usePointStyle: true, padding: 8, boxWidth: 12, font: { size: 11 } } },
          tooltip: { callbacks: { afterBody: (items: any[]) => {
            if (!this.compareMode) return [];
            const idx = items[0]?.dataIndex;
            if (idx === undefined) return [];
            const v25 = values2025[idx], v26 = values2026[idx];
            if (v25 <= 0) return [];
            const pct = (((v26 - v25) / v25) * 100).toFixed(1);
            const arrow = v26 > v25 ? '▲' : v26 < v25 ? '▼' : '→';
            return [`${arrow} Variación: ${pct}%`];
          } } }
        },
        scales: {
          x: { ticks: { maxRotation: 45, minRotation: 0, font: { size: 10 } }, grid: { display: false } },
          y: { beginAtZero: true, ticks: { font: { size: 10 } }, grid: { color: 'rgba(0,0,0,0.06)' } }
        }
      },
      plugins: isLine ? [] : [BG_COLUMNS_PLUGIN, BAR_DATALABELS_PLUGIN]
    };

    if (isHorizontal) {
      config.options.indexAxis = 'y';
      config.options.scales = {
        x: { beginAtZero: true, grid: { color: 'rgba(0,0,0,0.06)' } },
        y: { grid: { display: false } }
      };
    }

    return config;
  }

  // ── Exportar ──────────────────────────────────────────────────────────────────

  // Exporta la gráfica actual como imagen PNG con fondo blanco.
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
    link.download = `imu_grafica_${this.selectedZona}_${this.selectedMes}_${this.currentYear}.png`;
    link.click();
  }

  // Tabla 1: datos del año actual, sin color.
  exportExcel(): void {
    this.exportPlain(this.rows, 'IMU', `imu_${this.selectedZona}_${this.selectedMes}_${this.currentYear}.xlsx`);
  }

  // Tabla 3: datos del año anterior, sin color.
  exportExcel2025(): void {
    this.exportPlain(this.prevRows, `IMU_${this.currentYear - 1}`,
      `imu_${this.selectedZona}_${this.selectedMes}_${this.currentYear - 1}.xlsx`);
  }

  private exportPlain(rows: any[], sheet: string, fileName: string): void {
    const ws = XLSX.utils.json_to_sheet(rows);
    const wb = XLSX.utils.book_new();
    XLSX.utils.book_append_sheet(wb, ws, sheet);
    saveWorkbook(wb, fileName);
  }

  // Tabla 2: comparativa con las celdas pintadas de rojo/verde (igual que en pantalla).
  exportCompareExcel(): void {
    const cols = this.columns;
    const ws: any = {};

    const HEADER_S = {
      fill: { patternType: 'solid', fgColor: { rgb: 'FF006341' } },
      font: { bold: true, color: { rgb: 'FFFFFFFF' }, sz: 10 },
      alignment: { horizontal: 'center', vertical: 'center', wrapText: true }
    };
    cols.forEach((c, ci) => {
      ws[XLSX.utils.encode_cell({ r: 0, c: ci })] = { v: c, t: 's', s: HEADER_S };
    });

    this.rows.forEach((row, ri) => {
      const r = ri + 1;
      cols.forEach((col, ci) => {
        const cmp = this.cellCompare(row, col);
        const rgb = cmp === 'up' ? 'FFFEE2E2' : cmp === 'down' ? 'FFD1FAE5' : null;
        const isNum = this.isNumericCol(col);
        // Mantenemos el texto exacto del portal (sin redondear).
        ws[XLSX.utils.encode_cell({ r, c: ci })] = {
          v: String(row[col] ?? ''),
          t: 's',
          s: {
            fill: rgb ? { patternType: 'solid', fgColor: { rgb } } : undefined,
            alignment: { horizontal: isNum ? 'right' : 'left' }
          }
        };
      });
    });

    ws['!ref'] = XLSX.utils.encode_range({ s: { c: 0, r: 0 }, e: { c: cols.length - 1, r: this.rows.length } });

    const wb = XLSX.utils.book_new();
    XLSX.utils.book_append_sheet(wb, ws, 'Comparacion');
    saveWorkbook(wb, `imu_comparacion_${this.selectedZona}_${this.selectedMes}.xlsx`);
  }
}
