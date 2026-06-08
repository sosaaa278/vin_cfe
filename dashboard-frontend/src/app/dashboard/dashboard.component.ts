import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import Chart from 'chart.js/auto';
import * as XLSX from 'xlsx-js-style';
import { DashboardService } from '../services/dashboard.service';
import { AuthService } from '../services/auth.service';

// ── Plugin: columna de fondo verde/roja por categoría ─────────────────────────
const BG_COLUMNS_PLUGIN: any = {
  id: 'bgColumns',
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

    (chart.data.labels as string[]).forEach((_: string, idx: number) => {
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

// ── Plugin: etiqueta de valor sobre/al lado de cada barra ─────────────────────
const BAR_DATALABELS_PLUGIN: any = {
  id: 'barDataLabels',
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
          ctx.textAlign    = 'left';
          ctx.textBaseline = 'middle';
          ctx.fillText(txt, el.x + 4, el.y);
        } else {
          ctx.textAlign    = 'center';
          ctx.textBaseline = 'bottom';
          ctx.fillText(txt, el.x, el.y - 3);
        }
        ctx.restore();
      });
    });
  }
};

const PIE_COLORS = [
  'rgb(230, 57, 70)',
  'rgb(0, 119, 255)',
  'rgb(50, 205, 50)',
  'rgb(255, 200, 0)',
  'rgb(128, 0, 128)',
  'rgb(255, 102, 0)',
  'rgb(0, 206, 209)',
  'rgb(255, 20, 147)',
  'rgb(101, 67, 33)',
  'rgb(64, 64, 64)',
];

// ── Plugin: etiquetas de área y cantidad sobre cada rebanada de pastel ─────────
const PIE_DATALABELS_PLUGIN: any = {
  id: 'pieDatalabels',
  afterDatasetsDraw(chart: any) {
    if (chart.config.type !== 'pie') return;
    const { ctx } = chart;
    const ds     = chart.data.datasets[0];
    const meta   = chart.getDatasetMeta(0);
    const labels = chart.data.labels as string[];
    const total  = (ds.data as number[]).reduce((a: number, b: number) => a + b, 0);

    meta.data.forEach((arc: any, i: number) => {
      const val = ds.data[i] as number;
      if (!val || total === 0 || val / total < 0.035) return;

      const midAngle = arc.startAngle + (arc.endAngle - arc.startAngle) / 2;
      const r = arc.outerRadius * 0.62;
      const x = arc.x + Math.cos(midAngle) * r;
      const y = arc.y + Math.sin(midAngle) * r;

      const raw   = String(labels[i] ?? '');
      const short = raw.length > 13 ? raw.slice(0, 12) + '…' : raw;
      const valStr = val.toLocaleString('es-MX');

      ctx.save();
      ctx.textAlign    = 'center';
      ctx.textBaseline = 'middle';
      ctx.shadowColor  = 'rgba(0,0,0,0.55)';
      ctx.shadowBlur   = 3;
      ctx.fillStyle    = '#ffffff';
      ctx.font         = 'bold 9px "Segoe UI", sans-serif';
      ctx.fillText(short,  x, y - 7);
      ctx.fillText(valStr, x, y + 7);
      ctx.restore();
    });
  }
};

@Component({
  selector: 'app-dashboard',
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: './dashboard.component.html',
  styleUrls: ['./dashboard.component.css']
})
export class DashboardComponent implements OnInit {

  compareChart: any;
  compareData: any[] = [];
  chartType: 'bar' | 'line' | 'horizontalBar' | 'pie' = 'bar';
  pieYear: '2025' | '2026' = '2026';
  currentCompareMode: 'all' | 'code' = 'all';

  totalRecords = 0;
  tableDetected = 'NO';
  status = 'WAITING';
  tableData: any[] = [];
  columns: string[] = [];
  allCompareData: { [code: string]: any[] } = {};

  // KPI analytics
  selectedCode = '';
  kpiTotal2025 = 0;
  kpiTotal2026 = 0;
  kpiVariacionTotal = 0;
  kpiAreasAlza = 0;
  kpiAreasBaja = 0;
  kpiAreasSinCambio = 0;
  kpiWorstArea = '';
  kpiBestArea = '';
  hasKpi = false;

  constructor(
    private dashboardService: DashboardService,
    public auth: AuthService,
    private router: Router,
  ) {}

  ngOnInit(): void {
    this.loadCompare();
  }

  isTotal(area: any): boolean {
    return /^total$/i.test(String(area ?? '').trim());
  }

