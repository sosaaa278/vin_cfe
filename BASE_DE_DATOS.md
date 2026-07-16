# Documentación de la base de datos

Referencia de qué guarda cada tabla, de dónde sale cada columna y cómo se escriben los datos.
Motor: SQLite en desarrollo (`inconformidades.db`) o PostgreSQL en producción (ver
`ConnectionStrings__DefaultConnection`); el esquema es el mismo en ambos motores, EF Core solo
cambia el tipo físico de columna (`TEXT`/`INTEGER` en SQLite vs `timestamp`/`text`/`integer` en
Postgres). Todas las tablas están definidas en `DashboardAPI/models/*.cs` y mapeadas por
`DashboardAPI/Data/AppDbContext.cs`; el histórico completo de cambios está en
`DashboardAPI/Migrations/`.

## Resumen

| Tabla | Modelo | Quién escribe | Qué es |
|---|---|---|---|
| `Inconformidades` | `Inconformidad.cs` | `WebScraperService.PersistRowsAsync` | Filas crudas tal como salen de la tabla `TABLE_12` del portal CFE (una fila por SEC+AREA+Código) |
| `HechosReportes` | `HechoReporte.cs` | `ReporteStore.SaveImuAsync` / `SaveCausasAsync` | Mismos datos scrapeados pero re-modelados en formato largo/tidy para Power BI (IMU y Causas) |
| `ScrapeCaches` | `ScrapeCache.cs` | `InconformidadesMetaController` (y cualquier caché futura) | Caché genérica clave→JSON con TTL, para no re-scrapear en cada request |
| `Users` | `User.cs` | `SeedData.SeedUsersAsync` (seed) + alta manual | Credenciales de login y la división CFE asociada a cada usuario |
| `__EFMigrationsHistory` | — (gestionada por EF Core) | EF Core | Registro interno de qué migraciones ya se aplicaron |

Los datos de "Meta vs Real" (`/api/inconformidades-meta-real`) **no** se persisten en una tabla
propia: se scrapean con `MetaRealService` y solo se guardan como JSON crudo dentro de
`ScrapeCaches` (clave `meta-real-{división}`), con TTL de 6 horas. No hay una tabla
`HechosReportes` con `Fuente = 'META_REAL'` en el código actual (a diferencia de lo que sugiere
un comentario viejo en `ReporteStore`).

---

## `Inconformidades`

Fila cruda por cada celda numérica de la tabla de resultados del portal CFE
(`https://cssnal.cfe.mx/Inconformidades/solTermino.asp`, tabla `#TABLE_12`). Es el "ancho"
original: una fila = una combinación de Sector+Área para una fecha de consulta, con una columna
por cada código de inconformidad presente en la tabla del portal (`TOTAL`, y los demás códigos
que el portal despliegue).

| Columna | Tipo | Origen | Descripción |
|---|---|---|---|
| `Id` | `int` (PK, autoincrement) | generado por la BD | Identificador interno |
| `FechaConsulta` | `DateTime` | parámetro `fechaHasta` de la consulta (`DataController.Compare`/`CompareByCode`) | Fecha "hasta" del rango consultado; funciona como snapshot/versión del scrape, no la fecha del evento |
| `SEC` | `string` | primera columna de `TABLE_12` | Sector |
| `AREA` | `string` | segunda columna de `TABLE_12` | Área/zona del sector |
| `Codigo` | `string` | encabezado de columna de `TABLE_12` (fila 2, `<th>`) | Código de inconformidad (p. ej. `TOTAL`, u otro código del portal) |
| `Valor` | `string` | celda de datos correspondiente | Se guarda **como texto tal cual viene del HTML** (con comas de miles); se parsea a `double` en el momento de usarlo (`SumValores`, `Compare`) |

**Cómo se guarda** (`WebScraperService.PersistRowsAsync`):
- Cada fila scrapeada se "aplana": por cada columna que no sea `SEC`/`AREA` se inserta un registro
  `Inconformidad` independiente (relación 1 fila de tabla HTML → N filas de BD).
- Antes de insertar, se cargan en memoria todas las claves `(SEC, AREA, Codigo)` ya existentes
  para esa `FechaConsulta` y se arma un `HashSet` para evitar tanto duplicados dentro del propio
  lote scrapeado como choques con lo que ya había en la BD (evita N+1 `AnyAsync`).
- Hay un índice único `UX_Inconformidades_Key` sobre `(FechaConsulta, SEC, AREA, Codigo)` como
  segunda barrera contra duplicados, y un índice `IX_Inconformidades_Fecha_Codigo` sobre
  `(FechaConsulta, Codigo)` que acelera las consultas de `DataController.Compare`.
- Se llena desde `WebScraperService.GetTableData(...)`, invocado por `DataController.Compare` y
  `CompareByCode` (comparativo año actual vs anterior). **No** se llena desde `causas/all` ni
  desde `imu` — esos usan `HechosReportes`.

