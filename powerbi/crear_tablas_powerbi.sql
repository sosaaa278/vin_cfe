-- ============================================================================
--  Power BI – tabla de hechos + vista unificada  (PostgreSQL)
--  Ejecuta este script UNA VEZ en DBeaver sobre la base de datos del proyecto.
--
--  ¿Por qué a mano? La app crea el esquema con EnsureCreated(), que NO agrega
--  tablas nuevas a una base de datos que YA existe. En una base nueva (desarrollo)
--  la tabla se crea sola al arrancar; en una existente, corre este script.
-- ============================================================================

-- 1) Tabla de hechos en formato largo/tidy (la llena la app: IMU y Causas).
CREATE TABLE IF NOT EXISTS "HechosReportes" (
    "Id"            serial PRIMARY KEY,
    "Fuente"        text        NOT NULL,
    "FechaConsulta" timestamp   NOT NULL,
    "Anio"          integer     NOT NULL,
    "Mes"           integer     NULL,
    "ZonaFiltro"    text        NOT NULL DEFAULT '',
    "Cve"           text        NOT NULL DEFAULT '',
    "Area"          text        NOT NULL DEFAULT '',
    "Categoria"     text        NOT NULL DEFAULT '',
    "Metrica"       text        NOT NULL DEFAULT '',
    "Descripcion"   text        NOT NULL DEFAULT '',
    "Valor"         double precision NOT NULL DEFAULT 0
);

CREATE INDEX IF NOT EXISTS "IX_Hechos_Fuente_Periodo_Zona"
    ON "HechosReportes" ("Fuente", "Anio", "Mes", "ZonaFiltro");

-- 2) Vista unificada para Power BI: junta HechosReportes (IMU + Causas)
--    con la tabla Inconformidades reformateada al mismo esquema tidy.
CREATE OR REPLACE VIEW vw_hechos AS
SELECT
    "Fuente",
    "FechaConsulta",
    "Anio",
    "Mes",
    "ZonaFiltro",
    "Cve",
    "Area",
    "Categoria",
    "Metrica",
    "Descripcion",
    "Valor"
FROM "HechosReportes"

UNION ALL

SELECT
    'INCONFORMIDADES'                                   AS "Fuente",
    "FechaConsulta",
    EXTRACT(YEAR  FROM "FechaConsulta")::int            AS "Anio",
    EXTRACT(MONTH FROM "FechaConsulta")::int            AS "Mes",
    ''                                                  AS "ZonaFiltro",
    "SEC"                                               AS "Cve",
    "AREA"                                              AS "Area",
    'INCONFORMIDADES'                                   AS "Categoria",
    "Codigo"                                            AS "Metrica",
    ''                                                  AS "Descripcion",
    COALESCE(NULLIF(replace("Valor", ',', ''), '')::double precision, 0) AS "Valor"
FROM "Inconformidades";

-- Listo: en Power BI conéctate a la vista  vw_hechos
