# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

A two-tier dashboard for monitoring CFE (Comisión Federal de Electricidad) inconformidades (complaints) data:
- **Backend**: ASP.NET Core 9 Web API (`DashboardAPI/`) — scrapes the CFE internal portal and exposes a REST API
- **Frontend**: Angular 17 SPA (`dashboard-frontend/`) — displays charts and tables using Chart.js and Bootstrap 5

The backend scrapes `cssnal.cfe.mx` (only reachable on the CFE internal network/VPN) using Playwright + HtmlAgilityPack, persists data to a database, and serves the Angular build as static files in production.

## Commands

### Backend (`DashboardAPI/`)
```
dotnet run --project DashboardAPI          # Dev server on http://localhost:5111
dotnet build DashboardAPI                  # Build
dotnet ef migrations add <Name> --project DashboardAPI    # New migration
dotnet ef database update --project DashboardAPI          # Apply migrations
```

Playwright browsers must be installed once after `dotnet build`:
```
pwsh DashboardAPI/bin/Debug/net9.0/playwright.ps1 install chromium
```

### Frontend (`dashboard-frontend/`)
```
cd dashboard-frontend
npm install
npm start           # Dev server on http://localhost:4200 (proxies /api → http://localhost:5111)
npm run build       # Production build (output to dist/)
npm test            # Karma/Jasmine unit tests
```

## Configuration

The backend loads a `.env` file from the working directory **before** `WebApplication.CreateBuilder`, so environment variables in `.env` override `appsettings.json`. Use double-underscore `__` for nesting (e.g., `Jwt__Key` → `Jwt:Key`).

Required `.env` keys:
```
Jwt__Key=<min-32-char secret>
Jwt__Issuer=DashboardAPI
Jwt__Audience=DashboardFrontend
```

Users are defined under the `Users` section in `appsettings.json` (or via `Users__0__Username` / `Users__0__Password` in `.env`). The database defaults to SQLite (`inconformidades.db`) unless `ConnectionStrings__DefaultConnection` is set to a Postgres connection string.

## Architecture

### Backend DI Lifetimes (important)
- `MetaRealService` — **Scoped** (each HTTP request gets its own Playwright instance to avoid concurrency issues)
- `WebScraperService` — **Scoped**
- `ReporteStore` — **Scoped**
- `FullCompareService` — **Singleton** (holds an in-memory cache; invalidated via `POST /api/data/fullcompare/refresh`)

### Data Flow
1. Angular calls `GET /api/data/*` with a JWT in the `Authorization` header
2. `WebScraperService` opens a persistent Chromium context (dirs: `playwright-data-scraper`, `playwright-data-causas`) and navigates CFE forms
3. Scraped rows are saved to `Inconformidades` (raw) and `HechosReportes` (tidy/long format for Power BI) tables
4. Compare endpoints read from DB rather than re-scraping, except `compare/{codigo}` which always scrapes live

### Date Ranges
All scraping defaults to `RangoFechas` (`DashboardAPI/Helpers/RangoFechas.cs`): **January 1 → May 4** of the requested year. Change `MesCorte`/`DiaCorte` constants there when the cutoff date changes. The frontend `DateRangeBarComponent` lets the user override with a custom range.

### Database Schema
- `Inconformidades` — raw scraped rows; unique key on `(FechaConsulta, SEC, AREA, Codigo)`
- `HechosReportes` — tidy/long format rows saved by `ReporteStore`; each scrape replaces the previous snapshot for the same `(Fuente, Anio, Mes, ZonaFiltro)`
- `ScrapeCaches` — key/value cache for scrape results

### Frontend Structure
All routes are protected by `authGuard` except `/login`. The `authInterceptor` attaches the JWT and logs out on 401. The `apiUrl` is configured in `src/environments/environment.ts` (dev: `http://localhost:5111`).

Key Angular services:
- `DashboardService` — wraps all `/api/data/*` calls
- `InconformidadesMetaService` — `/api/inconformidades-meta/*` calls
- `AuthService` — login/logout and token storage
- `DateRangeService` — shared observable for the selected date range

### Production Deployment
The API can run as a **Windows Service** (`UseWindowsService()` is already registered). Build the Angular app (`npm run build`), copy `dist/dashboard-frontend/browser/` into `DashboardAPI/wwwroot/`, and run the API — it serves both the API and the SPA from a single process on the configured `PORT` env var (default 5111).

## API Routes Reference

| Method | Path | Notes |
|--------|------|-------|
| POST | `/api/auth/login` | Returns JWT; rate-limited to 5/min per IP |
| GET | `/api/data` | Scrape current year inconformidades |
| GET | `/api/data/compare` | Year-over-year totals from DB (scrapes prev year if missing) |
| GET | `/api/data/fullcompare` | Full compare with in-memory cache |
| POST | `/api/data/fullcompare/refresh` | Invalidate fullcompare cache |
| GET | `/api/data/compare/{codigo}` | Live scrape comparison for one cause code |
| GET | `/api/data/causas/all` | All cause codes for a zone/date range |
| GET | `/api/data/causas/bothyears` | Both years' causes in one request |
| GET | `/api/data/imu` | "Por cada mil usuarios" report |
| GET | `/api/data/inconformidades-meta-real` | Meta vs real goals (Playwright) |