---

## `HechosReportes`

Tabla "larga/tidy" (una fila = una métrica) pensada para Power BI. Unifica los reportes **IMU**
y **Causas** bajo el mismo esquema de columnas, usando `Fuente` para distinguir el origen. Cada
scrape **reemplaza** el snapshot anterior del mismo periodo/zona (no acumula histórico de
scrapes repetidos, solo el último).

| Columna | Tipo | Origen | Descripción |
|---|---|---|---|
| `Id` | `int` (PK, autoincrement) | generado por la BD | Identificador interno |
| `Fuente` | `string` | fijo por método (`"IMU"` o `"CAUSAS"`) | De qué reporte del portal viene la fila |
| `FechaConsulta` | `DateTime` | `DateTime.Now` al momento de guardar | Cuándo se hizo el scrape (no es una fecha de negocio) |
| `Anio` | `int` | parámetro `anio` pasado al endpoint | Año del periodo reportado |
| `Mes` | `int?` | parámetro `mes` (solo IMU) | IMU es mensual → tiene mes; Causas es acumulado anual → `null` |
| `ZonaFiltro` | `string` | parámetro `zonaFiltro`/`cveZona` de la consulta | Zona CFE usada al filtrar (`"00000"` = todas las zonas) |
| `Cve` | `string` | columna `CVE` de la fila scrapeada (solo IMU) | Clave de la fila del reporte IMU (ej. `DC010`); vacío en Causas |
| `Area` | `string` | columna `AREA` de la fila scrapeada (solo IMU) | Nombre de zona/área; vacío en Causas |
| `Categoria` | `string` | IMU: prefijo del nombre de columna (`TOTAL GENERAL`/`COMERCIAL`/`MEDICION`/`DISTRIBUCION`/`OTROS`); Causas: el código de causa (`E02`, `E03`, …) | Agrupador de alto nivel de la métrica |
| `Metrica` | `string` | IMU: resto del nombre de columna tras quitar la categoría (ej. `"PROCEDENTES CONS ANORM"`); Causas: el valor de la celda cuya cabecera contiene `"CLAVE"` | Nombre específico de la métrica |
| `Descripcion` | `string` | solo Causas: valor de la celda cuya cabecera contiene `"DESCRIP"` | Descripción textual de la causa; vacío en IMU |
| `Valor` | `double` | celda numérica correspondiente, parseada (se limpian comas y `%`) | El número de la métrica |

**Cómo se guarda** (`ReporteStore.cs`):

- **IMU** (`SaveImuAsync`, llamado desde `GET /api/data/imu`): por cada fila scrapeada (que trae
  columnas compuestas tipo `"COMERCIAL PROCEDENTES CONS ANORM"`), se separa el nombre en
  `Categoria`/`Metrica` con `SplitImuColumn` (compara contra una lista fija de prefijos conocidos;
  si no matchea ninguno, cae en `Categoria = "OTROS"`). Cada columna numérica de la fila HTML se
  convierte en **una fila de `HechosReportes`**. Antes de insertar, se borran todas las filas
  previas con el mismo `(Fuente="IMU", Anio, Mes, ZonaFiltro)` — es un "upsert" a nivel de
  snapshot completo, no fila por fila.
- **Causas** (`SaveCausasAsync`, llamado desde `GET /api/data/causas/all`): recibe un diccionario
  `código de causa → lista de filas`; por cada fila busca por *keyword* (no por nombre exacto de
  columna, porque el portal no es consistente) las celdas `CLAVE`, `DESCRIP` y `CAUSA` (esta
  última es el conteo numérico). Igual que IMU, borra antes de insertar todo lo previo con el
  mismo `(Fuente="CAUSAS", Anio, ZonaFiltro)`.
- Ambos métodos **atrapan cualquier excepción y solo loguean un warning** — un fallo al guardar
  nunca rompe la respuesta HTTP al frontend.
- Índice `IX_Hechos_Fuente_Periodo_Zona` sobre `(Fuente, Anio, Mes, ZonaFiltro)`, usado tanto
  para las queries del "borrar snapshot anterior" como para lecturas de Power BI.

---

## `ScrapeCaches`

Caché genérica de propósito general: clave de texto → blob JSON con timestamp, para servir datos
sin volver a scrapear el portal en cada request (Playwright es lento).

| Columna | Tipo | Origen | Descripción |
|---|---|---|---|
| `Id` | `int` (PK, autoincrement) | generado por la BD | Identificador interno |
| `Clave` | `string` (único, `UX`/`IX` sobre `Clave`) | definida por el llamador | Hoy solo se usa `"meta-real-{cveDivision}"` desde `InconformidadesMetaController` |
| `Json` | `string` | `JsonSerializer.Serialize(response)` del payload ya transformado (labels/datasets/rawTable) | Contenido cacheado, ya listo para devolver tal cual al frontend |
| `FechaGuardadoUtc` | `DateTime` | `DateTime.UtcNow` al guardar | Usado para calcular si la caché sigue vigente (`CacheTtl = 6 horas`) |

