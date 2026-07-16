import { Component, OnInit, ViewChild, ElementRef, ChangeDetectorRef, OnDestroy  } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { Chart, ChartConfiguration } from 'chart.js/auto';
import * as XLSX from 'xlsx-js-style';
import { AuthService } from '../services/auth.service';
import { NavComponent } from '../shared/nav.component';
import { DateRangeBarComponent } from '../shared/date-range-bar.component';
import { DateRangeService } from '../services/date-range.service';
import { saveWorkbook } from '../shared/excel-export';
import { InconformidadesMetaService } from '../services/inconformidades-meta.service';
import { DashboardService } from '../services/dashboard.service';
import { HttpClient } from '@angular/common/http';
import { Subscription } from 'rxjs';

// Plugin: valores numéricos encima de cada barra (responsive según ancho de barra)
const META_DATALABELS_PLUGIN: any = {
  id: 'metaDatalabels',
  afterDatasetsDraw(chart: any) {
    const { ctx } = chart;
    const isHoriz = chart.options?.indexAxis === 'y';
    chart.data.datasets.forEach((dataset: any, i: number) => {
      if (dataset.type === 'line') return;
      const meta = chart.getDatasetMeta(i);
      if (meta.hidden) return;
      meta.data.forEach((bar: any, j: number) => {
        const value = dataset.data[j];
        if (!value) return;
        const v = Number(value);
        const label = v.toLocaleString('es-MX', { maximumFractionDigits: 2 });
        const barSize = isHoriz ? Math.abs(bar.height ?? 12) : Math.abs(bar.width ?? 12);
        const fontSize = Math.max(7, Math.min(11, Math.floor(barSize * 0.55)));
        ctx.save();
        ctx.font = `bold ${fontSize}px sans-serif`;
        ctx.fillStyle = '#222';
        ctx.textAlign = 'center';
        if (isHoriz) {
          ctx.textBaseline = 'middle';
          const labelW = ctx.measureText(label).width;
          ctx.fillText(label, bar.x + labelW / 2 + 3, bar.y);
        } else {
          ctx.textBaseline = 'bottom';
          ctx.fillText(label, bar.x, bar.y - 2);
        }
        ctx.restore();
      });
    });
  }
};

