# Dashboard de Inconformidades — Documentación técnica

Aplicación web interna que consulta el portal de CFE (`cssnal.cfe.mx`),
extrae los datos de inconformidades, los guarda en base de datos y los
muestra como tablas y gráficas comparativas año contra año (2025 vs 2026),
con exportación a Excel/PNG.

---

## 1. ¿Cómo funciona? (flujo general)

```
Usuario (navegador)
      │  login usuario/contraseña
      ▼
Frontend Angular  ──── token JWT ────►  Backend .NET (API)
 (tablas, gráficas)                           │
                                              │  scraping automatizado
                                              ▼
                                    Portal CFE (solTermino.asp / causasTerminacion.asp)
                                              │
                                              ▼
                                    Base de datos (histórico)
```

1. El usuario inicia sesión. El backend valida y devuelve un **token JWT**.
2. El frontend guarda el token y lo envía en cada petición.
3. Cuando se pide un comparativo, el backend abre un navegador automatizado
   (**Playwright**), entra al portal de CFE, llena el formulario (división, zona,
   fechas), envía la consulta y **lee la tabla de resultados** (`TABLE_12`).
4. Los datos se **guardan en la base de datos** (histórico) y se devuelven al
   frontend, que arma KPIs, tablas y gráficas.
5. Si el portal de CFE no responde, el sistema **usa lo último guardado en BD**
   como respaldo.

### Dos tipos de consulta
- **Inconformidades** (`solTermino.asp`): totales por zona/área, comparados año vs año.
- **Causas de terminación** (`causasTerminacion.asp`): desglose por código
  (E02–E07, Q07), en vivo, filtrable por zona.

### Optimizaciones clave del backend
- `FullCompareService`: cachea el scrape completo de 2 años durante **4 horas**
  (evita re-consultar el portal en cada visita). Seguro para múltiples usuarios
  a la vez (bloqueo con `SemaphoreSlim`).
- El scraping reusa una sola sesión de navegador por lote de códigos.
- Reintentos con espera incremental si el portal tarda; mensaje claro si la
  red/VPN de CFE no está disponible.

---

## 2. Tecnologías usadas

