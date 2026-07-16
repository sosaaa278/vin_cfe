using HtmlAgilityPack;
using Microsoft.Playwright;
using DashboardAPI.Data;
using DashboardAPI.Helpers;
using DashboardAPI.Models;
using Microsoft.EntityFrameworkCore;

namespace DashboardAPI.Services
{
    /// <summary>
    /// Servicio singleton que cachea un scrape completo de dos años.
    /// Duración de la caché (TTL): 4 horas. Se invalida manualmente con InvalidateCache().
    /// Seguro entre hilos gracias a SemaphoreSlim (patrón de doble verificación de bloqueo).
    /// Recurre a la BD cuando el portal de CFE no está disponible.
    /// </summary>
    public class FullCompareService
    {
        // ── Caché (una entrada por rango de fechas) ─────────────────────────────────
        private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(4);
        private readonly Dictionary<string, (FullCompareResponse resp, DateTime time)> _cache = new();
        private readonly SemaphoreSlim _lock = new(1, 1);

        // ── Config del navegador (igual que WebScraperService) ──────────────────────
        private const string UserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36";

        private readonly ILogger<FullCompareService> _logger;
        private readonly IServiceScopeFactory _scopeFactory;

        public FullCompareService(ILogger<FullCompareService> logger, IServiceScopeFactory scopeFactory)
        {
            _logger = logger;
            _scopeFactory = scopeFactory;
        }

        // ── Public API ─────────────────────────────────────────────────────────────

