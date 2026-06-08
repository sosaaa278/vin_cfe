import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { environment } from '../../environments/environment';

@Injectable({
  providedIn: 'root'
})
export class DashboardService {
  private readonly API = `${environment.apiUrl}/api/data`;

  constructor(private http: HttpClient) {}

  getData(url: string) {
    return this.http.get<any[]>(`${this.API}?url=${encodeURIComponent(url)}`);
  }

  getCompareByCode(code: string) {
    return this.http.get<any[]>(`${this.API}/compare/${code}`);
  }

  getCompareData() {
    return this.http.get<any[]>(`${this.API}/compare`);
  }

  getFullCompare() {
    return this.http.get<{
      rawData2026: any[];
      compare: { [code: string]: any[] };
    }>(`${this.API}/fullcompare`);
  }

  getInconformidadesMetaReal(desde?: string, hasta?: string) {
    const p = new URLSearchParams();
    if (desde) p.set('desde', desde);
    if (hasta) p.set('hasta', hasta);
    const qs = p.toString();
    return this.http.get<any[]>(`${this.API}/inconformidades-meta-real${qs ? `?${qs}` : ''}`);
  }


  getCausas(code: string = 'E02') {
    return this.http.get<any[]>(`${this.API}/causas?code=${encodeURIComponent(code)}`);
  }

  getCausasAll(year?: number) {
    const q = year ? `?year=${year}` : '';
    return this.http.get<{ [code: string]: any[] }>(`${this.API}/causas/all${q}`);
  }
}