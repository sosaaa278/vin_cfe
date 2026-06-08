import { Component, OnInit, ViewChild, ElementRef, ChangeDetectorRef, OnDestroy  } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { Chart, ChartConfiguration } from 'chart.js/auto';
import * as XLSX from 'xlsx-js-style';
import { AuthService } from '../services/auth.service';
import { InconformidadesMetaService } from '../services/inconformidades-meta.service';
import { HttpClient } from '@angular/common/http'  

@Component({
  selector: 'app-inconformidades-meta',
  standalone: true,
  imports: [CommonModule, FormsModule],
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
  chartInstance: Chart<'bar'> | null = null;
  
  rawTable: any[] = [];
  constructor(
    private metaRealService: InconformidadesMetaService,
    public auth: AuthService,
    private router: Router,
    private http: HttpClient,
    private cdr: ChangeDetectorRef
  ) {}

  ngOnInit(): void {
    // No manual popup required: data is fetched automatically from the backend.
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

  this.metaRealService.obtenerDatosAsync().subscribe({
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

          // Para la gráfica: usar directamente la data de Chart.js si ya viene armada
          this.chartData = { labels: response.labels, datasets: response.datasets };
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

  private findValueColumns(headers: string[]): { metaKey: string | null; realKey: string | null } {
    const upperHeaders = headers.map(h => h.toUpperCase());
    const metaIndex = upperHeaders.findIndex(h => h.includes('META'));
    const realIndex = upperHeaders.findIndex(h => h.includes('REAL'));

    return {
      metaKey: metaIndex >= 0 ? headers[metaIndex] : null,
      realKey: realIndex >= 0 ? headers[realIndex] : null
    };
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

  // ── Process scraped data ───────────────────────────────────────────────────
  private processScrapedData(data: any[]): void {
    if (!data || data.length === 0) {
      this.status = 'ERROR';
      this.errorMessage = 'No se encontraron datos en la tabla';
      return;
    }

    try {
      this.rawData = data;
      this.totalRecords = this.rawData.length;
      this.tableDetected = 'SI';

      // Get table columns from first row
      if (this.rawData.length > 0) {
        this.tableColumns = Object.keys(this.rawData[0]);
      }

      // Process data for chart
      this.processChartData(this.rawData);

      this.status = 'EXITO';
    } catch (error: any) {
      this.status = 'ERROR';
      this.errorMessage = error?.message || 'Error al procesar los datos';
      console.error('Error processing data:', error);
    }
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
      labels: categoryLabels,
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
  

  // ── Chart rendering ───────────────────────────────────────────────────────
  chartType: 'bar' | 'line' | 'horizontalBar' | 'pie' = 'bar';



  // render chart con el diseño consistente y permitiendo cambiar tipo

  renderChart(): void {
    if (!this.chartRef?.nativeElement || !this.chartData) return;

    // Asegurar que el canvas tenga casi todo el ancho disponible
    // (el CSS del contenedor manda, aquí no tocamos layout agresivo)



  const canvas = this.chartRef?.nativeElement;
  const ctx = canvas.getContext('2d');
  if (!ctx) return;

  //  Destruir la instancia anterior
  if (this.chartInstance) {
    this.chartInstance.destroy();
  }

  //  Luego: Crear la nueva gráfica con la configuración en línea
    const normalizedType: any = this.chartType === 'horizontalBar' ? 'bar' : this.chartType;

    this.chartInstance = new Chart(ctx, {
      type: normalizedType,
      data: this.chartData,
      options: {

      responsive: true,
      maintainAspectRatio: true,
      plugins: {
        legend: { display: true, position: 'top' },
        title: { display: true, text: 'Inconformidades Meta Real' }
      },
      scales: {
        ...(normalizedType === 'bar'
          ? { x: { beginAtZero: true }, y: { ticks: { autoSkip: false } } }
          : {})
      },
      ...(normalizedType === 'bar' && this.chartType === 'horizontalBar'
        ? { indexAxis: 'y' }
        : {})
    }
  });
}

  // ── Export chart as PNG ────────────────────────────────────────────────────
  exportChart(): void {
    if (!this.chartInstance) {
      alert('No hay gráfica para exportar');
      return;
    }

    try {
        // TypeScript ya sabe que this.chartInstance no es null aquí
        const image = this.chartInstance.toBase64Image();
        const link = document.createElement('a');
        link.href = image;
        link.download = `inconformidades-meta-${new Date().getTime()}.png`;
        link.click();
      } catch (error) {
        console.error('Error exporting chart:', error);
        alert('Error al exportar la gráfica');
      }
    }

  // ── Export data as Excel ───────────────────────────────────────────────────
  exportExcel(): void {
    if (!this.rawData || this.rawData.length === 0) {
      alert('No hay datos para exportar');
      return;
    }

    try {
      // Create workbook
      const ws = XLSX.utils.json_to_sheet(this.rawData);
      const wb = XLSX.utils.book_new();
      XLSX.utils.book_append_sheet(wb, ws, 'Datos Meta-Real');

      // Style the header
      const headerStyle = {
        fill: { fgColor: { rgb: 'FF4472C4' } },
        font: { bold: true, color: { rgb: 'FFFFFFFF' } },
        alignment: { horizontal: 'center' }
      };

      const range = XLSX.utils.decode_range(ws['!ref'] || 'A1');
      for (let C = range.s.c; C <= range.e.c; ++C) {
        const address = XLSX.utils.encode_col(C) + '1';
        if (!ws[address]) continue;
        ws[address].s = headerStyle;
      }

      // Auto-fit columns
      ws['!cols'] = [];
      for (let i = 0; i < this.tableColumns.length; i++) {
        ws['!cols'].push({ wch: 15 });
      }

      // Generate file
      const fileName = `inconformidades-meta-${new Date().getTime()}.xlsx`;
      XLSX.writeFile(wb, fileName);
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