using DashboardAPI.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using DashboardAPI.Data;
using DashboardAPI.Models;
using Microsoft.EntityFrameworkCore;

namespace DashboardAPI.Controllers
{
    [Authorize]
    [ApiController]
    [Route("api/[controller]")]
    public class DataController : ControllerBase
    {
        private readonly AppDbContext _context;
        private readonly WebScraperService _scraper;
        private readonly FullCompareService _fullCompare;
        private readonly ILogger<DataController> _logger;

        public DataController(
            WebScraperService scraper,
            AppDbContext context,
            FullCompareService fullCompare,
            ILogger<DataController> logger)
        {
            _scraper = scraper;
            _context = context;
            _fullCompare = fullCompare;
            _logger = logger;
        }

        // =========================
        // SCRAPING PRINCIPAL
        // =========================

        [HttpGet]
        public async Task<IActionResult> Get()
        {
            try
            {
                var data =
                    await _scraper.GetTableData(
                        "https://cssnal.cfe.mx/Inconformidades/solTermino.asp",

                        $"{DateTime.Now.Year}/01/01",

                        DateTime.Now.ToString("yyyy/MM/dd")
                    );

                return Ok(data);
            }
            catch (Exception ex)
            {
                return BadRequest(
                    $"Error scraping data: {ex.Message}");
            }
        }

        // =========================
        // COMPARATIVO TOTAL
        // =========================

        [HttpGet("compare")]
        public async Task<IActionResult> Compare()
        {
            try
            {
                var now = DateTime.Now;
                var currentYear  = now.Year;
                var previousYear = currentYear - 1;

                var startPrev = new DateTime(previousYear, 1, 1);
                var startCurr = new DateTime(currentYear,  1, 1);
                var endCurr   = new DateTime(currentYear + 1, 1, 1);

                // Use only the most recent scrape date per year to avoid summing duplicates
                var latestPrev = await _context.Inconformidades
                    .Where(x => x.FechaConsulta >= startPrev && x.FechaConsulta < startCurr && x.Codigo == "TOTAL")
                    .MaxAsync(x => (DateTime?)x.FechaConsulta);

                var latestCurr = await _context.Inconformidades
                    .Where(x => x.FechaConsulta >= startCurr && x.FechaConsulta < endCurr && x.Codigo == "TOTAL")
                    .MaxAsync(x => (DateTime?)x.FechaConsulta);

                if (latestCurr == null)
                    return Ok(new List<Comparativo>());

                var yearPrevious = latestPrev.HasValue
                    ? await _context.Inconformidades
                        .Where(x => x.FechaConsulta == latestPrev.Value && x.Codigo == "TOTAL")
                        .ToListAsync()
                    : new List<Inconformidad>();

                var yearCurrent = await _context.Inconformidades
                    .Where(x => x.FechaConsulta == latestCurr.Value && x.Codigo == "TOTAL")
                    .ToListAsync();

                // O(1) area lookup via dictionary
                var prevByArea = yearPrevious
                    .GroupBy(x => x.AREA.Trim())
                    .ToDictionary(g => g.Key, g => g.ToList());

                var areas = yearCurrent.Select(x => x.AREA.Trim()).Distinct();
                var result = new List<Comparativo>();

                foreach (var area in areas)
                {
                    var totalCurrent = SumValores(yearCurrent.Where(x => x.AREA.Trim() == area));
                    var totalPrevious = prevByArea.TryGetValue(area, out var prevRows)
                        ? SumValores(prevRows)
                        : 0;

                    var variacion = totalPrevious > 0
                        ? (totalCurrent - totalPrevious) / totalPrevious * 100
                        : 0;

                    result.Add(new Comparativo
                    {
                        AREA      = area,
                        Total2025 = totalPrevious,
                        Total2026 = totalCurrent,
                        Variacion = Math.Round(variacion, 2)
                    });
                }

                return Ok(result);
            }
            catch (Exception ex)
            {
                return BadRequest($"Error comparing data: {ex.Message}");
            }
        }

        private static double SumValores(IEnumerable<Inconformidad> rows) =>
            rows.Sum(x => double.TryParse(x.Valor.Replace(",", ""), out var v) ? v : 0);

        // =========================
        // COMPARAR UNA ZONA
        // =========================

