using DashboardAPI.Services;
using DashboardAPI.Helpers;
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
        private const string InconformidadesUrl = "https://cssnal.cfe.mx/Inconformidades/solTermino.asp";
        private static readonly string[] CausaCodes = ["E02", "E03", "E04", "E05", "E06", "E07", "Q07"];
        private readonly AppDbContext _context;
        private readonly WebScraperService _scraper;
        private readonly FullCompareService _fullCompare;
        private readonly MetaRealService _metaReal;
        private readonly ReporteStore _store;
        private readonly ILogger<DataController> _logger;


        public DataController(
            WebScraperService scraper,
            AppDbContext context,
            FullCompareService fullCompare,
            MetaRealService metaReal,
            ReporteStore store,
            ILogger<DataController> logger)
        {
            _scraper = scraper;
            _context = context;
            _fullCompare = fullCompare;
            _metaReal = metaReal;
            _store = store;
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
                        InconformidadesUrl,
                        RangoFechas.Desde(DateTime.Now.Year),
                        RangoFechas.Hasta(DateTime.Now.Year)
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
        // POR CADA MIL USUARIOS
        // =========================

        [HttpGet("imu")]
        public async Task<IActionResult> Imu(
            [FromQuery] string zona = "00000",
            [FromQuery] string? mes = null,
            [FromQuery] int? year = null)
        {
            try
            {
                var hoy     = DateTime.Now;
                var useMes  = mes  ?? hoy.Month.ToString("D2");
                var useYear = year ?? hoy.Year;

                var data = await _scraper.GetImuReportAsync(zona, useMes, useYear);

                // Guardado tidy para Power BI (no rompe la respuesta si falla)
                if (data.Count > 0)
                    await _store.SaveImuAsync(data, useYear, int.Parse(useMes), zona);

                return Ok(data);
            }
            catch (CfePortalUnreachableException ex)
            {
                _logger.LogError("Imu: portal CFE inaccesible: {Err}", ex.Message);
                return StatusCode(503, ex.Message);
            }
            catch (Exception ex)
            {
                return BadRequest($"Error scraping reporte IMU: {ex.Message}");
            }
        }

        // =========================
        // COMPARATIVO TOTAL
        // =========================

        [HttpGet("compare")]
        public async Task<IActionResult> Compare(
            [FromQuery] string? desde = null, [FromQuery] string? hasta = null)
        {
            try
            {
                var now = DateTime.Now;
                var currentYear  = hasta != null ? RangoFechas.Anio(hasta) : now.Year;
                var previousYear = currentYear - 1;

                var startPrev = new DateTime(previousYear, 1, 1);
                var startCurr = new DateTime(currentYear,  1, 1);
                var endCurr   = new DateTime(currentYear + 1, 1, 1);

                // Usamos solo la fecha de scrape más reciente por año para no sumar duplicados
                var latestPrev = await _context.Inconformidades
                    .Where(x => x.FechaConsulta >= startPrev && x.FechaConsulta < startCurr && x.Codigo == "TOTAL")
                    .MaxAsync(x => (DateTime?)x.FechaConsulta);

                // Si el año anterior (p. ej. 2025) todavía no está en la BD, lo scrapeamos
                // (1 de enero → 4 de mayo) y lo guardamos. Así el comparativo del dashboard
                // siempre tiene el año previo, no solo el actual.
                if (latestPrev == null)
                {
                    await _scraper.GetComparisonData(
                        InconformidadesUrl,
                        desde != null ? RangoFechas.ConAnio(desde, previousYear) : RangoFechas.Desde(previousYear),
                        hasta != null ? RangoFechas.ConAnio(hasta, previousYear) : RangoFechas.Hasta(previousYear));

                    latestPrev = await _context.Inconformidades
                        .Where(x => x.FechaConsulta >= startPrev && x.FechaConsulta < startCurr && x.Codigo == "TOTAL")
                        .MaxAsync(x => (DateTime?)x.FechaConsulta);
                }

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

                // Búsqueda de área en O(1) usando un diccionario
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
                        : totalCurrent * 100;

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
                    else
                    {
                        variacion = totalCurrent * 100;
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
                    RangoFechas.Desde(previousYear);

                var hastaPrevious =
                    RangoFechas.Hasta(previousYear);

                var desdeCurrent =
                    RangoFechas.Desde(currentYear);

                var hastaCurrent =
                    RangoFechas.Hasta(currentYear);

                // =========================
                // SCRAPING AÑO ANTERIOR
                // =========================

                var dataPrevious =
                    await _scraper.GetComparisonData(
                        InconformidadesUrl,
                        desdePrevious,
                        hastaPrevious);

                await Task.Delay(3000);

                // =========================
                // SCRAPING AÑO ACTUAL
                // =========================

                var dataCurrent =
                    await _scraper.GetComparisonData(
                        InconformidadesUrl,
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
                    else
                    {
                        variacion = totalCurrent * 100;
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
        public async Task<IActionResult> FullCompare(
            [FromQuery] string? desde = null, [FromQuery] string? hasta = null)
        {
            try
            {
                var data =
                    await _fullCompare.GetFullCompareAsync(desde, hasta);

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
        public async Task<IActionResult> CausasAll(
            [FromQuery] int? year = null,
            [FromQuery] string zona = "00000",
            [FromQuery] string? desde = null,
            [FromQuery] string? hasta = null)
        {
            var today   = DateTime.Now;
            // Si llega un rango (desde/hasta) lo usamos tal cual; el año para guardar
            // sale de "hasta". Si no, caemos al rango fijo del año pedido (o el actual).
            var useYear = hasta != null ? RangoFechas.Anio(hasta) : (year ?? today.Year);
            var desdeUse = desde != null ? RangoFechas.Normaliza(desde) : RangoFechas.Desde(useYear);
            var hastaUse = hasta != null ? RangoFechas.Normaliza(hasta) : RangoFechas.Hasta(useYear);

            try
            {
                var result = await _scraper.GetCausasDataAllAsync(desdeUse, hastaUse, CausaCodes, zona);
                await _store.SaveCausasAsync(result, useYear, zona);
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
            var desdeCurr = RangoFechas.Desde(currYear);
            var hastaCurr = RangoFechas.Hasta(currYear);
            var desdePrev = RangoFechas.Desde(prevYear);
            var hastaPrev = RangoFechas.Hasta(prevYear);

            _logger.LogInformation("CausasBothYears zona={Zona}: scraping {Curr} then {Prev}", zona, currYear, prevYear);

            try
            {
                var current  = await _scraper.GetCausasDataAllAsync(desdeCurr, hastaCurr, CausaCodes, zona);
                var previous = await _scraper.GetCausasDataAllAsync(desdePrev, hastaPrev, CausaCodes, zona);
                await _store.SaveCausasAsync(current,  currYear, zona);
                await _store.SaveCausasAsync(previous, prevYear, zona);
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
            var desdeCurr    = RangoFechas.Desde(currYear);
            var hastaCurr    = RangoFechas.Hasta(currYear);
            var desdePrev    = RangoFechas.Desde(prevYear);
            var hastaPrev    = RangoFechas.Hasta(prevYear);
            var result = new Dictionary<string, object>();
            foreach (var code in CausaCodes)
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
                    RangoFechas.Desde(DateTime.Now.Year),
                    RangoFechas.Hasta(DateTime.Now.Year),
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
                    InconformidadesUrl,
                    RangoFechas.Desde(DateTime.Now.Year),
                    RangoFechas.Hasta(DateTime.Now.Year)
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

                if (response.Status == "SUCCESS" && response.Data.Count > 0)
                {
                    var now  = DateTime.Now;
                    var anio = hasta != null ? RangoFechas.Anio(hasta) : now.Year;
                    var mes  = now.Month;
                    await _store.SaveMetaRealAsync(response.Data, anio, mes);
                }

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