  readonly COMPARE_CODES = ['30202', 'E02', 'E03', 'E04', 'E05', 'E06', 'E07', 'Q07'];
  readonly VISIBLE_COMPARE_CODES = ['30202', 'E02', 'E03', 'E04', 'E05', 'E06', 'E07', 'Q07'];

  get hasCompareData(): boolean {
    return Object.keys(this.allCompareData).length > 0;
  }

  get visibleColumns(): string[] {
    const without = this.columns.filter(c => c !== '30202' && !/^total$/i.test(c.trim()));
    const idxE02 = without.indexOf('E02');
    if (idxE02 !== -1 && this.columns.includes('30202')) {
      without.splice(idxE02, 0, '30202');
    }
    return without;
  }

  get rawDataColumns(): string[] {
    const cols = this.columns.filter(c => c !== '30202');
    const idxE02 = cols.indexOf('E02');
    if (idxE02 !== -1 && this.columns.includes('30202')) {
      cols.splice(idxE02, 0, '30202');
    }
    return cols;
  }

  isCompareCode(col: string): boolean {
    return this.VISIBLE_COMPARE_CODES.includes(col);
  }

  get2025Value(area: string, code: string): number {
    const rows = this.allCompareData[code];
    if (!rows) return 0;
    const clean = String(area ?? '').trim();
    const found = rows.find((r: any) => String(r.area ?? '').trim() === clean);
    return this.parseValue(found?.total2025 ?? 0);
  }

  get2026CellStyle(row: any, col: string): { [key: string]: string } {
    if (!this.isCompareCode(col)) return {};
    const v25 = this.get2025Value(row['AREA'], col);
    const v26 = this.parseValue(row[col] ?? 0);
    if (v26 > v25) return { 'background-color': 'rgba(220,53,69,0.28)' };
    if (v26 < v25) return { 'background-color': 'rgba(40,167,69,0.28)' };
    return {};
  }

  get2025SubRowCellStyle(isFirst: boolean): { [key: string]: string } {
    return {
      'background-color': 'rgba(215,228,255,0.55)',
      'border-top': '1px dashed #9ca3af',
      ...(isFirst ? { 'border-left': '3px solid #6c757d' } : {})
    };
  }

  getZoneTotal2026(row: any): number {
    return this.VISIBLE_COMPARE_CODES.reduce((sum, code) => sum + this.parseValue(row[code] ?? 0), 0);
  }

  getZoneTotal2025(area: string): number {
    return this.VISIBLE_COMPARE_CODES.reduce((sum, code) => sum + this.get2025Value(area, code), 0);
  }

  getTotalZoneCellStyle2026(row: any): { [key: string]: string } {
    const v26 = this.getZoneTotal2026(row);
    const v25 = this.getZoneTotal2025(row['AREA']);
    const base: { [key: string]: string } = {
      'font-weight': 'bold',
      'border': '2px solid #343a40'
    };
    if (v26 > v25) return { ...base, 'background-color': 'rgba(220,53,69,0.38)' };
    if (v26 < v25) return { ...base, 'background-color': 'rgba(40,167,69,0.38)' };
    return { ...base, 'background-color': 'rgba(108,117,125,0.18)' };
  }

  get2025CellStyle(row: any, col: string): { [key: string]: string } {
    if (!this.isCompareCode(col)) return {};
    const v25 = this.get2025Value(row['AREA'], col);
    const v26 = this.parseValue(row[col] ?? 0);
    if (v26 > v25) return { 'background-color': 'rgba(220,53,69,0.18)' };
    if (v26 < v25) return { 'background-color': 'rgba(40,167,69,0.18)' };
    return {};
  }

  // =========================
  // SCRAPING
  // =========================

  loadData(): void {
    this.status = 'LOADING';
    this.dashboardService.getFullCompare().subscribe({
      next: (data) => {
        this.allCompareData = data.compare;
        if (data.rawData2026.length > 0) {
          this.tableData = data.rawData2026;
          this.columns = Object.keys(data.rawData2026[0]);
        }
        this.totalRecords = data.rawData2026.length;
        this.tableDetected = data.rawData2026.length > 0 ? 'YES' : 'NO';
        this.status = 'SUCCESS';
        const codes = Object.keys(data.compare);
        const defaultCode = codes.includes('E02') ? 'E02' : codes[0];
        if (defaultCode) this.loadCompareByCode(defaultCode);
      },
      error: () => { this.status = 'ERROR'; }
    });
  }

  // =========================
  // KPIs
  // =========================

  private parseValue(value: any): number {
    return Number(String(value ?? '0').replace(/,/g, '').replace('%', '')) || 0;
  }

