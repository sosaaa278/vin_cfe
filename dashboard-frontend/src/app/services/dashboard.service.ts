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

  getCausas(code: string = 'E02') {
    return this.http.get<any[]>(`${this.API}/causas?code=${encodeURIComponent(code)}`);
  }

  getCausasAll(year?: number, zona: string = '00000') {
    const params: string[] = [];
    if (year)          params.push(`year=${year}`);
    if (zona !== '00000') params.push(`zona=${zona}`);
    const q = params.length ? '?' + params.join('&') : '';
    return this.http.get<{ [code: string]: any[] }>(`${this.API}/causas/all${q}`);
  }

  getCausasBothYears(zona: string = '00000') {
    const q = zona !== '00000' ? `?zona=${zona}` : '';
    return this.http.get<{
      current:  { [code: string]: any[] };
      previous: { [code: string]: any[] };
    }>(`${this.API}/causas/bothyears${q}`);
  }
}