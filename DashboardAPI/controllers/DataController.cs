using System.Security.Claims;
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
        private readonly ReporteStore _store;
        private readonly ILogger<DataController> _logger;

        public DataController(
            WebScraperService scraper,
            AppDbContext context,
            FullCompareService fullCompare,
            ReporteStore store,
            ILogger<DataController> logger)
        {
            _scraper     = scraper;
            _context     = context;
            _fullCompare = fullCompare;
            _store       = store;
            _logger      = logger;
        }

        private string GetUserDivision() =>
            User.FindFirstValue("division") ?? "DC000";

        // =========================
        // ZONAS POR DIVISIÓN
        // =========================

        [HttpGet("zonas")]
        public async Task<IActionResult> GetZonas()
        {
            try
            {
                var zonas = await _scraper.GetZonasForDivisionAsync(GetUserDivision());
                return Ok(zonas);
            }
            catch (CfePortalUnreachableException)
            {
                return Ok(new[] { new { value = "00000", label = "Todas las zonas" } });
            }
            catch (Exception ex)
            {
                _logger.LogWarning("GetZonas: {Err}", ex.Message);
                return Ok(new[] { new { value = "00000", label = "Todas las zonas" } });
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

                var data = await _scraper.GetImuReportAsync(zona, useMes, useYear, GetUserDivision());

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
                var now          = DateTime.Now;
                var currentYear  = hasta != null ? RangoFechas.Anio(hasta) : now.Year;
                var previousYear = currentYear - 1;

                var startPrev = new DateTime(previousYear, 1, 1);
                var startCurr = new DateTime(currentYear,  1, 1);
                var endCurr   = new DateTime(currentYear + 1, 1, 1);

                var latestPrev = await _context.Inconformidades
                    .Where(x => x.FechaConsulta >= startPrev && x.FechaConsulta < startCurr && x.Codigo == "TOTAL")
                    .MaxAsync(x => (DateTime?)x.FechaConsulta);

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

                var prevByArea = yearPrevious
                    .GroupBy(x => x.AREA.Trim())
                    .ToDictionary(g => g.Key, g => g.ToList());

                var areas  = yearCurrent.Select(x => x.AREA.Trim()).Distinct();
                var result = new List<Comparativo>();

                foreach (var area in areas)
                {
                    var totalCurrent  = SumValores(yearCurrent.Where(x => x.AREA.Trim() == area));
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
        // COMPARATIVO EN VIVO
        // =========================

        [HttpGet("compare/{codigo}")]
        public async Task<IActionResult> CompareByCode(string codigo)
        {
            try
            {
                var today        = DateTime.Now;
                var currentYear  = today.Year;
                var previousYear = currentYear - 1;

                var dataPrevious = await _scraper.GetComparisonData(
                    InconformidadesUrl,
                    RangoFechas.Desde(previousYear),
                    RangoFechas.Hasta(previousYear));

                await Task.Delay(3000);

                var dataCurrent = await _scraper.GetComparisonData(
                    InconformidadesUrl,
                    RangoFechas.Desde(currentYear),
                    RangoFechas.Hasta(currentYear));

                var result = new List<Comparativo>();

                foreach (var rowCurrent in dataCurrent)
                {
                    var area       = rowCurrent["AREA"];
                    var rowPrevious = dataPrevious.FirstOrDefault(x => x["AREA"] == area);

                    double totalPrevious = 0;
                    double totalCurrent  = 0;

                    if (rowPrevious != null && rowPrevious.ContainsKey(codigo))
                        double.TryParse(rowPrevious[codigo].Replace(",", ""), out totalPrevious);

                    if (rowCurrent.ContainsKey(codigo))
                        double.TryParse(rowCurrent[codigo].Replace(",", ""), out totalCurrent);

                    var variacion = totalPrevious > 0
                        ? ((totalCurrent - totalPrevious) / totalPrevious) * 100
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
                return BadRequest($"Error comparing live data: {ex.Message}");
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
                var data = await _fullCompare.GetFullCompareAsync(desde, hasta, GetUserDivision());
                return Ok(data);
            }
            catch (Exception ex)
            {
                return BadRequest($"Error in full compare: {ex.Message}");
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
            var today    = DateTime.Now;
            var useYear  = hasta != null ? RangoFechas.Anio(hasta) : (year ?? today.Year);
            var desdeUse = desde != null ? RangoFechas.Normaliza(desde) : RangoFechas.Desde(useYear);
            var hastaUse = hasta != null ? RangoFechas.Normaliza(hasta) : RangoFechas.Hasta(useYear);

            try
            {
                var result = await _scraper.GetCausasDataAllAsync(desdeUse, hastaUse, CausaCodes, zona, GetUserDivision());
                await _store.SaveCausasAsync(result, useYear, zona);
                return Ok(result);
            }
            catch (CfePortalUnreachableException ex)
            {
                _logger.LogError("CausasAll: portal CFE inaccesible: {Err}", ex.Message);
                return StatusCode(503, ex.Message);
            }
        }

    }
}