  private computeKpis(data: any[]): void {
    const totalRow = data.find(x => this.isTotal(x.area));
    const rows = data.filter(x => !this.isTotal(x.area));

    if (totalRow) {
      this.kpiTotal2025 = this.parseValue(totalRow.total2025);
      this.kpiTotal2026 = this.parseValue(totalRow.total2026);
      this.kpiVariacionTotal = this.parseValue(totalRow.variacion);
      this.hasKpi = true;
    } else {
      this.kpiTotal2025 = rows.reduce((s, x) => s + this.parseValue(x.total2025), 0);
      this.kpiTotal2026 = rows.reduce((s, x) => s + this.parseValue(x.total2026), 0);
      this.kpiVariacionTotal = this.kpiTotal2025 > 0
        ? Math.round(((this.kpiTotal2026 - this.kpiTotal2025) / this.kpiTotal2025) * 10000) / 100
        : 0;
      this.hasKpi = rows.length > 0;
    }

    this.kpiAreasAlza = rows.filter(x =>
      this.parseValue(x.total2026) > this.parseValue(x.total2025)
    ).length;
    this.kpiAreasBaja = rows.filter(x =>
      this.parseValue(x.total2026) < this.parseValue(x.total2025)
    ).length;
    this.kpiAreasSinCambio = rows.filter(x =>
      this.parseValue(x.total2026) === this.parseValue(x.total2025)
    ).length;

    const withHistory = rows.filter(x => this.parseValue(x.total2025) > 0);
    const sorted = [...withHistory].sort((a, b) =>
      this.parseValue(b.variacion) - this.parseValue(a.variacion)
    );
    this.kpiWorstArea = sorted[0]?.area ?? '—';
    this.kpiBestArea  = sorted[sorted.length - 1]?.area ?? '—';
  }

  // =========================
  // CONSTRUCCIÓN DE GRÁFICA
  // =========================

