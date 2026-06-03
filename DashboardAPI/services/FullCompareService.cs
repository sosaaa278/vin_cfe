using HtmlAgilityPack;
using Microsoft.Playwright;
using DashboardAPI.Data;
using DashboardAPI.Models;
using Microsoft.EntityFrameworkCore;

namespace DashboardAPI.Services
{
    /// <summary>
    /// Singleton service that caches a full two-year scrape.
    /// Cache TTL: 4 hours. Invalidate manually via InvalidateCache().
    /// Thread-safe via SemaphoreSlim (double-checked locking pattern).
    /// Falls back to DB when the CFE portal is unreachable.
    /// </summary>
    public class FullCompareService
    {
        // ── Cache ──────────────────────────────────────────────────────────────────
        private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(4);
        private FullCompareResponse? _cache;
        private DateTime             _cacheTime = DateTime.MinValue;
        private readonly SemaphoreSlim _lock = new(1, 1);

        // ── Browser config (mirrors WebScraperService) ─────────────────────────────
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

        public async Task<FullCompareResponse> GetFullCompareAsync()
        {
            // Fast path: valid cache (no lock needed)
            if (_cache != null && DateTime.UtcNow - _cacheTime < CacheTtl)
                return _cache;

            await _lock.WaitAsync();
            try
            {
                // Double-checked locking: another thread may have populated cache
                if (_cache != null && DateTime.UtcNow - _cacheTime < CacheTtl)
                    return _cache;

                _logger.LogInformation("Cache miss — starting full scrape (TTL expired or first run)");

                var today        = DateTime.Now;
                var previousYear = today.Year - 1;
                var currentYear  = today.Year;
                const string url = "https://cssnal.cfe.mx/Inconformidades/solTermino.asp";

                var (pw, ctx) = await CreateBrowserAsync();
                try
                {
                    var data2025 = await ScrapeYearAsync(ctx, url,
                        $"{previousYear}/01/01",
                        $"{previousYear}/{today.Month:D2}/{today.Day:D2}");

                    _logger.LogInformation("Scraped {Count} rows for {Year}", data2025.Count, previousYear);
                    await Task.Delay(2000);

                    var data2026 = await ScrapeYearAsync(ctx, url,
                        $"{currentYear}/01/01",
                        $"{currentYear}/{today.Month:D2}/{today.Day:D2}");

                    _logger.LogInformation("Scraped {Count} rows for {Year}", data2026.Count, currentYear);

                    // If scraping returned no data, fall back to DB
                    if (data2026.Count == 0 && data2025.Count == 0)
                    {
                        _logger.LogWarning("Scraping returned 0 rows for both years — falling back to DB");
                        return await GetFromDbAsync(previousYear, currentYear);
                    }

                    _cache     = new FullCompareResponse
                    {
                        RawData2026 = data2026,
                        Compare     = BuildCompare(data2025, data2026)
                    };
                    _cacheTime = DateTime.UtcNow;
                    return _cache;
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

        /// <summary>Forces the next call to re-scrape regardless of TTL.</summary>
        public void InvalidateCache()
        {
            _cache     = null;
            _cacheTime = DateTime.MinValue;
            _logger.LogInformation("FullCompareService cache invalidated");
        }

        // ── Browser ────────────────────────────────────────────────────────────────

        private static async Task<(IPlaywright pw, IBrowserContext ctx)> CreateBrowserAsync()
        {
            var pw      = await Playwright.CreateAsync();
            var dataDir = Path.Combine(Directory.GetCurrentDirectory(), "playwright-data-compare");

            var ctx = await pw.Chromium.LaunchPersistentContextAsync(dataDir,
                new BrowserTypeLaunchPersistentContextOptions
                {
                    Headless  = true,
                    Channel   = OperatingSystem.IsWindows() ? "msedge" : null,
                    UserAgent = UserAgent,
                    SlowMo    = 300,
                    Args      = ["--no-sandbox", "--disable-setuid-sandbox"]
                });

            return (pw, ctx);
        }

        // ── Scraping ───────────────────────────────────────────────────────────────

        private async Task<List<Dictionary<string, string>>> ScrapeYearAsync(
            IBrowserContext ctx, string url, string fechaDesde, string fechaHasta)
        {
            var result = new List<Dictionary<string, string>>();
            var page   = ctx.Pages.Count > 0 ? ctx.Pages[0] : await ctx.NewPageAsync();

            // Navigate with up to 3 retries
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
                    _logger.LogWarning("Navigate attempt {A}/3 failed: {Err}", attempt + 1, ex.Message);
                    if (attempt < 2) await Task.Delay(2000 * (int)Math.Pow(2, attempt));
                }
            }

            if (!loaded)
            {
                _logger.LogError("Could not navigate to {Url}", url);
                return result;
            }

            await page.WaitForSelectorAsync("select[name='cveDivision']", new() { Timeout = 60_000 });

            // Fill form
            await page.SelectOptionAsync("select[name='cveDivision']", "DC000"); await page.WaitForTimeoutAsync(1000);
            await page.SelectOptionAsync("select[name='cveZona']",     "00000"); await page.WaitForTimeoutAsync(800);
            await page.SelectOptionAsync("select[name='cveArea']",     "00000"); await page.WaitForTimeoutAsync(800);
            await page.SelectOptionAsync("select[name='cveProceso']",  "D");     await page.WaitForTimeoutAsync(800);
            await page.SelectOptionAsync("select[name='cveProcImproc']","T");    await page.WaitForTimeoutAsync(800);
            await page.FillAsync("input[name='fechaDesde']", fechaDesde);
            await page.FillAsync("input[name='fechaHasta']", fechaHasta);
            await page.WaitForTimeoutAsync(500);

            await page.ClickAsync("#procesa");

            // Wait for results table instead of a fixed delay
            try
            {
                await page.WaitForSelectorAsync("#TABLE_12", new() { Timeout = 30_000 });
            }
            catch
            {
                _logger.LogWarning("TABLE_12 not found for {Desde}→{Hasta}", fechaDesde, fechaHasta);
            }
            await page.WaitForTimeoutAsync(1500);

            // Parse
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

            // Use indexed access (avoids Enumerable overhead on HtmlNodeCollection)
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

        // ── Data Processing ────────────────────────────────────────────────────────

        /// <summary>
        /// Builds the compare dictionary keyed by inconformidad code.
        /// O(n) per code thanks to the area lookup dictionary — was O(n²) before.
        /// </summary>
        private static Dictionary<string, List<Comparativo>> BuildCompare(
            List<Dictionary<string, string>> data2025,
            List<Dictionary<string, string>> data2026)
        {
            var result = new Dictionary<string, List<Comparativo>>();
            if (data2026.Count == 0) return result;

            // O(n) lookup: normalize area key to prevent whitespace/case mismatches
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
                        : val2026 > 0 ? 100 : 0;

                    comparativos.Add(new Comparativo
                    {
                        AREA      = rawArea.Trim(), // keep original casing for display
                        Total2025 = val2025,
                        Total2026 = val2026,
                        Variacion = Math.Round(variacion, 2)
                    });
                }

                result[code] = comparativos;
            }

            return result;
        }

        // ── DB Fallback ────────────────────────────────────────────────────────────

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
                _logger.LogWarning("No DB data available for {Year}", currentYear);
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
                "DB fallback: {C26} rows for {Y26} (date {D26}), {C25} rows for {Y25} (date {D25})",
                data2026.Count, currentYear, latestCurr.Value.ToString("yyyy-MM-dd"),
                data2025.Count, previousYear, latestPrev?.ToString("yyyy-MM-dd") ?? "none");

            var result = new FullCompareResponse
            {
                RawData2026 = data2026,
                Compare     = BuildCompare(data2025, data2026)
            };
            _cache     = result;
            _cacheTime = DateTime.UtcNow;
            return result;
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
