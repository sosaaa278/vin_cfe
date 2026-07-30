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
        private readonly SisquemService _sisquem;
        private readonly ILogger<DataController> _logger;

        public DataController(
            WebScraperService scraper,
            AppDbContext context,
            FullCompareService fullCompare,
            ReporteStore store,
            SisquemService sisquem,
            ILogger<DataController> logger)
        {
            _scraper     = scraper;
            _context     = context;
            _fullCompare = fullCompare;
            _store       = store;
            _sisquem     = sisquem;
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

        [HttpGet("areas")]
        public async Task<IActionResult> GetAreas([FromQuery] string zona = "00000")
        {
            try
            {
                var areas = await _scraper.GetAreasLiveAsync(zona, GetUserDivision());
                return Ok(areas);
            }
            catch (CfePortalUnreachableException)
            {
                return Ok(new[] { new { value = "00000", label = "Todas las áreas" } });
            }
            catch (Exception ex)
            {
                _logger.LogWarning("GetAreas: {Err}", ex.Message);
                return Ok(new[] { new { value = "00000", label = "Todas las áreas" } });
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

        // =========================
        // COLONIAS (detalle de solicitudes)
        // =========================

        [HttpGet("colonias")]
        public async Task<IActionResult> Colonias(
            [FromQuery] string zona = "00000",
            [FromQuery] string area = "00000",
            [FromQuery] string? desde = null,
            [FromQuery] string? hasta = null)
        {
            var today    = DateTime.Now;
            var useYear  = hasta != null ? RangoFechas.Anio(hasta) : today.Year;
            var desdeUse = desde != null ? RangoFechas.Normaliza(desde) : RangoFechas.Desde(useYear);
            var hastaUse = hasta != null ? RangoFechas.Normaliza(hasta) : RangoFechas.Hasta(useYear);

            try
            {
                var data = await _scraper.GetColoniasReportAsync(desdeUse, hastaUse, zona, area, GetUserDivision());
                if (data.Count > 0)
                    await _store.SaveColoniasAsync(data, useYear, zona, area);
                return Ok(data);
            }
            catch (CfePortalUnreachableException ex)
            {
                _logger.LogError("Colonias: portal CFE inaccesible: {Err}", ex.Message);
                return StatusCode(503, ex.Message);
            }
            catch (Exception ex)
            {
                return BadRequest($"Error scraping reporte de colonias: {ex.Message}");
            }
        }

        [HttpGet("colonias/debug")]
        public IActionResult ColoniasDebug()
        {
            try
            {
                var dir = Path.Combine(Directory.GetCurrentDirectory(), WebScraperService.DebugDumpDir);
                if (!Directory.Exists(dir))
                    return NotFound("No hay volcados de diagnóstico todavía.");

                var latest = new DirectoryInfo(dir)
                    .GetFiles("debug_colonias_*.html")
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .FirstOrDefault();

                if (latest == null)
                    return NotFound("No hay volcados de diagnóstico de colonias todavía.");

                var html = System.IO.File.ReadAllText(latest.FullName);
                return Content(html, "text/html");
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Error leyendo el volcado de diagnóstico: {ex.Message}");
            }
        }

        // Desglose por tipo de inconformidad (E02-E07) agrupado por colonia — usado por el modal
        // de "clic en colonia" del apartado Colonias. El portal no permite filtrar a una sola
        // colonia, así que se repite el reporte una vez por código y se combina por Clave.
        private static readonly string[] ColoniaInconformidadCodes =
            ["E01", "E02", "E03", "E04", "E05", "E06", "E07",
             "Q01", "Q02", "Q03", "Q04", "Q06", "Q07", "Q08", "QC2", "QC7"];

        [HttpGet("colonias/inconformidades")]
        public async Task<IActionResult> ColoniaInconformidades(
            [FromQuery] string zona = "00000",
            [FromQuery] string area = "00000",
            [FromQuery] string? desde = null,
            [FromQuery] string? hasta = null)
        {
            var today    = DateTime.Now;
            var useYear  = hasta != null ? RangoFechas.Anio(hasta) : today.Year;
            var desdeUse = desde != null ? RangoFechas.Normaliza(desde) : RangoFechas.Desde(useYear);
            var hastaUse = hasta != null ? RangoFechas.Normaliza(hasta) : RangoFechas.Hasta(useYear);

            try
            {
                var data = await _scraper.GetColoniaInconformidadesAsync(
                    desdeUse, hastaUse, zona, area, GetUserDivision(), ColoniaInconformidadCodes);
                return Ok(data);
            }
            catch (CfePortalUnreachableException ex)
            {
                _logger.LogError("ColoniaInconformidades: portal CFE inaccesible: {Err}", ex.Message);
                return StatusCode(503, ex.Message);
            }
            catch (Exception ex)
            {
                return BadRequest($"Error scraping desglose de inconformidades por colonia: {ex.Message}");
            }
        }

        // =========================
        // QUEJAS Y EMERGENCIAS (sistema sisquem)
        // =========================

        [HttpGet("quejas-emergencias")]
        public async Task<IActionResult> QuejasEmergencias(
            [FromQuery] string[]? zona = null,
            [FromQuery] string[]? tipoOrden = null,
            [FromQuery] string? desde = null,
            [FromQuery] string? hasta = null)
        {
            var today    = DateTime.Now;
            var useYear  = hasta != null ? RangoFechas.Anio(hasta) : today.Year;
            var desdeUse = desde != null ? RangoFechas.Normaliza(desde) : RangoFechas.Desde(useYear);
            var hastaUse = hasta != null ? RangoFechas.Normaliza(hasta) : RangoFechas.Hasta(useYear);

            // sisquem usa el código corto de división (ej. "DC"), distinto al formato
            // "DC000" que usa GetUserDivision() para el resto de los reportes (cssnal.cfe.mx).
            var divisionLarga = GetUserDivision();
            var divisionCorta = divisionLarga.Length >= 2 ? divisionLarga[..2] : divisionLarga;

            try
            {
                var data = await _sisquem.GetReporteAsync(desdeUse, hastaUse, divisionCorta, zona, tipoOrden);
                if (data.ResumenEmergencias.Count > 0 || data.ResumenQuejas.Count > 0)
                    await _store.SaveQuejasEmergenciasAsync(data, useYear, zona);
                return Ok(data);
            }
            catch (CfePortalUnreachableException ex)
            {
                _logger.LogError("QuejasEmergencias: sisquem inaccesible: {Err}", ex.Message);
                return StatusCode(503, ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "QuejasEmergencias: error inesperado: {Err}", ex.Message);
                return BadRequest($"Error consultando el reporte de Quejas y Emergencias: {ex.Message}");
            }
        }

        [HttpGet("quejas-emergencias/debug")]
        public IActionResult QuejasEmergenciasDebug()
        {
            try
            {
                var dir = Path.Combine(Directory.GetCurrentDirectory(), WebScraperService.DebugDumpDir);
                if (!Directory.Exists(dir))
                    return NotFound("No hay volcados de diagnóstico todavía.");

                var latest = new DirectoryInfo(dir)
                    .GetFiles("debug_quejas_emergencias_*.json")
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .FirstOrDefault();

                if (latest == null)
                    return NotFound("No hay volcados de diagnóstico de Quejas y Emergencias todavía.");

                var json = System.IO.File.ReadAllText(latest.FullName);
                return Content(json, "application/json");
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Error leyendo el volcado de diagnóstico: {ex.Message}");
            }
        }

    }
}