  private buildChartConfig(
    labels: string[],
    values2025: number[],
    values2026: number[]
  ): any {
    const title = this.selectedCode
      ? `Inconformidad ${this.selectedCode} — Comparativo 2025 vs 2026`
      : 'Comparativo general 2025 vs 2026';

    // ── PASTEL ───────────────────────────────────────────────────────────────
    if (this.chartType === 'pie') {
      const colors = labels.map((_, i) => PIE_COLORS[i % PIE_COLORS.length]);
      const pieValues = this.pieYear === '2025' ? values2025 : values2026;
      const totalPie  = pieValues.reduce((a, b) => a + b, 0);
      return {
        type: 'pie',
        data: {
          labels,
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
          plugins: {
            title: {
              display: true,
              text: `${title} (distribución ${this.pieYear})`,
              font: { size: 14, weight: 'bold' },
              padding: { bottom: 12 }
            },
            legend: { position: 'right' as const },
            tooltip: {
              callbacks: {
                label: (ctx: any) => {
                  const pct = totalPie > 0
                    ? ((ctx.parsed / totalPie) * 100).toFixed(1) : '0.0';
                  return `  ${ctx.label}: ${ctx.parsed.toLocaleString()} (${pct}%)`;
                }
              }
            }
          }
        },
        plugins: [PIE_DATALABELS_PLUGIN]
      };
    }

    // ── BARRAS / LÍNEA / BARRAS HORIZONTALES ─────────────────────────────────
    const isHorizontal = this.chartType === 'horizontalBar';
    const isLine       = this.chartType === 'line';
    const resolvedType = isHorizontal ? 'bar' : this.chartType;

    const titleLines = title;

    // 2026 barras: azul sólido (distinto del fondo verde/rojo)
    const BG_2026_BAR     = 'rgba(59, 130, 246, 0.78)';
    const BORDER_2026_BAR = 'rgb(37, 99, 235)';

    // Colores condicionales solo para puntos de la gráfica de línea
    const bg2026Points = values2026.map((v, i) =>
      v > values2025[i] ? 'rgba(220, 53, 69, 0.78)'  :
      v < values2025[i] ? 'rgba(40, 167, 69, 0.78)'  :
                          'rgba(108, 117, 125, 0.78)'
    );

    // Colores de fondo de columna (muy suaves, misma condición)
    const bgFills = values2026.map((v, i) =>
      v > values2025[i] ? 'rgba(220, 53, 69, 0.09)'  :
      v < values2025[i] ? 'rgba(40, 167, 69, 0.09)'  :
                          'rgba(108, 117, 125, 0.05)'
    );

    // Colores de series: gris (2025) y azul (2026 en línea)
    const C25_BAR    = 'rgba(156, 163, 175, 0.65)';
    const C25_BORDER = 'rgb(107, 114, 128)';
    const C25_FILL   = 'rgba(107, 114, 128, 0.10)';
    const C26_LINE   = 'rgba(59, 130, 246, 1)';
    const C26_FILL   = 'rgba(59, 130, 246, 0.10)';

    const datasets: any[] = [
      {
        label: '2025',
        data: values2025,
        backgroundColor: isLine ? C25_FILL  : C25_BAR,
        borderColor:     C25_BORDER,
        borderWidth:     isLine ? 2     : 1,
        tension: 0.35,
        fill:    isLine,
        pointRadius:          isLine ? 4 : undefined,
        pointHoverRadius:     isLine ? 7 : undefined,
        pointBackgroundColor: isLine ? C25_BORDER : undefined,
        order: 1
      },
      {
        label: '2026',
        data: values2026,
        backgroundColor: isLine ? C26_FILL : BG_2026_BAR,
        borderColor:     isLine ? C26_LINE : BORDER_2026_BAR,
        borderWidth:     isLine ? 2.5 : 1,
        tension: 0.35,
        fill:    isLine,
        pointRadius:          isLine ? 5 : undefined,
        pointHoverRadius:     isLine ? 8 : undefined,
        pointBackgroundColor: isLine ? bg2026Points : undefined,
        order: 0
      }
    ];

    const config: any = {
      type: resolvedType,
      data: { labels, datasets },
      options: {
        responsive: true,
        interaction: { mode: 'index' as const, intersect: false },
        plugins: {
          bgColumns: isLine ? {} : { colors: bgFills },
          title: {
            display: true,
            text: titleLines,
            font: { size: 14, weight: 'bold' },
            padding: { bottom: 12 }
          },
          legend: {
            position: 'top' as const,
            labels: { usePointStyle: true, padding: 16 }
          },
          tooltip: {
            callbacks: {
              afterBody: (items: any[]) => {
                const idx = items[0]?.dataIndex;
                if (idx === undefined) return [];
                const v25 = values2025[idx];
                const v26 = values2026[idx];
                if (v25 <= 0) return [];
                const pct   = (((v26 - v25) / v25) * 100).toFixed(1);
                const arrow = v26 > v25 ? '▲' : v26 < v25 ? '▼' : '→';
                return [`${arrow} Variación: ${pct}%`];
              }
            }
          }
        },
        scales: {
          x: { ticks: { maxRotation: 45, minRotation: 0 }, grid: { display: false } },
          y: { beginAtZero: true, grid: { color: 'rgba(0,0,0,0.06)' } }
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

  private renderCompareChart(data: any[], _useHighlight = false): void {
    this.compareData = data;
    this.computeKpis(data);

    const areaData  = data.filter(x => !this.isTotal(x.area));
    const totalRow  = data.find(x => this.isTotal(x.area));
    const isBarType = this.chartType === 'bar' || this.chartType === 'horizontalBar';

    // Solo las gráficas de barras incluyen la fila TOTAL al final
    const chartData  = isBarType && totalRow
      ? [...areaData, { ...totalRow, area: 'TOTAL' }]
      : areaData;

    const labels     = chartData.map(x => x.area);
    const values2025 = chartData.map(x => this.parseValue(x.total2025));
    const values2026 = chartData.map(x => this.parseValue(x.total2026));

    if (this.compareChart) this.compareChart.destroy();
    this.compareChart = new Chart(
      'compareChart',
      this.buildChartConfig(labels, values2025, values2026)
    );
  }

  // =========================
  // COMPARATIVO TOTAL (DB)
  // =========================

  loadCompare(): void {
    this.currentCompareMode = 'all';
    this.selectedCode = '';
    this.dashboardService.getCompareData().subscribe(data => {
      this.renderCompareChart(data);
    });
  }

  // =========================
  // COMPARATIVO POR CÓDIGO
  // =========================

  loadCompareByCode(code: string): void {
    this.currentCompareMode = 'code';
    this.selectedCode = code;

    const cached = this.allCompareData[code];
    if (cached && cached.length > 0) {
      this.renderCompareChart(cached, true);
      return;
    }

    this.dashboardService.getCompareByCode(code).subscribe(data => {
      this.renderCompareChart(data, true);
    });
  }

  onChartTypeChange(chartType: 'bar' | 'line' | 'horizontalBar' | 'pie'): void {
    this.chartType = chartType;
    if (this.compareData.length > 0) {
      this.renderCompareChart(this.compareData, this.currentCompareMode === 'code');
    }
  }

  onPieYearChange(year: '2025' | '2026'): void {
    this.pieYear = year;
    if (this.compareData.length > 0) {
      this.renderCompareChart(this.compareData, this.currentCompareMode === 'code');
    }
  }

  // =========================
  // EXPORTAR
  // =========================

  goToInconformidadesMeta(): void {
    this.router.navigate(['/inconformidades-meta']);
  }

  verCausas(): void {
    this.router.navigate(['/causas']);
  }



  exportSubRowExcel(): void {
    const data: any[] = [];
    this.tableData.forEach(row => {
      const entry2026: any = { AÑO: '2026' };
      this.visibleColumns.forEach(col => entry2026[col] = row[col]);
      data.push(entry2026);
      if (!this.isTotal(row['AREA'])) {
        const entry2025: any = { AÑO: '2025' };
        this.visibleColumns.forEach(col => {
          if (col === 'AREA') entry2025[col] = row[col];
          else if (this.isCompareCode(col)) entry2025[col] = this.get2025Value(row['AREA'], col);
          else entry2025[col] = '';
        });
        data.push(entry2025);
      }
    });
    const ws = XLSX.utils.json_to_sheet(data);
    const wb = XLSX.utils.book_new();
    XLSX.utils.book_append_sheet(wb, ws, 'Comparativo_Inconformidades');
    XLSX.writeFile(wb, 'comparativo_inconformidades.xlsx');
  }

  exportExcel(): void {
    const ws = XLSX.utils.json_to_sheet(this.tableData);
    const wb = XLSX.utils.book_new();
    XLSX.utils.book_append_sheet(wb, ws, 'Dashboard');
    XLSX.writeFile(wb, 'dashboard.xlsx');
  }

  exportCompareExcel(): void {
    const wb = XLSX.utils.book_new();
    const ws: any = {};

    // Encabezados con fondo oscuro y texto blanco
    const HEADER_S = {
      fill: { patternType: 'solid', fgColor: { rgb: 'FF1E293B' } },
      font: { bold: true, color: { rgb: 'FFFFFFFF' }, sz: 11 },
      alignment: { horizontal: 'center', vertical: 'center' }
    };
    ['Área', '2025', '2026', 'Variación %'].forEach((h, ci) => {
      ws[XLSX.utils.encode_cell({ r: 0, c: ci })] = { v: h, t: 's', s: HEADER_S };
    });

    this.compareData.forEach((item, ri) => {
      const row       = ri + 1;
      const isTot     = this.isTotal(item.area);
      const v26       = this.parseValue(item.total2026);
      const v25       = this.parseValue(item.total2025);
      const varr      = this.parseValue(item.variacion);

      // Rojo suave si empeoró, verde suave si mejoró
      const numRgb = !isTot && v26 > v25  ? 'FFFEE2E2' :
                     !isTot && v26 < v25  ? 'FFD1FAE5' : null;
      const varRgb = !isTot && varr > 0   ? 'FFFEE2E2' :
                     !isTot && varr < 0   ? 'FFD1FAE5' : null;

      const makeS = (rgb: string | null, align: string = 'center') => ({
        fill: rgb ? { patternType: 'solid', fgColor: { rgb } } : undefined,
        font: isTot ? { bold: true, sz: 11 } : { sz: 11 },
        alignment: { horizontal: align, vertical: 'center' }
      });

      ws[XLSX.utils.encode_cell({ r: row, c: 0 })] = { v: item.area,              t: 's', s: makeS(null, 'left') };
      ws[XLSX.utils.encode_cell({ r: row, c: 1 })] = { v: item.total2025,         t: 'n', s: makeS(numRgb) };
      ws[XLSX.utils.encode_cell({ r: row, c: 2 })] = { v: item.total2026,         t: 'n', s: makeS(numRgb) };
      ws[XLSX.utils.encode_cell({ r: row, c: 3 })] = { v: `${item.variacion}%`,   t: 's', s: makeS(varRgb) };
    });

    ws['!ref']  = XLSX.utils.encode_range({ s: { c: 0, r: 0 }, e: { c: 3, r: this.compareData.length } });
    ws['!cols'] = [{ wch: 32 }, { wch: 12 }, { wch: 12 }, { wch: 14 }];

    XLSX.utils.book_append_sheet(wb, ws, 'Comparativo');
    XLSX.writeFile(wb, 'comparativo_2025_2026.xlsx');
  }

  exportChart(): void {
    const canvas = document.getElementById('compareChart') as HTMLCanvasElement;
    const link   = document.createElement('a');
    link.href     = canvas.toDataURL('image/png');
    link.download = `comparativo_${this.selectedCode || 'general'}.png`;
    link.click();
  }
  
}