**Cómo se guarda**: `InconformidadesMetaController.ScrapeMetaReal` primero busca por `Clave`; si
existe y `DateTime.UtcNow - FechaGuardadoUtc < 6h`, devuelve el `Json` guardado sin tocar
Playwright. Si no hay caché o expiró, scrapea con `MetaRealService`, arma la respuesta, y hace
"upsert" (crea la fila si no existía, o sobreescribe `Json`/`FechaGuardadoUtc` si ya existía).

---

## `Users`

Cuentas de login. Un usuario por división CFE (creadas por el seed), más cualquier alta manual.

| Columna | Tipo | Origen | Descripción |
|---|---|---|---|
| `Id` | `int` (PK, autoincrement) | generado por la BD | Identificador interno |
| `Rpe` | `string` (único, `UX_Users_Rpe`) | seed: `"RPE" + nombre de división sin espacios` (ej. `RPENORTE`); en alta manual, el RPE real del empleado | Nombre de usuario para login |
| `PasswordHash` | `string` | `IPasswordHasher.Hash(...)` (BCrypt) | Nunca se guarda la contraseña en claro |
| `DivisionCode` | `string` | seed: `Division.Code` de `Divisions.All` (`models/Division.cs`, ej. `DC000` = Norte) | División CFE del usuario; se copia al claim `division` del JWT en el login y se usa como `cveDivision` en todo el scraping (`GetUserDivision()` en los controllers) |
| `Role` | `string` | fijo `"User"` en el seed | Rol de autorización (hoy sin uso diferenciado más allá del valor) |
| `Status` | `string` | fijo `"active"` en el seed | Estado de la cuenta |
| `CreatedAt` | `DateTime` | `DateTime.UtcNow` en el seed | Fecha de alta |

**Cómo se guarda**: `SeedData.SeedUsersAsync` corre en cada arranque (llamado desde `Program.cs`)
y recorre `Divisions.All` (16 divisiones fijas en código): si no existe un usuario con ese `Rpe`,
lo crea con la contraseña compartida de desarrollo (`Test1234!`, ver comentario en
`SeedData.cs` — cambiar antes de producción); si ya existe pero tiene `DivisionCode` vacío
(cuentas creadas antes de que existiera esa columna), lo corrige in situ. No hay endpoint de
"crear usuario" expuesto por API — el alta es vía seed o directamente en la BD.

---

## Vista `vw_hechos` (solo Postgres, para Power BI)

No es una tabla de la app sino una vista SQL que se crea corriendo
`powerbi/crear_tablas_powerbi.sql` una vez en la base de datos (Power BI se conecta directo a
Postgres, no pasa por la API). Hace `UNION ALL` de:
- `HechosReportes` tal cual (fuentes `IMU`, `CAUSAS`).
- `Inconformidades` reformateada al mismo esquema tidy, con `Fuente = 'INCONFORMIDADES'`,
  `Categoria = 'INCONFORMIDADES'`, `Metrica = Codigo`, `Anio`/`Mes` derivados de `FechaConsulta`
  con `EXTRACT`, y `Valor` convertido de texto a `double precision` (limpiando comas).

Power BI solo debe apuntar a `vw_hechos`, no a las tablas base, para tener las tres fuentes ya
unificadas en un único esquema de columnas.

---

## Notas sobre migraciones y proveedor de BD

- `AppDbContextFactory` siempre usa **SQLite** al generar migraciones
  (`dotnet ef migrations add`), pero en runtime la app usa **PostgreSQL** si
  `ConnectionStrings__DefaultConnection` está configurado. Por eso las migraciones tienen
  anotaciones de tipo SQLite (`"TEXT"` para `DateTime`) que no aplican tal cual en Postgres.
- La migración `20260624160000_FixDateTimeColumns` corrige esto en Postgres hace `ALTER` de las
  columnas `DateTime` a `TIMESTAMP WITHOUT TIME ZONE`, y solo se ejecuta bajo el proveedor
  `Npgsql`.
- `Program.cs` tiene `RegisterExistingTablesAsMigrated()`: si las tablas ya existen pero falta
  `__EFMigrationsHistory` (bases creadas fuera de EF), la puebla para que `Migrate()` no intente
  recrear tablas existentes. Solo corre en Postgres.
- Regla para migraciones nuevas: toda columna `DateTime` o `int Id` agregada requiere revisar si
  necesita una migración correctiva manual para Postgres (mismo patrón que
  `FixDateTimeColumns`/`FixUsersIdSequence`/`FixUsersCreatedAt`).