| Capa | Tecnología | Para qué |
|------|-----------|----------|
| **Frontend** | Angular 17 (TypeScript) | Interfaz, tablas y navegación |
| | Chart.js | Gráficas (barras, línea, pastel) |
| | Bootstrap 5 | Estilos |
| | xlsx-js-style | Exportar a Excel con formato |
| **Backend** | .NET 9 / ASP.NET Core (C#) | API REST |
| | Playwright + Microsoft Edge/Chromium | Scraping del portal CFE |
| | HtmlAgilityPack | Lectura del HTML de las tablas |
| | Entity Framework Core | Acceso a base de datos |
| **Base de datos** | PostgreSQL (producción) / SQLite (local) | Histórico de inconformidades |
| **Infra** | Docker, Windows Service | Despliegue |

Detección automática de BD: si hay cadena de conexión configurada usa
**PostgreSQL**; si no, cae a **SQLite** (`inconformidades.db`) para desarrollo.

---

## 3. Seguridad (resumen para el área de IT)

El sistema aplica varias capas estándar de la industria:

1. **Autenticación por JWT** — Tras el login se entrega un token firmado
   (HMAC-SHA256) con caducidad de 8 horas. Toda la API de datos está protegida
   con `[Authorize]`; sin token válido se rechaza con 401.

2. **Secretos fuera del código** — La clave JWT y los usuarios **no están en el
   código fuente**: se cargan desde un archivo `.env` (excluido de Git). El
   repositorio solo trae una plantilla `.env.example`.

3. **Límite de intentos de login (rate limiting)** — Máximo **5 intentos por
   minuto por IP**; más allá responde 429. Mitiga ataques de fuerza bruta.

4. **CORS restringido** — Solo se aceptan peticiones desde los orígenes
   configurados (no “cualquiera”).

5. **Cabeceras de seguridad HTTP** — Se envían en cada respuesta:
   - `X-Content-Type-Options: nosniff`
   - `X-Frame-Options: DENY` (anti-clickjacking)
   - `X-XSS-Protection`
   - `Referrer-Policy` y `Permissions-Policy` restrictivas
   - Se oculta la cabecera `Server`.

6. **Validación de token en el frontend** — Las rutas internas usan un *guard*
   que verifica el token; si expira o es inválido, cierra sesión automáticamente.

> **Punto a mejorar (recomendado decir al jefe de IT):** hoy las contraseñas
> se comparan en texto plano contra las del `.env`. Para producción conviene
> almacenarlas **hasheadas** (p. ej. BCrypt) y, si es posible, integrarlo con el
> **Directorio Activo / LDAP de CFE** en lugar de usuarios locales. También se
> recomienda servir todo bajo **HTTPS**.

---

## 4. ¿Qué necesito para subirlo a la red?

Hay dos piezas a publicar: el **backend (.NET)** y el **frontend (Angular)**.
El backend ya está preparado para servir también el frontend (un solo servidor).

### Requisitos en el servidor
- **.NET 9 Runtime** (ASP.NET Core).
- **Navegador para Playwright**: Microsoft Edge o Chromium instalado.
- **PostgreSQL** (recomendado para producción) o dejar SQLite.
- Acceso de red/**VPN a la red interna de CFE** (para alcanzar `cssnal.cfe.mx`).

### Pasos de publicación

**1. Compilar el frontend** (genera los archivos estáticos):
```powershell
cd dashboard-frontend
npm install
npm run build
```
Copiar el contenido compilado (`dist/...`) dentro de la carpeta `wwwroot` del
backend para que .NET lo sirva.

**2. Publicar el backend**:
```powershell
cd DashboardAPI
dotnet publish -c Release -o publish
```

**3. Crear el archivo `.env`** en la carpeta publicada (copiando de
`.env.example`) con la clave JWT y los usuarios reales.

**4. Subir los archivos al servidor con FileZilla:**
- Conéctate por **SFTP** (no FTP simple) con host, usuario y contraseña del
  servidor que te dé IT.
- Sube la carpeta `publish` completa (incluyendo `wwwroot` y `.env`).

**5. Configurar la base de datos con DBeaver:**
- Crea una conexión PostgreSQL al servidor de BD.
- Crea la base de datos (p. ej. `inconformidades`).
- Pon la cadena de conexión en el `.env` / `appsettings.json`
  (`ConnectionStrings:DefaultConnection`). Las tablas se crean solas al arrancar
  (`EnsureCreated`).
- DBeaver te sirve además para **ver y consultar el histórico** guardado.

**6. Arrancar el backend.** Opciones:
- Como **Windows Service** (ya está soportado en el código: `UseWindowsService`).
- Con **Docker** (hay `Dockerfile` listo, imagen con Playwright incluido).
- Manual: `dotnet DashboardAPI.dll` (escucha en el puerto `PORT` o 5111).

### Checklist final
- [ ] `.env` con clave JWT larga y usuarios reales (NO los de ejemplo).
- [ ] Cadena de conexión a PostgreSQL configurada.
- [ ] `Cors:AllowedOrigins` apuntando a la URL real del sitio.
- [ ] El servidor tiene Edge/Chromium y acceso a la red de CFE.
- [ ] (Recomendado) HTTPS habilitado.

---

## 5. Mapa rápido del código

**Backend (`DashboardAPI/`)**
- `Program.cs` — Arranque: JWT, CORS, rate limit, cabeceras, BD, servir Angular.
- `controllers/AuthController.cs` — Login y emisión de token.
- `controllers/DataController.cs` — Endpoints de datos y comparativos.
- `services/WebScraperService.cs` — Scraping de inconformidades y causas.
- `services/FullCompareService.cs` — Comparativo de 2 años con caché + respaldo BD.
- `Data/AppDbContext.cs` — Tablas e índices de la BD.
- `DotEnv.cs` — Carga de secretos desde `.env`.
- `models/` — Estructuras de datos (Inconformidad, Comparativo, etc.).

**Frontend (`dashboard-frontend/src/app/`)**
- `login/` — Pantalla de inicio de sesión.
- `dashboard/` — Tablas, KPIs y gráficas comparativas + exportación.
- `causas/` — Vista de causas de terminación por código/zona.
- `services/auth.service.ts` — Manejo del token JWT.
- `services/dashboard.service.ts` — Llamadas a la API.
- `guards/auth.guard.ts` — Protege rutas internas.
- `interceptors/auth.interceptor.ts` — Adjunta el token y maneja el 401.
```
