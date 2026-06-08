import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../environments/environment';

@Injectable({
  providedIn: 'root'
})
export class InconformidadesMetaService {
  private apiUrl = `${environment.apiUrl}/api/inconformidades-meta`;

  constructor(private http: HttpClient) {}
  obtenerDatosAsync() {
  return this.http.get('http://localhost:5111/api/InconformidadesMeta/scrape');
  }

  /**
   * Fetch meta vs real data from the CFE website
   * The backend will handle Playwright scraping
   */
  getMetaRealData(): Observable<any> {
    return this.http.get(`${this.apiUrl}/scrape`);
  }

  /**
   * Get cached meta real data (optional, for performance)
   */
  getCachedMetaRealData(): Observable<any> {
    return this.http.get(`${this.apiUrl}/cached`);
  }

  /**
   * Invalidate cache and force re-scrape
   */
  invalidateCache(): Observable<any> {
    return this.http.post(`${this.apiUrl}/invalidate-cache`, {});
  }
}