        [HttpGet("compare/area/{area}")]
        public async Task<IActionResult>
        CompareArea(string area)
        {
            try
            {
                var currentYear =
                    DateTime.Now.Year;

                var previousYear =
                    currentYear - 1;

                var yearPrevious =
                    await _context.Inconformidades
                        .Where(x =>
                            x.FechaConsulta.Year == previousYear &&
                            x.AREA.Contains(area))
                        .ToListAsync();

                var yearCurrent =
                    await _context.Inconformidades
                        .Where(x =>
                            x.FechaConsulta.Year == currentYear &&
                            x.AREA.Contains(area))
                        .ToListAsync();

                var codigos =
                    yearCurrent
                        .Select(x => x.Codigo)
                        .Distinct();

                var result =
                    new List<object>();

                foreach (var codigo in codigos)
                {
                    var totalPrevious =
                        yearPrevious
                            .Where(x => x.Codigo == codigo)
                            .Sum(x =>
                            {
                                double.TryParse(
                                    x.Valor.Replace(",", ""),
                                    out double val);

                                return val;
                            });

                    var totalCurrent =
                        yearCurrent
                            .Where(x => x.Codigo == codigo)
                            .Sum(x =>
                            {
                                double.TryParse(
                                    x.Valor.Replace(",", ""),
                                    out double val);

                                return val;
                            });

                    double variacion = 0;

                    if (totalPrevious > 0)
                    {
                        variacion =
                            ((totalCurrent - totalPrevious)
                            / totalPrevious) * 100;
                    }

                    result.Add(new
                    {
                        Codigo = codigo,

                        Total2025 = totalPrevious,

                        Total2026 = totalCurrent,

                        Variacion =
                            Math.Round(
                                variacion,
                                2)
                    });
                }

                return Ok(result);
            }
            catch (Exception ex)
            {
                return BadRequest(
                    $"Error comparing area: {ex.Message}");
            }
        }

        // =========================
        // COMPARATIVO EN VIVO
        // =========================

        [HttpGet("compare/{codigo}")]
        public async Task<IActionResult>
        CompareByCode(string codigo)
        {
            try
            {
                var today =
                    DateTime.Now;

                var currentYear =
                    today.Year;

                var previousYear =
                    currentYear - 1;

                // =========================
                // FECHAS DINÁMICAS
                // =========================

                var desdePrevious =
                    $"{previousYear}/01/01";

                var hastaPrevious =
                    $"{previousYear}/{today.Month:D2}/{today.Day:D2}";

                var desdeCurrent =
                    $"{currentYear}/01/01";

                var hastaCurrent =
                    $"{currentYear}/{today.Month:D2}/{today.Day:D2}";

                var url =
                    "https://cssnal.cfe.mx/Inconformidades/solTermino.asp";

                // =========================
                // SCRAPING AÑO ANTERIOR
                // =========================

                var dataPrevious =
                    await _scraper.GetComparisonData(
                        url,
                        desdePrevious,
                        hastaPrevious);

                await Task.Delay(3000);

                // =========================
                // SCRAPING AÑO ACTUAL
                // =========================

                var dataCurrent =
                    await _scraper.GetComparisonData(
                        url,
                        desdeCurrent,
                        hastaCurrent);

                var result =
                    new List<Comparativo>();

                foreach (var rowCurrent in dataCurrent)
                {
                    var area =
                        rowCurrent["AREA"];

                    var rowPrevious =
                        dataPrevious
                            .FirstOrDefault(x =>
                                x["AREA"] == area);

                    double totalPrevious = 0;

                    double totalCurrent = 0;

                    if (rowPrevious != null &&
                        rowPrevious.ContainsKey(codigo))
                    {
                        double.TryParse(
                            rowPrevious[codigo]
                                .Replace(",", ""),

                            out totalPrevious);
                    }

                    if (rowCurrent.ContainsKey(codigo))
                    {
                        double.TryParse(
                            rowCurrent[codigo]
                                .Replace(",", ""),

                            out totalCurrent);
                    }

                    double variacion = 0;

                    if (totalPrevious > 0)
                    {
                        variacion =
                            ((totalCurrent - totalPrevious)
                            / totalPrevious) * 100;
                    }

                    result.Add(
                        new Comparativo
                        {
                            AREA = area,

                            Total2025 = totalPrevious,

                            Total2026 = totalCurrent,

                            Variacion =
                                Math.Round(
                                    variacion,
                                    2)
                        });
                }

                return Ok(result);
            }
            catch (Exception ex)
            {
                return BadRequest(
                    $"Error comparing live data: {ex.Message}");
            }
        }

        // =========================
        // COMPARATIVO COMPLETO (CACHÉ)
        // =========================

        [HttpGet("fullcompare")]
        public async Task<IActionResult> FullCompare()
        {
            try
            {
                var data =
                    await _fullCompare.GetFullCompareAsync();

                return Ok(data);
            }
            catch (Exception ex)
            {
                return BadRequest(
                    $"Error in full compare: {ex.Message}");
            }
        }

        [HttpPost("fullcompare/refresh")]
        public IActionResult FullCompareRefresh()
        {
            _fullCompare.InvalidateCache();
            return Ok("Cache invalidado. El próximo GET re-scrapeará.");
        }

