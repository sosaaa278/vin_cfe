using Microsoft.AspNetCore.Mvc;
using DashboardAPI.Services;
using DashboardAPI.Data; //este es el appdbcontext
using DashboardAPI.Models; //ScrapeCache
using Microsoft.EntityFrameworkCore; //FirstOrDefaultAsync
using System.Text.Json; //JsonSerializer
using Microsoft.Extensions.Logging;

namespace DashboardAPI.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class InconformidadesMetaController : ControllerBase
    {
        private readonly MetaRealService _service;
        private readonly ILogger<InconformidadesMetaController> _logger;

        private readonly AppDbContext _db; //para llamar a la cache
        public InconformidadesMetaController(
        MetaRealService service,
        ILogger<InconformidadesMetaController> logger,
        AppDbContext db)                 // ← nuevo
        {
        _service = service;
        _logger = logger;
        _db = db;                        // ← nuevo
        }

        // Clave y tiempo de vida (TTL) de la caché de este endpoint.
        private const string CacheKey = "meta-real";
        private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(6);

        [HttpGet("scrape")]
        public async Task<IActionResult> ScrapeMetaReal()
        {
            try
            {
                _logger.LogInformation("Solicitud de scraping recibida");

                // ── PASO 3a: ¿hay caché fresca? → devolverla sin scrapear ──
                var cache = await _db.ScrapeCaches.FirstOrDefaultAsync(c => c.Clave == CacheKey);
                if (cache != null && DateTime.UtcNow - cache.FechaGuardadoUtc < CacheTtl)
                {
                    _logger.LogInformation("Devolviendo Meta-Real desde caché ({Edad:n0} min)",
                        (DateTime.UtcNow - cache.FechaGuardadoUtc).TotalMinutes);
                    return Content(cache.Json, "application/json");
                }

                // 1. Llamar al servicio (Devuelve DashboardData)
                var resultado = await _service.ObtenerDatosAsync();

                if (resultado == null || resultado.Data == null || resultado.Data.Count == 0)
                {
                    return NotFound(new { message = "No se encontraron datos en la tabla." });
                }

                // 2. Transformar datos crudos (List<Dictionary>) a formato Chart.js
                // Asumimos que la primera fila tiene las claves (ej. "Concepto", "Ene", "Feb"...)
                var primeraFila = resultado.Data[0];
                var claves = primeraFila.Keys.ToList();
                
                // Identificar columnas numéricas (excluyendo "Concepto" o similares)
                // Ajusta "Concepto" al nombre real de la columna en la tabla de CFE
                var columnaEtiqueta = claves.FirstOrDefault(k => k.Contains("Concepto") || k.Contains("Descripción")) ?? claves[0];
                var columnasNumericas = claves.Where(k => k != columnaEtiqueta).ToList();

                var datasets = resultado.Data.Select(fila => 
                {
                    var valores = columnasNumericas.Select(k => 
                    {
                        // Limpiar y convertir a número (manejar posibles formatos de moneda o %)
                        var rawValue = fila[k].Replace("$", "").Replace(",", "").Trim();
                        if (decimal.TryParse(rawValue, out var num)) return (double)num;
                        return 0.0;
                    }).ToList();

                    var concepto = fila[columnaEtiqueta];

                    return new
                    {
                        label = concepto,
                        data = valores,
                        borderColor = ObtenerColor(concepto),
                        backgroundColor = ObtenerColor(concepto).Replace("rgb", "rgba").Replace(")", ", 0.2)"),
                        fill = concepto.Contains("REAL")
                    };
                }).ToList();

                var response = new
                {
                    labels = columnasNumericas, // Ejes X (Meses, Divisiones, etc.)
                    datasets = datasets,
                    totalRecords = resultado.TotalRecords,
                    status = resultado.Status,
                    rawTable = resultado.Data // Datos crudos para tabla HTML
                };

                // ── PASO 3b: guardar en caché y devolver ──
                var json = JsonSerializer.Serialize(response);
                if (cache == null)
                {
                    cache = new ScrapeCache { Clave = CacheKey };
                    _db.ScrapeCaches.Add(cache);
                }
                cache.Json = json;
                cache.FechaGuardadoUtc = DateTime.UtcNow;
                await _db.SaveChangesAsync();

                return Content(json, "application/json");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error al procesar scraping");
                return StatusCode(500, new { error = ex.Message, detail = "Revisa la terminal del backend para más detalles." });
            }
        }

        private string ObtenerColor(string concepto)
        {
            if (concepto.Contains("REAL 2025")) return "rgb(54, 162, 235)"; 
            if (concepto.Contains("META 2026")) return "rgb(255, 206, 86)"; 
            if (concepto.Contains("REAL 2026")) return "rgb(75, 192, 192)"; 
            if (concepto.Contains("Diferencia")) return "rgb(255, 99, 132)"; 
            return "rgb(201, 203, 207)"; 
        }
    }
}   