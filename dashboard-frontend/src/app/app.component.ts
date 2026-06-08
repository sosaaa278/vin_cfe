import { Component } from '@angular/core';
import { RouterOutlet } from '@angular/router';

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [RouterOutlet],
  template: '<router-outlet />'
})
export class AppComponent {
  // Datos
  public barChartLabels: string[] = ['DC010', 'DC020', 'DC040'];
  public barChartType: 'bar' | 'line' | 'pie' = 'bar';
  public barChartLegend = true;

  public barChartData = [
    { data: [3772, 707, 3373], label: 'REAL 2025' },
    { data: [4914, 853, 4494], label: 'META 2026' }
  ];

  // Cambiar tipo de gráfica
  onChartTypeChange(event: any) {
    this.barChartType = event.target.value;
  }
}