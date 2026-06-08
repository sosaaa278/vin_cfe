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
        private readonly MetaRealService _metaReal;
        private readonly ILogger<DataController> _logger;


        public DataController(
            WebScraperService scraper,
            AppDbContext context,
            FullCompareService fullCompare,
            MetaRealService metaReal,
            ILogger<DataController> logger)
        {
            _scraper = scraper;
            _context = context;
            _fullCompare = fullCompare;
            _metaReal = metaReal;
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

                // Use explicit date ranges — DateTime.Year is not index-friendly in EF Core
                var startPrev = new DateTime(previousYear, 1, 1);
                var startCurr = new DateTime(currentYear,  1, 1);
                var endCurr   = new DateTime(currentYear + 1, 1, 1);

                var yearPrevious = await _context.Inconformidades
                    .Where(x => x.FechaConsulta >= startPrev
                             && x.FechaConsulta <  startCurr
                             && x.Codigo == "TOTAL")
                    .ToListAsync();

                var yearCurrent = await _context.Inconformidades
                    .Where(x => x.FechaConsulta >= startCurr
                             && x.FechaConsulta <  endCurr
                             && x.Codigo == "TOTAL")
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
        public async Task<IActionResult> CausasAll([FromQuery] int? year = null)
        {
            var today   = DateTime.Now;
            var useYear = year ?? today.Year;
            var desde   = $"{useYear}/01/01";
            // For a past year use the same month/day as today; for current year use today
            var hasta   = useYear == today.Year
                ? today.ToString("yyyy/MM/dd")
                : $"{useYear}/{today.Month:D2}/{today.Day:D2}";

            var codes  = new[] { "E02", "E03", "E04", "E05", "E06", "E07", "Q07" };
            var result = new Dictionary<string, List<Dictionary<string, string>>>();

            foreach (var code in codes)
            {
                try
                {
                    result[code] = await _scraper.GetCausasData(desde, hasta, code);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("CausasAll({Year}): code {Code} failed: {Err}", useYear, code, ex.Message);
                    result[code] = [];
                }
            }

            return Ok(result);
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

        // =========================
        // INCONFORMIDADES META REAL
        // =========================
        [HttpGet("inconformidades-meta-real")]
        public async Task<IActionResult> InconformidadesMetaReal([FromQuery] string? desde, [FromQuery] string? hasta)
        {
            try
            {
                _logger.LogInformation("Getting inconformidades meta-real data from {Desde} to {Hasta}", desde, hasta);
                
                var response = await _metaReal.ObtenerDatosAsync(desde, hasta);
                
                return Ok(response);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in InconformidadesMetaReal");
                return BadRequest($"Error InconformidadesMetaReal: {ex.Message}");
            }
        }   
    }
}

