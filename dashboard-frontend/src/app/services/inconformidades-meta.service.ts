import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { environment } from '../../environments/environment';

@Injectable({
  providedIn: 'root'
})
export class InconformidadesMetaService {
  constructor(private http: HttpClient) {}

  obtenerDatosAsync() {
    return this.http.get(`${environment.apiUrl}/api/InconformidadesMeta/scrape`);
  }
}
