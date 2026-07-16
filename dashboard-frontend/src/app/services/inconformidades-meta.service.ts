import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { environment } from '../../environments/environment';

@Injectable({
  providedIn: 'root'
})
export class InconformidadesMetaService {
  constructor(private http: HttpClient) {}

  obtenerDatosAsync(desde?: string, hasta?: string) {
    const params: string[] = [];
    if (desde) params.push(`desde=${encodeURIComponent(desde)}`);
    if (hasta) params.push(`hasta=${encodeURIComponent(hasta)}`);
    const q = params.length ? '?' + params.join('&') : '';
    return this.http.get(`${environment.apiUrl}/api/InconformidadesMeta/scrape${q}`);
  }
}