        // =========================
        // CAUSAS DE INCONFORMIDAD
        // =========================

        [HttpGet("causas/all")]
        public async Task<IActionResult> CausasAll([FromQuery] int? year = null, [FromQuery] string zona = "00000")
        {
            var today   = DateTime.Now;
            var useYear = year ?? today.Year;
            var desde   = $"{useYear}/01/01";
            var hasta   = useYear == today.Year
                ? today.ToString("yyyy/MM/dd")
                : $"{useYear}/{today.Month:D2}/{today.Day:D2}";

            var codes  = new[] { "E02", "E03", "E04", "E05", "E06", "E07", "Q07" };
            try
            {
                var result = await _scraper.GetCausasDataAllAsync(desde, hasta, codes, zona);
                return Ok(result);
            }
            catch (CfePortalUnreachableException ex)
            {
                _logger.LogError("CausasAll: portal CFE inaccesible: {Err}", ex.Message);
                return StatusCode(503, ex.Message);
            }
        }

        // =========================
        // AMBOS AÑOS EN UNA PETICIÓN
        // =========================

        [HttpGet("causas/bothyears")]
        public async Task<IActionResult> CausasBothYears([FromQuery] string zona = "00000")
        {
            var today    = DateTime.Now;
            var currYear = today.Year;
            var prevYear = currYear - 1;
            var codes    = new[] { "E02", "E03", "E04", "E05", "E06", "E07", "Q07" };

            var desdeCurr = $"{currYear}/01/01";
            var hastaCurr = today.ToString("yyyy/MM/dd");
            var desdePrev = $"{prevYear}/01/01";
            var hastaPrev = $"{prevYear}/{today.Month:D2}/{today.Day:D2}";

            _logger.LogInformation("CausasBothYears zona={Zona}: scraping {Curr} then {Prev}", zona, currYear, prevYear);

            try
            {
                var current  = await _scraper.GetCausasDataAllAsync(desdeCurr, hastaCurr, codes, zona);
                var previous = await _scraper.GetCausasDataAllAsync(desdePrev, hastaPrev, codes, zona);
                return Ok(new { current, previous });
            }
            catch (CfePortalUnreachableException ex)
            {
                _logger.LogError("CausasBothYears: portal CFE inaccesible: {Err}", ex.Message);
                return StatusCode(503, ex.Message);
            }
        }

        [HttpGet("causas/compare")]
        public async Task<IActionResult> CausasCompare()
        {
            var today        = DateTime.Now;
            var currYear     = today.Year;
            var prevYear     = currYear - 1;
            var desdeCurr    = $"{currYear}/01/01";
            var hastaCurr    = today.ToString("yyyy/MM/dd");
            var desdePrev    = $"{prevYear}/01/01";
            var hastaPrev    = $"{prevYear}/{today.Month:D2}/{today.Day:D2}";
            var codes        = new[] { "E02", "E03", "E04", "E05", "E06", "E07", "Q07" };

            var result = new Dictionary<string, object>();
            foreach (var code in codes)
            {
                try
                {
                    var curr = await _scraper.GetCausasData(desdeCurr, hastaCurr, code);
                    var prev = await _scraper.GetCausasData(desdePrev, hastaPrev, code);
                    result[code] = new { current = curr, previous = prev };
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("CausasCompare: code {Code} failed: {Err}", code, ex.Message);
                    result[code] = new
                    {
                        current  = new List<Dictionary<string, string>>(),
                        previous = new List<Dictionary<string, string>>()
                    };
                }
            }
            return Ok(result);
        }

        [HttpGet("causas")]
        public async Task<IActionResult> Causas([FromQuery] string code = "E02")
        {
            try
            {
                var data = await _scraper.GetCausasData(
                    $"{DateTime.Now.Year}/01/01",
                    DateTime.Now.ToString("yyyy/MM/dd"),
                    code
                );
                return Ok(data);
            }
            catch (Exception ex)
            {
                return BadRequest($"Error scraping causas: {ex.Message}");
            }
        }

        // =========================
        // SNAPSHOT MANUAL
        // =========================

        [HttpGet("snapshot")]
        public async Task<IActionResult>
        Snapshot()
        {
            try
            {
                await _scraper.GetTableData(
                    "https://cssnal.cfe.mx/Inconformidades/solTermino.asp",

                    $"{DateTime.Now.Year}/01/01",

                    DateTime.Now.ToString("yyyy/MM/dd")
                );

                return Ok(
                    "Snapshot completed successfully");
            }
            catch (Exception ex)
            {
                return BadRequest(
                    $"Snapshot error: {ex.Message}");
            }
        }
    }
}