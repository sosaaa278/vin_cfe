import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { environment } from '../../environments/environment';

@Injectable({
  providedIn: 'root'
})
export class DashboardService {
  private readonly API = `${environment.apiUrl}/api/data`;

  constructor(private http: HttpClient) {}

  getCompareByCode(code: string) {
    return this.http.get<any[]>(`${this.API}/compare/${code}`);
  }

  getCompareData(desde?: string, hasta?: string) {
    return this.http.get<any[]>(`${this.API}/compare${this.rangeQuery(desde, hasta)}`);
  }

  getFullCompare(desde?: string, hasta?: string) {
    return this.http.get<{
      rawData2026: any[];
      compare: { [code: string]: any[] };
    }>(`${this.API}/fullcompare${this.rangeQuery(desde, hasta)}`);
  }

  /** Arma "?desde=...&hasta=..." (o "" si no hay rango). */
  private rangeQuery(desde?: string, hasta?: string): string {
    const p: string[] = [];
    if (desde) p.push(`desde=${encodeURIComponent(desde)}`);
    if (hasta) p.push(`hasta=${encodeURIComponent(hasta)}`);
    return p.length ? '?' + p.join('&') : '';
  }

  getCausasAll(year?: number, zona: string = '00000', desde?: string, hasta?: string) {
    const params: string[] = [];
    if (year)             params.push(`year=${year}`);
    if (zona !== '00000') params.push(`zona=${zona}`);
    if (desde)            params.push(`desde=${encodeURIComponent(desde)}`);
    if (hasta)            params.push(`hasta=${encodeURIComponent(hasta)}`);
    const q = params.length ? '?' + params.join('&') : '';
    return this.http.get<{ [code: string]: any[] }>(`${this.API}/causas/all${q}`);
  }

  // Reporte "por cada mil usuarios" (IMU) por zona, mes y año
  getImu(zona: string = '00000', mes?: string, year?: number) {
    const params: string[] = [`zona=${zona}`];
    if (mes)  params.push(`mes=${mes}`);
    if (year) params.push(`year=${year}`);
    return this.http.get<any[]>(`${this.API}/imu?${params.join('&')}`);
  }

  getZonas() {
    return this.http.get<{ value: string; label: string }[]>(`${this.API}/zonas`);
  }
}