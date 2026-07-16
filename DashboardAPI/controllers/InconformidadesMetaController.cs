using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using System.Text.Json;
using DashboardAPI.Data;
using DashboardAPI.Models;
using DashboardAPI.Services;

namespace DashboardAPI.Controllers
{
    [ApiController]
    [Authorize]
    [Route("api/[controller]")]
    public class InconformidadesMetaController : ControllerBase
    {
        private readonly MetaRealService _service;
        private readonly AppDbContext _db;

        public InconformidadesMetaController(MetaRealService service, AppDbContext db)
        {
            _service = service;
            _db      = db;
        }

        private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(6);

        // Lee el DivisionCode del claim "division" del JWT (igual que DataController).
        private string GetUserDivision() =>
            User.FindFirstValue("division") ?? "DC000";

        [HttpGet("scrape")]
        public async Task<IActionResult> ScrapeMetaReal()
        {
            var cveDivision = GetUserDivision();
            var cacheKey    = $"meta-real-{cveDivision}";

            // ── 1. Cache fresca para esta división → devolver sin scrapear ──
            var cache = await _db.ScrapeCaches.FirstOrDefaultAsync(c => c.Clave == cacheKey);
            if (cache != null && DateTime.UtcNow - cache.FechaGuardadoUtc < CacheTtl)
                return Content(cache.Json, "application/json");

            // ── 2. Scrapear con la división del usuario autenticado ──
            var resultado = await _service.ObtenerDatosAsync(cveDivision: cveDivision);

            if (resultado == null || resultado.Data == null || resultado.Data.Count == 0)
                return NotFound(new { message = "No se encontraron datos en la tabla." });

            // ── 3. Transformar a {labels, datasets, rawTable} ──
            // MetaRealService emite InvariantCulture "N2": "5,060.00" (coma=miles, punto=decimal).
            // Quitamos las comas y parseamos con InvariantCulture.
            static double ParseVal(string? v)
            {
                var s = (v ?? "").Replace(",", "").Replace(" ", "").Trim();
                return double.TryParse(s,
                    System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var d) ? d : 0;
            }

            var firstRow  = resultado.Data[0];
            var labelKey  = firstRow.Keys.FirstOrDefault() ?? "Concepto";
            var zoneCodes = firstRow.Keys.Skip(1).ToList();

            var datasets = resultado.Data.Select(fila => new
            {
                label = fila.GetValueOrDefault(labelKey, ""),
                data  = zoneCodes.Select(z => ParseVal(fila.GetValueOrDefault(z))).ToArray(),
                borderColor     = ObtenerColor(fila.GetValueOrDefault(labelKey, "")),
                backgroundColor = ObtenerColor(fila.GetValueOrDefault(labelKey, ""))
                                    .Replace("rgb", "rgba").Replace(")", ", 0.2)")
            }).ToList();

            var response = new
            {
                labels       = zoneCodes,
                datasets,
                totalRecords = resultado.TotalRecords,
                status       = resultado.Status,
                rawTable     = resultado.Data
            };

            // ── 4. Guardar en caché y devolver ──
            var json = JsonSerializer.Serialize(response);
            if (cache == null)
            {
                cache = new ScrapeCache { Clave = cacheKey };
                _db.ScrapeCaches.Add(cache);
            }
            cache.Json              = json;
            cache.FechaGuardadoUtc  = DateTime.UtcNow;
            await _db.SaveChangesAsync();

            return Content(json, "application/json");
        }

        private static string ObtenerColor(string concepto)
        {
            if (concepto.Contains("REAL 2025")) return "rgb(46, 117, 182)";
            if (concepto.Contains("META 2026")) return "rgb(192, 48, 56)";
            if (concepto.Contains("REAL 2026")) return "rgb(112, 173, 71)";
            if (concepto.Contains("Diferencia")) return "rgb(237, 28, 36)";
            return "rgb(201, 203, 207)";
        }
    }
}
