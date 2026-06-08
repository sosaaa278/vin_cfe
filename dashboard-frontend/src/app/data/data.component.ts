import { Component } from '@angular/core';
import { HttpClient } from '@angular/common/http';

@Component({
  selector: 'app-data',
  templateUrl: './data.component.html'
})
export class DataComponent {
  loading = false;
  error: string | null = null;
  datos: any = null;

  constructor(private http: HttpClient) {}

  // Método que llama al backend
  cargarDatos() {
    this.loading = true;
    this.error = null;
    this.datos = null;

    this.http.get('/api/Data/InconformidadesMetaReal').subscribe({
      next: (data) => {
        this.datos = data;
        this.loading = false;
      },
      error: (err) => {
        this.error = err.error?.message || 'Error desconocido';
        this.loading = false;
      }
    });
  }
}   