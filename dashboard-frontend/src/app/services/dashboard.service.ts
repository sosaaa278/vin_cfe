import { Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { environment } from '../../environments/environment';

export interface GraficasQuejasEmergencias {
  ejeX: string[];
  emergenciasPendientes: number[];
  quejasPendientes: number[];
  emergenciasAtendidas: number[];
  quejasAtendidas: number[];
  emergenciasGeneradas: number[];
  quejasGeneradas: number[];
}

export interface ResumenGlobalQuejasEmergencias {
  emergenciasPendientes: number;
  emergenciasVencidas: number;
  emergenciasEnAtencion: number;
  quejasPendientes: number;
  quejasVencidas: number;
  quejasEnAtencion: number;
}

export interface QuejasEmergenciasResponse {
  graficas: GraficasQuejasEmergencias;
  resumenEmergencias: any[];
  resumenQuejas: any[];
  detalleEmergencias: any[];
  detalleQuejas: any[];
  listado: any[];
  resumenEstadoEmergencias: any[];
  resumenMunicipioEmergencias: any[];
  resumenEstadoQuejas: any[];
  resumenMunicipioQuejas: any[];
  resumenGlobal: ResumenGlobalQuejasEmergencias;
  integridadOk: boolean;
}

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

  getAreas(zona: string = '00000') {
    return this.http.get<{ value: string; label: string }[]>(`${this.API}/areas?zona=${zona}`);
  }

  // Reporte de solicitudes por colonia
  getColonias(zona: string = '00000', area: string = '00000', desde?: string, hasta?: string) {
    const params: string[] = [];
    if (zona !== '00000') params.push(`zona=${zona}`);
    if (area !== '00000') params.push(`area=${area}`);
    if (desde)            params.push(`desde=${encodeURIComponent(desde)}`);
    if (hasta)            params.push(`hasta=${encodeURIComponent(hasta)}`);
    const q = params.length ? '?' + params.join('&') : '';
    return this.http.get<any[]>(`${this.API}/colonias${q}`);
  }

  // Desglose por tipo de inconformidad (E02-E07) agrupado por colonia — usado por el
  // modal de "clic en colonia" del apartado Colonias.
  getColoniaInconformidades(zona: string = '00000', area: string = '00000', desde?: string, hasta?: string) {
    const params: string[] = [];
    if (zona !== '00000') params.push(`zona=${zona}`);
    if (area !== '00000') params.push(`area=${area}`);
    if (desde)            params.push(`desde=${encodeURIComponent(desde)}`);
    if (hasta)            params.push(`hasta=${encodeURIComponent(hasta)}`);
    const q = params.length ? '?' + params.join('&') : '';
    return this.http.get<any[]>(`${this.API}/colonias/inconformidades${q}`);
  }

  // Reporte "Quejas y Emergencias" (sistema sisquem) — primer caso del proyecto que
  // manda selección múltiple real por query string; HttpParams.append() soporta
  // varios valores bajo la misma clave (?zona=A&zona=B), y ASP.NET Core los bindea
  // automático a un string[] en el controller sin código adicional.
  getQuejasEmergencias(zonas: string[], tiposOrden: string[], desde?: string, hasta?: string) {
    let params = new HttpParams();
    zonas.forEach(z => params = params.append('zona', z));
    tiposOrden.forEach(t => params = params.append('tipoOrden', t));
    if (desde) params = params.set('desde', desde);
    if (hasta) params = params.set('hasta', hasta);
    return this.http.get<QuejasEmergenciasResponse>(`${this.API}/quejas-emergencias`, { params });
  }
}