        /// <param name="desde">Inicio del rango "yyyy-MM-dd"/"yyyy/MM/dd". Si es null, usa 1-ene del año actual.</param>
        /// <param name="hasta">Fin del rango. Si es null, usa el corte fijo de RangoFechas (4-may).</param>
        public async Task<FullCompareResponse> GetFullCompareAsync(string? desde = null, string? hasta = null, string cveDivision = "DC000")
        {
            // Año actual = año de "hasta" (o el de hoy si no se pasó rango)
            var currentYear  = hasta != null ? RangoFechas.Anio(hasta) : DateTime.Now.Year;
            var previousYear = currentYear - 1;

            // Rango del año actual y su equivalente en el año anterior
            var currDesde = desde != null ? RangoFechas.Normaliza(desde) : RangoFechas.Desde(currentYear);
            var currHasta = hasta != null ? RangoFechas.Normaliza(hasta) : RangoFechas.Hasta(currentYear);
            var prevDesde = desde != null ? RangoFechas.ConAnio(desde, previousYear) : RangoFechas.Desde(previousYear);
            var prevHasta = hasta != null ? RangoFechas.ConAnio(hasta, previousYear) : RangoFechas.Hasta(previousYear);

            var cacheKey = $"{currDesde}|{currHasta}|{cveDivision}";

            // Camino rápido: caché válida para este rango y división (no hace falta bloquear)
            if (_cache.TryGetValue(cacheKey, out var hit) && DateTime.UtcNow - hit.time < CacheTtl)
                return hit.resp;

            await _lock.WaitAsync();
            try
            {
                // Doble verificación: otro hilo pudo haber llenado la caché mientras esperábamos
                if (_cache.TryGetValue(cacheKey, out hit) && DateTime.UtcNow - hit.time < CacheTtl)
                    return hit.resp;

                _logger.LogInformation("Caché vacía para {Key} — iniciando scrape completo", cacheKey);

                const string url = "https://cssnal.cfe.mx/Inconformidades/solTermino.asp";

                var (pw, ctx) = await CreateBrowserAsync();
                try
                {
                    var data2025 = await ScrapeYearAsync(ctx, url, prevDesde, prevHasta, cveDivision);

                    _logger.LogInformation("Scraped {Count} rows for {Year}", data2025.Count, previousYear);
                    await Task.Delay(2000);

                    var data2026 = await ScrapeYearAsync(ctx, url, currDesde, currHasta, cveDivision);

                    _logger.LogInformation("Scraped {Count} rows for {Year}", data2026.Count, currentYear);

                    // Si ambos años devolvieron 0, recurrir completamente a BD
                    if (data2026.Count == 0 && data2025.Count == 0)
                    {
                        _logger.LogWarning("El scraping devolvió 0 filas en ambos años — recurriendo a la BD");
                        return await GetFromDbAsync(previousYear, currentYear);
                    }

                    // Si solo 2025 falló pero 2026 tiene datos, intentar BD para el año anterior
                    if (data2025.Count == 0 && data2026.Count > 0)
                    {
                        _logger.LogWarning("Scraping 2025 devolvió 0 filas — buscando año anterior en BD");
                        data2025 = await GetYearFromDbAsync(previousYear, currentYear);
                    }

                    var resp = new FullCompareResponse
                    {
                        RawData2026 = data2026,
                        Compare     = BuildCompare(data2025, data2026)
                    };
                    _cache[cacheKey] = (resp, DateTime.UtcNow);
                    return resp;
                }
                finally
                {
                    await ctx.CloseAsync();
                    pw.Dispose();
                }
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>Obliga a que la siguiente llamada vuelva a hacer scrape sin importar el TTL.</summary>
        public void InvalidateCache()
        {
            _cache.Clear();
            _logger.LogInformation("Caché de FullCompareService invalidada");
        }

        // ── Navegador ────────────────────────────────────────────────────────────────

        private static async Task<(IPlaywright pw, IBrowserContext ctx)> CreateBrowserAsync()
        {
            var pw      = await Playwright.CreateAsync();
            var dataDir = Path.Combine(Directory.GetCurrentDirectory(), "playwright-data-compare");

            var ctx = await pw.Chromium.LaunchPersistentContextAsync(dataDir,
                new BrowserTypeLaunchPersistentContextOptions
                {
                    Headless          = true,
                    Channel           = OperatingSystem.IsWindows() ? "msedge" : null,
                    UserAgent         = UserAgent,
                    SlowMo            = 300,
                    IgnoreHTTPSErrors = true,
                    Args              = ["--no-sandbox", "--disable-setuid-sandbox"]
                });

            return (pw, ctx);
        }

        // ── Scraping ───────────────────────────────────────────────────────────────

        private async Task<List<Dictionary<string, string>>> ScrapeYearAsync(
            IBrowserContext ctx, string url, string fechaDesde, string fechaHasta, string cveDivision = "DC000")
        {
            var result = new List<Dictionary<string, string>>();
            var page   = ctx.Pages.Count > 0 ? ctx.Pages[0] : await ctx.NewPageAsync();

            // Navegar con hasta 3 reintentos
            bool loaded = false;
            for (int attempt = 0; attempt < 3 && !loaded; attempt++)
            {
                try
                {
                    await page.GotoAsync(url, new PageGotoOptions
                    {
                        WaitUntil = WaitUntilState.DOMContentLoaded,
                        Timeout   = 60_000
                    });
                    loaded = true;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Intento de navegación {A}/3 falló: {Err}", attempt + 1, ex.Message);
                    if (attempt < 2) await Task.Delay(2000 * (int)Math.Pow(2, attempt));
                }
            }

            if (!loaded)
            {
                _logger.LogError("No se pudo navegar a {Url}", url);
                return result;
            }

            await page.WaitForSelectorAsync("select[name='cveDivision']", new() { Timeout = 60_000 });

            // Llenar el formulario
            await page.SelectOptionAsync("select[name='cveDivision']", cveDivision); await page.WaitForTimeoutAsync(1000);
            await page.SelectOptionAsync("select[name='cveZona']",     "00000"); await page.WaitForTimeoutAsync(800);
            await page.SelectOptionAsync("select[name='cveArea']",     "00000"); await page.WaitForTimeoutAsync(800);
            await page.SelectOptionAsync("select[name='cveProceso']",  "D");     await page.WaitForTimeoutAsync(800);
            await page.SelectOptionAsync("select[name='cveProcImproc']","T");    await page.WaitForTimeoutAsync(800);
            await page.FillAsync("input[name='fechaDesde']", fechaDesde);
            await page.FillAsync("input[name='fechaHasta']", fechaHasta);
            await page.WaitForTimeoutAsync(500);

            await page.ClickAsync("#procesa");

            // Esperar la tabla de resultados en lugar de una espera fija
            try
            {
                await page.WaitForSelectorAsync("#TABLE_12", new() { Timeout = 30_000 });
            }
            catch
            {
                _logger.LogWarning("No se encontró TABLE_12 para {Desde}→{Hasta}", fechaDesde, fechaHasta);
            }
            await page.WaitForTimeoutAsync(1500);

            // Leer el HTML
            var html = await page.ContentAsync();
            var doc  = new HtmlDocument();
            doc.LoadHtml(html);

            var table = doc.DocumentNode.SelectSingleNode("//table[@id='TABLE_12']");
            if (table == null) return result;

            var headerCells = table.SelectNodes(".//tr[2]/th");
            if (headerCells == null) return result;

            var headers = new List<string> { "SEC", "AREA" };
            foreach (var h in headerCells)
            {
                var t = NormalizeCell(h);
                if (!string.IsNullOrWhiteSpace(t)) headers.Add(t);
            }

            var rows = table.SelectNodes(".//tr");
            if (rows == null) return result;

            // Acceso por índice (evita el costo de Enumerable sobre HtmlNodeCollection)
            for (int ri = 2; ri < rows.Count; ri++)
            {
                var cells = rows[ri].SelectNodes("./td");
                if (cells == null || cells.Count < 3) continue;

                var item = new Dictionary<string, string>
                {
                    ["SEC"]  = NormalizeCell(cells[0]),
                    ["AREA"] = NormalizeCell(cells[1])
                };

                for (int i = 2; i < cells.Count && i < headers.Count; i++)
                    item[headers[i]] = NormalizeCell(cells[i]);

                result.Add(item);
            }

            return result;
        }

        // ── Procesamiento de datos ───────────────────────────────────────────────────

        /// <summary>
        /// Construye el diccionario de comparación indexado por código de inconformidad.
        /// O(n) por código gracias al diccionario de áreas
        /// </summary>
        private static Dictionary<string, List<Comparativo>> BuildCompare(
            List<Dictionary<string, string>> data2025,
            List<Dictionary<string, string>> data2026)
        {
            var result = new Dictionary<string, List<Comparativo>>();
            if (data2026.Count == 0) return result;

            // Búsqueda O(n): normalizamos la clave de área para evitar diferencias por espacios/mayúsculas
            var lookup2025 = data2025
                .Where(r => r.ContainsKey("AREA"))
                .GroupBy(r => NormalizeArea(r["AREA"]))
                .ToDictionary(g => g.Key, g => g.First());

            var codes = data2026[0].Keys
                .Where(k => k != "SEC" && k != "AREA")
                .ToList();

            foreach (var code in codes)
            {
                var comparativos = new List<Comparativo>(data2026.Count);

                foreach (var row2026 in data2026)
                {
                    if (!row2026.TryGetValue("AREA", out var rawArea)) continue;

                    var area       = NormalizeArea(rawArea);
                    var val2026    = ParseDouble(row2026, code);
                    var val2025    = lookup2025.TryGetValue(area, out var row2025)
                                     ? ParseDouble(row2025, code)
                                     : 0;

                    var variacion  = val2025 > 0
                        ? ((val2026 - val2025) / val2025) * 100
                        : val2026 * 100;

                    comparativos.Add(new Comparativo
                    {
                        AREA      = rawArea.Trim(), // conservamos las mayúsculas originales para mostrar
                        Total2025 = val2025,
                        Total2026 = val2026,
                        Variacion = Math.Round(variacion, 2)
                    });
                }

                result[code] = comparativos;
            }

            return result;
        }

        // ── Respaldo desde la BD ───────────────────────────────────────────────────

        /// <summary>Devuelve los registros más recientes del año anterior desde la BD (sin comparar).</summary>
        private async Task<List<Dictionary<string, string>>> GetYearFromDbAsync(int previousYear, int currentYear)
        {
            using var scope  = _scopeFactory.CreateScope();
            var context      = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var startPrev    = new DateTime(previousYear, 1, 1);
            var startCurr    = new DateTime(currentYear,  1, 1);

            var latestPrev = await context.Inconformidades
                .Where(x => x.FechaConsulta >= startPrev && x.FechaConsulta < startCurr)
                .MaxAsync(x => (DateTime?)x.FechaConsulta);

            if (!latestPrev.HasValue) return [];

            var records = await context.Inconformidades
                .Where(x => x.FechaConsulta == latestPrev.Value)
                .ToListAsync();

            _logger.LogInformation("Año anterior desde BD: {C} filas (fecha {D:yyyy-MM-dd})",
                records.Count, latestPrev.Value);
            return ToRawData(records);
        }

        private async Task<FullCompareResponse> GetFromDbAsync(int previousYear, int currentYear)
        {
            using var scope   = _scopeFactory.CreateScope();
            var context       = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var startPrev = new DateTime(previousYear, 1, 1);
            var startCurr = new DateTime(currentYear,  1, 1);
            var endCurr   = new DateTime(currentYear + 1, 1, 1);

            var latestPrev = await context.Inconformidades
                .Where(x => x.FechaConsulta >= startPrev && x.FechaConsulta < startCurr)
                .MaxAsync(x => (DateTime?)x.FechaConsulta);

            var latestCurr = await context.Inconformidades
                .Where(x => x.FechaConsulta >= startCurr && x.FechaConsulta < endCurr)
                .MaxAsync(x => (DateTime?)x.FechaConsulta);

            if (latestCurr == null)
            {
                _logger.LogWarning("No hay datos en la BD para {Year}", currentYear);
                return new FullCompareResponse { RawData2026 = [], Compare = [] };
            }

            var currRecords = await context.Inconformidades
                .Where(x => x.FechaConsulta == latestCurr.Value)
                .ToListAsync();

            var prevRecords = latestPrev.HasValue
                ? await context.Inconformidades
                    .Where(x => x.FechaConsulta == latestPrev.Value)
                    .ToListAsync()
                : [];

            var data2026 = ToRawData(currRecords);
            var data2025 = ToRawData(prevRecords);

            _logger.LogInformation(
                "Respaldo BD: {C26} filas de {Y26} (fecha {D26}), {C25} filas de {Y25} (fecha {D25})",
                data2026.Count, currentYear, latestCurr.Value.ToString("yyyy-MM-dd"),
                data2025.Count, previousYear, latestPrev?.ToString("yyyy-MM-dd") ?? "none");

            // No se cachea el respaldo de BD: es un fallback temporal mientras el
            // portal no responde; la próxima llamada reintentará el scrape en vivo.
            return new FullCompareResponse
            {
                RawData2026 = data2026,
                Compare     = BuildCompare(data2025, data2026)
            };
        }

        private static List<Dictionary<string, string>> ToRawData(List<Inconformidad> records)
        {
            return records
                .GroupBy(x => new { x.SEC, x.AREA })
                .Select(g =>
                {
                    var dict = new Dictionary<string, string>
                    {
                        ["SEC"]  = g.Key.SEC,
                        ["AREA"] = g.Key.AREA
                    };
                    foreach (var r in g)
                        dict[r.Codigo] = r.Valor;
                    return dict;
                })
                .ToList();
        }

        // ── Helpers ────────────────────────────────────────────────────────────────

        /// <summary>Decodes HTML and strips non-breaking spaces.</summary>
        private static string NormalizeCell(HtmlNode node) =>
            System.Net.WebUtility.HtmlDecode(node.InnerText)
                .Replace(" ", " ")
                .Trim();

        /// <summary>Normalizes an area name for comparison (trim + upper).</summary>
        private static string NormalizeArea(string area) =>
            area.Trim().ToUpperInvariant();

        /// <summary>Safely parses a numeric string from a row dictionary.</summary>
        private static double ParseDouble(Dictionary<string, string> row, string key)
        {
            if (!row.TryGetValue(key, out var raw)) return 0;
            double.TryParse(raw.Replace(",", ""), out double val);
            return val;
        }
    }
}