// Plugin: fondo de columna rojo/verde por zona (igual que en Causas/IMU)
const META_BG_COLUMNS_PLUGIN: any = {
  id: 'metaBgColumns',
  beforeDatasetsDraw(chart: any) {
    if (chart.config.type !== 'bar') return;
    const { ctx, chartArea } = chart;
    if (!chartArea) return;
    const colors: string[] = chart.options?.plugins?.metaBgColumns?.colors ?? [];
    if (!colors.length) return;
    const isHoriz = chart.options.indexAxis === 'y';
    // Solo consideramos datasets de tipo barra (excluye la línea % Diferencia)
    const metas = (chart.data.datasets as any[])
      .map((ds: any, di: number) => ({ ds, meta: chart.getDatasetMeta(di) }))
      .filter(({ ds, meta }: any) => ds.type === 'bar' && !meta.hidden)
      .map(({ meta }: any) => meta);
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

@Component({
  selector: 'app-inconformidades-meta',
  standalone: true,
  imports: [CommonModule, FormsModule, NavComponent, DateRangeBarComponent],
  templateUrl: './inconformidades-meta.component.html',
  styleUrls: ['./inconformidades-meta.component.css']
})

export class InconformidadesMetaComponent implements OnInit, OnDestroy {
    @ViewChild('myChart', { static: false }) chartRef!:  ElementRef<HTMLCanvasElement>; 

  /**
   * El problema reportado es que Chart.js a veces intenta renderizar antes de que
   * Angular termine de pintar el canvas. Usamos esta bandera para renderizar
   * en el primer ciclo posterior cuando ya exista el canvas.
   */
  pendingChartRender = false;


  ngAfterViewInit() {
    // no renderizamos aquí: el canvas puede requerir un ciclo extra tras el fetch
  }

  ngAfterViewChecked(): void {
    if (!this.pendingChartRender) return;
    if (!this.chartRef?.nativeElement) return;
    if (!this.chartData) return;

    // Render solo una vez por cada “Generar Dashboard”
    this.pendingChartRender = false;

    // Si ya existe, lo destruimos y recreamos con el tipo actual
    if (this.chartInstance) {
      this.chartInstance.destroy();
      this.chartInstance = null;
    }

    this.renderChart();
  }




  // ── State variables ────────────────────────────────────────────────────────
  status: string = 'ESPERANDO';
  totalRecords: number = 0;
  tableDetected: string = 'NO';
  errorMessage: string = '';
  rawData: any[] = [];
  tableColumns: string[] = [];
  chartData: any = null;
  chartInstance: Chart | null = null;

  // ── Mapeo código de división → nombre visible (solo presentación) ──────────────
  // Internamente se siguen usando los códigos (DC010, etc.) para datos/consultas.
  private readonly DIVISION_NAMES: { [code: string]: string } = {
    DC010: 'CHIHUAHUA',  DC020: 'CUAUHTEMOC', DC040: 'JUAREZ',
    DC060: 'DELICIAS',   DC140: 'CASAS GRANDES', DC220: 'TORREON',
    DC240: 'PARRAL',     DC260: 'DURANGO',    DC270: 'GOMEZ PALACIO',
    TOTAL: 'TOTAL'
  };

  /** Devuelve el nombre de la división para mostrar; si no es un código conocido, regresa el texto original. */
  divName(code: any): string {
    const key = String(code ?? '').trim().toUpperCase();
    return this.zoneNames[key] ?? this.DIVISION_NAMES[key] ?? String(code ?? '');
  }

  // Colores de la gráfica (azul / rojo / verde + línea roja para % Diferencia)
  private readonly COLOR_REAL2025 = 'rgb(46, 117, 182)';
  private readonly COLOR_META2026 = 'rgb(192, 48, 56)';
  private readonly COLOR_REAL2026 = 'rgb(112, 173, 71)';
  private readonly COLOR_LINEA    = 'rgb(237, 28, 36)';

  rawTable: any[] = [];

  // Mapa código zona → nombre legible, poblado dinámicamente desde /api/data/zonas
  private zoneNames: { [code: string]: string } = {};

  private rangeSub?: Subscription;

  constructor(
    private metaRealService: InconformidadesMetaService,
    private dashboardService: DashboardService,
    public auth: AuthService,
    private router: Router,
    private http: HttpClient,
    private cdr: ChangeDetectorRef,
    private dateRange: DateRangeService
  ) {
    // Igual que en causas/IMU: si el usuario ya generó el dashboard y luego
    // cambia el rango de fechas global, recargamos con el rango nuevo.
    // Ignoramos la primera emisión (la inicial, al entrar a la página).
    let first = true;
    this.rangeSub = this.dateRange.range$.subscribe(() => {
      if (first) { first = false; return; }
      if (this.rawTable.length > 0) this.loadData();
    });
  }

  ngOnInit(): void {
    this.dashboardService.getZonas().subscribe({
      next: (zonas) => {
        this.zoneNames = {};
        for (const z of (zonas ?? [])) {
          if (z.value) this.zoneNames[z.value.trim().toUpperCase()] = z.label?.trim() ?? z.value;
        }
      },
      error: () => {}
    });
  }

  private desiredColumns = ['DC010', 'DC020', 'DC040', 'DC060', 'DC140', 'DC220', 'DC240', 'DC260', 'DC270', 'TOTAL'];
  private desiredRowMatchers = [
    { label: 'REAL 2025', regex: /REAL\s*2025|2025\s*REAL/i },
    { label: 'META 2026', regex: /META\s*2026|2026\s*META/i },
    { label: 'REAL 2026', regex: /REAL\s*2026|2026\s*REAL/i },
    { label: 'DIFERENCIA META', regex: /DIFERENCIA.*META|META.*DIFERENCIA|DIFERE.*META/i }
  ];

    ngOnDestroy() {
      if (this.chartInstance) {
        this.chartInstance.destroy();
      }
      this.rangeSub?.unsubscribe();
    }
  // ── Fetch and process live data from backend ─────────────────────────────────
  loadData(): void {
    this.status = 'CARGANDO';
    this.errorMessage = 'Obteniendo datos Meta-Real desde CFE...';
    this.rawData = [];
    this.tableColumns = [];
    this.chartData = null;
    this.totalRecords = 0;
    this.tableDetected = 'NO';

  const { desde, hasta } = this.dateRange.current;
  this.metaRealService.obtenerDatosAsync(desde, hasta).subscribe({
      next: (response: any) => {
        if (!response) {
          this.status = 'ERROR';
          this.errorMessage = 'Respuesta inválida del servidor.';
          return;
        }

        // backend actual devuelve: { labels, datasets, totalRecords, status, rawTable }
        // y NO devuelve response.data/rawTable con la misma estructura anterior.
        const rawTable = response.rawTable ?? response.data ?? [];
        const dataCount = rawTable?.length ?? 0;

        if (!rawTable || dataCount === 0) {
          this.status = 'ERROR';
          this.errorMessage = 'No se obtuvo información de la tabla. Verifica la conexión o inténtalo de nuevo.';
          return;
        }

        // Compatibilidad: si viene con rawTable/labels/datasets, lo usamos directo.
        if (response.datasets && response.labels && response.rawTable) {
          this.rawTable = response.rawTable;
          this.totalRecords = response.totalRecords ?? dataCount;
          this.tableDetected = 'SI';
          this.status = response.status ?? 'EXITO';

          // Para la gráfica: armamos la combinada (3 barras + línea % Diferencia)
          this.chartData = this.buildMetaChartData(response.labels, response.datasets);
          this.pendingChartRender = true;

          return;
        }

        // Ruta legacy (si el backend devolviera response.data)
        const legacyData = response.data;
        if (!legacyData || legacyData.length === 0) {
          this.status = 'ERROR';
          this.errorMessage = 'No se obtuvo información de la tabla. Verifica la conexión o inténtalo de nuevo.';
          return;
        }

        const rowLabelKey = response.rowLabelKey || Object.keys(response.data[0] || {})[0] || 'Periodo';


        const filtered = response.data
          .map((row: any) => {
            const normalizedRow: any = {};
            const keys = Object.keys(row);
            if (keys.length > 0 && (!rowLabelKey || rowLabelKey.trim() === '')) {
              normalizedRow[rowLabelKey] = row[keys[0]];
              for (let i = 1; i < keys.length; i++) normalizedRow[keys[i]] = row[keys[i]];
            } else {
              normalizedRow[rowLabelKey] = row[rowLabelKey];
              for (const key of keys) {
                if (key !== rowLabelKey) normalizedRow[key] = row[key];
              }
            }
            return normalizedRow;
          })
          .filter((row: any) => {
            const label = String(row[rowLabelKey] || '').trim().toUpperCase();
            return this.desiredRowMatchers.some(matcher => matcher.regex.test(label));
          });

        if (filtered.length === 0) {
          this.status = 'ERROR';
          this.errorMessage = 'No se encontraron las filas de datos requeridas. Verifica los nombres de fila en la tabla original.';
          return;
        }

        this.rawData = filtered;
        this.tableDetected = response.tableDetected ? 'SI' : 'NO';
        this.totalRecords = this.rawData.length;
        this.tableColumns = [rowLabelKey, ...this.desiredColumns];

        this.processChartData(this.rawData);
        // El render del chart se hace en ngAfterViewChecked para asegurar que el canvas ya está listo
        this.pendingChartRender = true;
        this.status = 'EXITO';
        this.errorMessage = `Datos obtenidos correctamente. Última actualización: ${response.lastUpdated ?? ''}`;
      },
      error: (err: any) => {
        this.status = 'ERROR';
        this.errorMessage = this.extractErrorMessage(err);
      }
    });
  }

  private extractErrorMessage(error: any): string {
    if (!error) return 'Error desconocido al obtener datos.';
    if (error.error?.details) return `Error al obtener datos: ${error.error.details}`;
    if (error.message) return `Error al obtener datos: ${error.message}`;
    return 'Error al obtener datos desde el servidor.';
  }

  private parseNumericValue(value: any): number {
    if (value == null) return 0;
    if (typeof value === 'number') return value;

    const str = String(value)
      .replace(/\./g, '')
      .replace(/,/g, '.')
      .replace(/[^0-9.\-]/g, '')
      .trim();

    const parsed = Number(str);
    return Number.isFinite(parsed) ? parsed : 0;
  }

  // ── Process data for chart ─────────────────────────────────────────────────
  private processChartData(data: any[]): void {
    if (!data || data.length === 0) {
      this.chartData = null;
      return;
    }

    const rowLabelKey = this.tableColumns[0] || Object.keys(data[0] || {})[0];
    const normalizedColumns = this.tableColumns.map(c => c.trim().toUpperCase());
    const numericKeys = this.desiredColumns
      .map(desired => {
        const index = normalizedColumns.indexOf(desired.toUpperCase());
        return index >= 0 ? this.tableColumns[index] : null;
      })
      .filter((key): key is string => key !== null);
    const categoryLabels = numericKeys;

    const datasets = this.desiredRowMatchers
      .map((matcher, rowIndex) => {
        const row = data.find((item: any) => {
          const label = String(item[rowLabelKey] || '').trim().toUpperCase();
          return matcher.regex.test(label);
        });
        if (!row) return null;

        const values = numericKeys.map(key => this.parseNumericValue(row[key]));
        return {
          label: matcher.label,
          data: values,
          backgroundColor: this.getDatasetColor(rowIndex),
          borderColor: this.getDatasetBorderColor(rowIndex),
          borderWidth: 1
        };
      })
      .filter((dataset: any) => dataset !== null);

    this.chartData = {
      labels: categoryLabels.map(c => this.divName(c)),
      datasets
    };

    // Sugiere mostrar % dentro de las barras/pastel
    // (en Chart.js: para barra usamos datalabels manual via canvas en plugins custom)

  }



  private getDatasetColor(index: number): string {
    // Colores pedidos:
    // REAL 2025 (azul), META 2026 (rojo), REAL 2026 (verde)
    const colors = [
      'rgb(0, 68, 255)',  // dataset 0 -> REAL 2025 (azul)
      'rgb(255, 0, 55)',  // dataset 1 -> META 2026 (rojo)
      'rgb(0, 68, 255)',  // dataset 2 -> REAL 2026 (verde)
      'rgb(201, 203, 207)'  // fallback
    ];
    return colors[index] ?? colors[colors.length - 1];
  }

  private getDatasetBorderColor(index: number): string {
    const colors = [
      'rgb(0, 68, 255)',
      'rgb(255, 0, 55)',
      'rgb(0, 68, 255)',
      'rgb(201, 203, 207)'
    ];
    return colors[index] ?? colors[colors.length - 1];
  }

  /**
   * Construye la gráfica combinada (barras + línea) a partir de lo que manda el
   * backend (labels = códigos de división, datasets = filas REAL/META).
   * Solo presentación: no cambia datos ni cálculos del backend.
   *  - 3 barras: REAL 2025 (azul), META 2026 (rojo), REAL 2026 (verde)
   *  - 1 línea: % Diferencia (índice 2026 vs 2025) en eje secundario
   *  - etiquetas X con el nombre de la división (no el código)
   *  - se excluye la columna TOTAL del gráfico (sí se ve en la tabla)
   */
  private buildMetaChartData(labels: string[], datasets: any[]): any {
    const safeLabels = labels ?? [];
    const valuesFor = (re: RegExp): number[] => {
      const ds = (datasets ?? []).find(d => re.test(String(d?.label ?? '')));
      return safeLabels.map((_, i) => Number(ds?.data?.[i]) || 0);
    };

    // Quitamos la columna TOTAL del gráfico
    const keep = safeLabels.map(l => String(l ?? '').trim().toUpperCase() !== 'TOTAL');
    const flt  = (arr: number[]) => arr.filter((_, i) => keep[i]);

    const r25raw = flt(valuesFor(/REAL\s*2025/i));
    const m26raw = flt(valuesFor(/META\s*2026/i));
    const r26raw = flt(valuesFor(/REAL\s*2026/i));
    const nombresRaw = safeLabels.filter((_, i) => keep[i]).map(c => this.divName(c));

    // % Diferencia antes de ordenar
    const diffRaw = r26raw.map((r26, i) =>
      r26 !== 0 ? Math.round(((r26 - r25raw[i]) / r26) * 10000) / 100 : 0
    );

    // Ordenar índices por REAL 2026 de mayor a menor (izq → der)
    const idx = r26raw
      .map((v, i) => ({ v, i }))
      .sort((a, b) => b.v - a.v)
      .map(x => x.i);

    const reorder = (arr: number[]) => idx.map(i => arr[i]);
    const real25  = reorder(r25raw);
    const meta26  = reorder(m26raw);
    const real26  = reorder(r26raw);
    const diffPct = reorder(diffRaw);
    const nombres = idx.map(i => nombresRaw[i]);

    // Fondo de columna: rojo si REAL 2026 > META 2026, verde si no superó la meta
    const bgColors = real26.map((r26, i) =>
      r26 > meta26[i] ? 'rgba(220, 53, 69, 0.12)' : 'rgba(40, 167, 69, 0.12)'
    );

    return {
      labels: nombres,
      bgColors,
      datasets: [
        { type: 'bar', label: 'REAL 2025', data: real25,
          backgroundColor: this.COLOR_REAL2025, borderColor: this.COLOR_REAL2025, borderWidth: 1, order: 2, yAxisID: 'y' },
        { type: 'bar', label: 'META 2026', data: meta26,
          backgroundColor: this.COLOR_META2026, borderColor: this.COLOR_META2026, borderWidth: 1, order: 2, yAxisID: 'y' },
        { type: 'bar', label: 'REAL 2026', data: real26,
          backgroundColor: this.COLOR_REAL2026, borderColor: this.COLOR_REAL2026, borderWidth: 1, order: 2, yAxisID: 'y' },
        { type: 'line', label: '% Diferencia', data: diffPct, yAxisID: 'y1',
          borderColor: this.COLOR_LINEA, backgroundColor: this.COLOR_LINEA, borderWidth: 2,
          pointRadius: 4, pointHoverRadius: 6, pointBackgroundColor: this.COLOR_LINEA, pointBorderColor: '#fff',
          tension: 0.4, fill: false, order: 1 }
      ]
    };
  }
  

  // ── Chart rendering ───────────────────────────────────────────────────────
  // Solo dos orientaciones disponibles en el desplegable: vertical / horizontal.
  chartType: 'bar' | 'horizontalBar' = 'bar';



  // render chart con el diseño consistente y permitiendo cambiar orientación

  renderChart(): void {
    if (!this.chartRef?.nativeElement || !this.chartData) return;

    const canvas = this.chartRef.nativeElement;
    const ctx = canvas.getContext('2d');
    if (!ctx) return;

    // Destruir la instancia anterior
    if (this.chartInstance) {
      this.chartInstance.destroy();
      this.chartInstance = null;
    }

    const horizontal = this.chartType === 'horizontalBar';

    // Clonamos los datasets y asignamos el eje de valor según la orientación.
    // Vertical  -> el valor va en el eje Y (bars: 'y', línea %: 'y1')
    // Horizontal-> el valor va en el eje X (bars: 'xVal', línea %: 'xPct')
    const datasets = (this.chartData.datasets || []).map((ds: any) => {
      const isLine = ds.type === 'line';
      const copy = { ...ds };
      delete copy.xAxisID;
      delete copy.yAxisID;
      if (horizontal) {
        copy.xAxisID = isLine ? 'xPct' : 'xVal';
      } else {
        copy.yAxisID = isLine ? 'y1' : 'y';
      }
      return copy;
    });

    const data = { labels: this.chartData.labels, datasets };

    const valueAxis = {
      type: 'linear',
      beginAtZero: true,
      title: { display: true, text: 'INCONFORMIDADES', color: '#2f5496', font: { weight: 'bold' } }
    };
    const pctAxis = {
      type: 'linear',
      title: { display: true, text: '% VARIACION', color: '#c00000', font: { weight: 'bold' } },
      grid: { drawOnChartArea: false }
    };
    const catAxis = { ticks: { autoSkip: false, maxRotation: 0, minRotation: 0 }, grid: { display: false } };

    const scales: any = horizontal
      ? {
          xVal: { ...valueAxis, position: 'bottom' },
          xPct: { ...pctAxis, position: 'top' },
          y:    { ...catAxis }
        }
      : {
          y:  { ...valueAxis, position: 'left' },
          y1: { ...pctAxis, position: 'right' },
          x:  { ...catAxis }
        };

    const bgColors: string[] = this.chartData.bgColors ?? [];

    // Gráfica combinada: barras (REAL/META) + línea (% Diferencia) en eje secundario.
    const config: any = {
      type: 'bar',
      data,
      plugins: [META_BG_COLUMNS_PLUGIN, META_DATALABELS_PLUGIN],
      options: {
        indexAxis: horizontal ? 'y' : 'x',
        responsive: true,
        maintainAspectRatio: false,
        interaction: { mode: 'index', intersect: false },
        plugins: {
          metaBgColumns: { colors: bgColors },
          legend: { display: true, position: 'bottom', labels: { usePointStyle: true, padding: 14 } },
          title: { display: true, text: 'INCONFORMIDADES', font: { size: 16, weight: 'bold' }, color: '#2f5496' },
          tooltip: {
            callbacks: {
              title: (items: any[]) => items?.[0]?.label ?? '',
              label: (item: any) => {
                const v = horizontal ? (item.parsed?.x ?? 0) : (item.parsed?.y ?? 0);
                if (item.dataset?.label === '% Diferencia') {
                  return ` % Diferencia : ${Number(v).toFixed(2)}`;
                }
                return ` ${item.dataset?.label} : ${Number(v).toLocaleString('es-MX', { maximumFractionDigits: 2 })}`;
              }
            }
          }
        },
        scales
      }
    };

    this.chartInstance = new Chart(ctx, config);
  }

  // ── Helpers para colorear la fila REAL 2026 vs META 2026 ─────────────────────
  /** ¿La clave de columna corresponde a una división/total con valor numérico? */
  private isValueColumn(key: any): boolean {
    const k = String(key ?? '').trim().toUpperCase();
    // Hardcoded Norte + patrón genérico D<letra><3 dígitos> (DC010, DA020, etc.) + TOTAL
    return this.desiredColumns.includes(k) || /^D[A-Z]\d{3}$/.test(k) || k === 'TOTAL';
  }

  /** Texto de la primera columna (etiqueta de fila, p. ej. "REAL 2026"). */
  private rowLabel(row: any): string {
    const keys = Object.keys(row || {});
    return String(row?.[keys[0]] ?? '');
  }

  /** Fila META 2026 dentro de la tabla cruda. */
  private metaRow2026(): any {
    return this.rawTable.find(r => /META\s*2026/i.test(this.rowLabel(r)));
  }

  /** ¿Es la fila REAL 2026? */
  isReal2026Row(row: any): boolean {
    return /REAL\s*2026/i.test(this.rowLabel(row));
  }

  /**
   * Clase para cada celda de la tabla. Solo aplica a las celdas numéricas
   * de la fila REAL 2026: rojo si supera META 2026, verde si es menor.
   */
  cellClass(row: any, key: any): string {
    if (!this.isReal2026Row(row) || !this.isValueColumn(key)) return '';
    const meta = this.metaRow2026();
    if (!meta) return '';
    const realVal = this.parseNumericValue(row[key]);
    const metaVal = this.parseNumericValue(meta[key]);
    if (realVal > metaVal) return 'cell-red';
    if (realVal < metaVal) return 'cell-green';
    return '';
  }

  // ── Export chart as PNG ────────────────────────────────────────────────────
  exportChart(): void {
    if (!this.chartInstance) {
      alert('No hay gráfica para exportar');
      return;
    }
    try {
      const canvas = this.chartInstance.canvas;
      const off = document.createElement('canvas');
      off.width = canvas.width; off.height = canvas.height;
      const offCtx = off.getContext('2d')!;
      offCtx.fillStyle = '#ffffff';
      offCtx.fillRect(0, 0, off.width, off.height);
      offCtx.drawImage(canvas, 0, 0);
      const link = document.createElement('a');
      link.href = off.toDataURL('image/png');
      link.download = `inconformidades-meta-${new Date().getTime()}.png`;
      link.click();
    } catch (error) {
      console.error('Error exporting chart:', error);
      alert('Error al exportar la gráfica');
    }
  }

  // ── Export data as Excel ───────────────────────────────────────────────────
  exportExcel(): void {
    if (!this.rawTable || this.rawTable.length === 0) {
      alert('No hay datos para exportar');
      return;
    }

    try {
      // Exportamos la misma tabla que se muestra en pantalla.
      const ws = XLSX.utils.json_to_sheet(this.rawTable);
      const wb = XLSX.utils.book_new();
      XLSX.utils.book_append_sheet(wb, ws, 'Inconformidades Meta');

      const cols = Object.keys(this.rawTable[0]);
      const labelKey = cols[0];

      // Encabezado azul con texto blanco.
      const headerStyle = {
        fill: { fgColor: { rgb: 'FF4472C4' } },
        font: { bold: true, color: { rgb: 'FFFFFFFF' } },
        alignment: { horizontal: 'center' }
      };
      for (let C = 0; C < cols.length; C++) {
        const address = XLSX.utils.encode_cell({ r: 0, c: C });
        if (ws[address]) ws[address].s = headerStyle;
      }

      // Colorea la fila REAL 2026 comparando contra META 2026 (rojo > / verde <).
      const metaRow = this.metaRow2026();
      const realIdx = this.rawTable.findIndex(r => this.isReal2026Row(r));
      if (metaRow && realIdx >= 0) {
        const redStyle = { fill: { fgColor: { rgb: 'FFFFC7CE' } }, font: { color: { rgb: 'FF9C0006' }, bold: true } };
        const greenStyle = { fill: { fgColor: { rgb: 'FFC6EFCE' } }, font: { color: { rgb: 'FF006100' }, bold: true } };
        for (let C = 0; C < cols.length; C++) {
          const key = cols[C];
          if (!this.isValueColumn(key)) continue;
          const realVal = this.parseNumericValue(this.rawTable[realIdx][key]);
          const metaVal = this.parseNumericValue(metaRow[key]);
          const address = XLSX.utils.encode_cell({ r: realIdx + 1, c: C });
          if (!ws[address]) continue;
          if (realVal > metaVal) ws[address].s = redStyle;
          else if (realVal < metaVal) ws[address].s = greenStyle;
        }
      }

      // Ancho de columnas.
      ws['!cols'] = cols.map(() => ({ wch: 14 }));

      const fileName = `inconformidades-meta-${new Date().getTime()}.xlsx`;
      saveWorkbook(wb, fileName);
    } catch (error) {
      console.error('Error exporting Excel:', error);
      alert('Error al exportar a Excel');
    }
  }

  // ── Navigation ─────────────────────────────────────────────────────────────
  goBack(): void {
    this.router.navigate(['/dashboard']);
  }
}