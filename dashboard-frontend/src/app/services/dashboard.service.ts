import { Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
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

  // Tabla completa (todas las columnas) para UN código de inconformidad específico,
  // agrupada por colonia — usado por el detalle en vivo dentro del modal de Colonias.
  getColoniaInconformidadDetalle(zona: string, area: string, desde: string | undefined, hasta: string | undefined, tipoSolTermino: string) {
    const params: string[] = [`tipoSolTermino=${encodeURIComponent(tipoSolTermino)}`];
    if (zona !== '00000') params.push(`zona=${zona}`);
    if (area !== '00000') params.push(`area=${area}`);
    if (desde) params.push(`desde=${encodeURIComponent(desde)}`);
    if (hasta) params.push(`hasta=${encodeURIComponent(hasta)}`);
    return this.http.get<any[]>(`${this.API}/colonias/inconformidad-detalle?${params.join('&')}`);
  }

  // Envía el reporte diario de Inconformidades por correo al destinatario que teclee
  // el usuario (si se omite, el backend usa el Email:To configurado por defecto).
  enviarReportePorCorreo(to?: string) {
    const q = to ? `?to=${encodeURIComponent(to)}` : '';
    return this.http.post(`${this.API}/reportes/enviar-ahora${q}`, {}, { responseType: 'text' });
  }

  // SICOSS Distribución — solicitudes pendientes por zona/centro (tercer sistema CFE,
  // independiente de cssnal.cfe.mx y sisquem). Sin rango de fechas: el sistema origen
  // siempre da el estado actual.
  getSicossZonas() {
    return this.http.get<{ value: string; label: string }[]>(`${this.API}/sicoss/zonas`);
  }

  getSicossCentros(zona: string) {
    return this.http.get<{ value: string; label: string }[]>(`${this.API}/sicoss/centros?zona=${zona}`);
  }

  getSicossPendientes(zona: string, cen: string) {
    return this.http.get<any[]>(`${this.API}/sicoss/pendientes?zona=${zona}&cen=${cen}`);
  }

  // Detalle en vivo (bitácora de movimientos + bitácora de servicios) de una solicitud —
  // se pide bajo demanda al hacer clic en una fila, no junto con getSicossPendientes.
  getSicossDetalle(solicitud: string) {
    return this.http.get<{ Movimientos: any[]; Servicios: any[] }>(
      `${this.API}/sicoss/detalle?solicitud=${encodeURIComponent(solicitud)}`
    );
  }
}