# Reportes con Power BI

La app guarda automáticamente los datos scrapeados en la base de datos en
formato "largo/tidy", listos para Power BI. Cada vez que consultas en la app
(IMU o Causas), se guarda ese snapshot; así se va formando el histórico.

## 1. Preparar la base de datos (una sola vez)
Ejecuta [`crear_tablas_powerbi.sql`](crear_tablas_powerbi.sql) en DBeaver sobre
la base del proyecto. Crea:
- la tabla **`HechosReportes`** (la llena la app: IMU y Causas),
- la vista **`vw_hechos`**, que unifica IMU + Causas + Inconformidades.

> En una base de datos nueva (desarrollo) la tabla se crea sola al arrancar la
> app; el script solo es indispensable en una base que ya existía.

## 2. Generar histórico
Entra a la app y consulta varias zonas/meses (y "Comparar con …"). Cada consulta
guarda su snapshot. Para Inconformidades, usa el dashboard normal.

## 3. Conectar Power BI
1. **Power BI Desktop** → *Inicio* → *Obtener datos* → **Base de datos PostgreSQL**.
2. Servidor y base de datos (los mismos del `.env` / DBeaver).
3. Elige la vista **`vw_hechos`** → *Cargar*.

## 4. Modelo y reportes
La vista entrega estas columnas (esquema estrella simple):

| Columna | Úsala como | Ejemplos |
|---|---|---|
| `Fuente` | filtro/segmentador | IMU, CAUSAS, INCONFORMIDADES |
| `Anio`, `Mes` | eje de tiempo | 2026 / 5 |
| `ZonaFiltro`, `Cve`, `Area` | dimensión geográfica | DC010 / CHIHUAHUA |
| `Categoria` | agrupador | COMERCIAL, MEDICION… o código E02… |
| `Metrica` | detalle | CONS ANORM, TOT PROC… |
| `Descripcion` | texto (causas) | descripción de la causa |
| `Valor` | **medida** | suma/promedio |

Ideas de reportes:
- IMU total por zona y mes (barras) con segmentador de `Categoria`.
- Comparativo `Anio` actual vs anterior (matriz con formato condicional rojo/verde).
- Top causas por zona (`Fuente = CAUSAS`, ordenado por `Valor`).
- Tendencia mensual de una métrica por zona (líneas).

## 5. Actualización automática (opcional)
Para que los reportes publicados se refresquen solos:
1. Instala **On-premises Data Gateway** en el servidor.
2. En Power BI Service, programa el *Actualizar* del conjunto de datos.
