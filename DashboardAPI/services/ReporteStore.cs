using DashboardAPI.Data;
using DashboardAPI.Models;
using Microsoft.EntityFrameworkCore;

namespace DashboardAPI.Services
{
    /// <summary>
    /// Guarda los datos scrapeados en la tabla HechosReportes en formato "largo/tidy",
    /// listo para reportes en Power BI. Cada consulta reemplaza el snapshot anterior del
    /// mismo periodo/zona para no acumular duplicados.
    /// </summary>
    public class ReporteStore
    {
        private readonly AppDbContext _db;
        private readonly ILogger<ReporteStore> _logger;

        // Categorías del reporte IMU (las usamos para separar el nombre compuesto de columna,
        // ej. "COMERCIAL PROCEDENTES CONS ANORM" → Categoria=COMERCIAL, Metrica="PROCEDENTES CONS ANORM").
        // "TOTAL GENERAL" va primero por ser de dos palabras.
        private static readonly string[] ImuCategorias =
            { "TOTAL GENERAL", "COMERCIAL", "MEDICION", "DISTRIBUCION" };

        public ReporteStore(AppDbContext db, ILogger<ReporteStore> logger)
        {
            _db = db;
            _logger = logger;
        }

        // ── IMU ──────────────────────────────────────────────────────────────────────

        public async Task SaveImuAsync(
            List<Dictionary<string, string>> rows, int anio, int mes, string zonaFiltro)
        {
            try
            {
                var previas = _db.HechosReportes.Where(h =>
                    h.Fuente == "IMU" && h.Anio == anio && h.Mes == mes && h.ZonaFiltro == zonaFiltro);
                _db.HechosReportes.RemoveRange(previas);

                int added = 0;
                foreach (var row in rows)
                {
                    var cve  = row.GetValueOrDefault("CVE",  "");
                    var area = row.GetValueOrDefault("AREA", "");

                    foreach (var (col, valor) in row)
                    {
                        if (col == "CVE" || col == "AREA") continue;
                        if (!TryParseValor(valor, out var v)) continue;

                        var (categoria, metrica) = SplitImuColumn(col);
                        _db.HechosReportes.Add(new HechoReporte
                        {
                            Fuente        = "IMU",
                            FechaConsulta = DateTime.Now,
                            Anio          = anio,
                            Mes           = mes,
                            ZonaFiltro    = zonaFiltro,
                            Cve           = cve,
                            Area          = area,
                            Categoria     = categoria,
                            Metrica       = metrica,
                            Valor         = v
                        });
                        added++;
                    }
                }

                await _db.SaveChangesAsync();
                _logger.LogInformation("ReporteStore: guardadas {Count} filas IMU ({Anio}-{Mes}, zona {Zona})", added, anio, mes, zonaFiltro);
            }
            catch (Exception ex)
            {
                // No queremos que un fallo de guardado rompa la respuesta al usuario.
                _logger.LogWarning("ReporteStore.SaveImuAsync falló: {Err}", ex.Message);
            }
        }

        private static (string categoria, string metrica) SplitImuColumn(string col)
        {
            foreach (var cat in ImuCategorias)
            {
                if (col == cat) return (cat, cat);
                if (col.StartsWith(cat + " ", StringComparison.Ordinal))
                    return (cat, col[(cat.Length + 1)..].Trim());
            }
            return ("OTROS", col);
        }

        // ── CAUSAS ───────────────────────────────────────────────────────────────────

        public async Task SaveCausasAsync(
            Dictionary<string, List<Dictionary<string, string>>> dataByCode, int anio, string zonaFiltro)
        {
            try
            {
                var previas = _db.HechosReportes.Where(h =>
                    h.Fuente == "CAUSAS" && h.Anio == anio && h.ZonaFiltro == zonaFiltro);
                _db.HechosReportes.RemoveRange(previas);

                int added = 0;
                foreach (var (code, lista) in dataByCode)
                {
                    foreach (var row in lista)
                    {
                        var clave = FindByKeyword(row, "CLAVE");
                        var desc  = FindByKeyword(row, "DESCRIP");
                        var valTx = FindByKeyword(row, "CAUSA");   // conteo de causas
                        if (!TryParseValor(valTx, out var v)) continue;

                        _db.HechosReportes.Add(new HechoReporte
                        {
                            Fuente        = "CAUSAS",
                            FechaConsulta = DateTime.Now,
                            Anio          = anio,
                            Mes           = null,
                            ZonaFiltro    = zonaFiltro,
                            Cve           = "",
                            Area          = "",
                            Categoria     = code,
                            Metrica       = clave,
                            Descripcion   = desc,
                            Valor         = v
                        });
                        added++;
                    }
                }

                await _db.SaveChangesAsync();
                _logger.LogInformation("ReporteStore: guardadas {Count} filas CAUSAS ({Anio}, zona {Zona})", added, anio, zonaFiltro);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("ReporteStore.SaveCausasAsync falló: {Err}", ex.Message);
            }
        }

        // ── META REAL ─────────────────────────────────────────────────────────────────

        public async Task SaveMetaRealAsync(List<Dictionary<string, string>> rows, int anio, int mes, string cveDivision = "DC000")
        {
            try
            {
                var previas = _db.HechosReportes.Where(h =>
                    h.Fuente == "META_REAL" && h.Anio == anio && h.Mes == mes);
                _db.HechosReportes.RemoveRange(previas);

                int added = 0;
                foreach (var row in rows)
                {
                    var concepto = row.GetValueOrDefault("Concepto", "");
                    if (string.IsNullOrWhiteSpace(concepto)) continue;

                    foreach (var (col, valorTx) in row)
                    {
                        if (col == "Concepto") continue;
                        if (!TryParseValor(valorTx, out var v)) continue;

                        _db.HechosReportes.Add(new HechoReporte
                        {
                            Fuente        = "META_REAL",
                            FechaConsulta = DateTime.Now,
                            Anio          = anio,
                            Mes           = mes,
                            ZonaFiltro    = cveDivision,
                            Cve           = col,       // DC010, DC020 … o "TOTAL"
                            Area          = "",
                            Categoria     = concepto,  // META, REAL, DIFERENCIA …
                            Metrica       = "VALOR",
                            Descripcion   = "",
                            Valor         = v
                        });
                        added++;
                    }
                }

                await _db.SaveChangesAsync();
                _logger.LogInformation("ReporteStore: guardadas {Count} filas META_REAL ({Anio}-{Mes})", added, anio, mes);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("ReporteStore.SaveMetaRealAsync falló: {Err}", ex.Message);
            }
        }

        // ── Helpers ────────────────────────────────────────────────────────────────────

        /// <summary>Busca el primer valor cuya clave contenga la palabra dada (sin importar mayúsculas).</summary>
        private static string FindByKeyword(Dictionary<string, string> row, string keyword)
        {
            foreach (var (k, val) in row)
                if (k.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                    return val;
            return "";
        }

        /// <summary>Convierte el texto del portal a número (quita comas y %). Vacíos = no numérico.</summary>
        private static bool TryParseValor(string? text, out double value)
        {
            value = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;
            return double.TryParse(text.Replace(",", "").Replace("%", "").Trim(), out value);
        }
    }
}
