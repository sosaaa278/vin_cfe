using DashboardAPI.Helpers;
using DashboardAPI.Models;
using Microsoft.Playwright;
using System.Text.RegularExpressions;
using System.Globalization;
using Microsoft.Extensions.Logging;

namespace DashboardAPI.Services
{
    public class MetaRealService
    {
        private readonly ILogger<MetaRealService> _logger;
        private readonly MetaRealOptions _options;

        public MetaRealService(ILogger<MetaRealService> logger, MetaRealOptions options)
        {
            _logger  = logger;
            _options = options;
        }

        // Reuses the main scraper's authenticated Chromium profile so the CFE portal
        // respects the submitted cveDivision value (fresh sessions default to Norte).
        private const string MetaRealPlaywrightDir = "playwright-data-scraper";
        private const string UserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36";

        public async Task<DashboardData> ObtenerDatosAsync(string? desde = null, string? hasta = null, string cveDivision = "DC000")
        {
            _logger.LogInformation("Iniciando scraping Meta Real (Playwright) división={Div}...", cveDivision);

            var sem = PlaywrightDirLock.For(MetaRealPlaywrightDir);
            await sem.WaitAsync(TimeSpan.FromSeconds(120));

            using var playwright = await Playwright.CreateAsync();
            var dataDir = Path.Combine(Directory.GetCurrentDirectory(), MetaRealPlaywrightDir);
            var browser = await playwright.Chromium.LaunchPersistentContextAsync(dataDir,
                new BrowserTypeLaunchPersistentContextOptions
                {
                    Headless          = true,
                    UserAgent         = UserAgent,
                    SlowMo            = 200,
                    IgnoreHTTPSErrors = true,
                    Args              = ["--no-sandbox", "--disable-setuid-sandbox"]
                });

            var page = browser.Pages.Count > 0 ? browser.Pages[0] : await browser.NewPageAsync();
            var fechaDesde = RangoFechas.Desde(DateTime.Now.Year);
            var fechaHasta = DateTime.Now.ToString("yyyy/MM/dd");

            try
            {
                // 1. Primer navegamos al portal principal (solTermino.asp) y seleccionamos la
                //    división para que el servidor actualice Session("cveDivision") vía AJAX.
                //    gInconformidadesMetaReal.asp lee la división de la sesión del servidor
                //    (no del campo de formulario), por eso hay que "primear" la sesión aquí.
                const string sessionPrimerUrl = "https://cssnal.cfe.mx/Inconformidades/solTermino.asp";a
                _logger.LogInformation("Iniciando sesión de división en portal principal ({Url})...", sessionPrimerUrl);
                await page.GotoAsync(sessionPrimerUrl, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60_000 });
                try { await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 6_000 }); } catch { }

                await page.SelectOptionAsync("select[name='cveDivision']", cveDivision);
                try { await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 6_000 }); } catch { }
                _logger.LogInformation("Sesión de división actualizada a {Div}", cveDivision);

                // 2. Ahora navegamos a Meta-Real: la sesión ya apunta a la división correcta.
                await page.GotoAsync(_options.Url, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60_000 });
                try { await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 5_000 }); } catch { }

                // 3. Configuración de Filtros en Meta-Real (refuerzo belt-and-suspenders)
                await page.SelectOptionAsync("select[name='cveDivision']", cveDivision);
                try { await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 6_000 }); }
                catch { /* timeout aceptable */ }

                await page.SelectOptionAsync("select[name='cveProceso']", "D");
                await page.FillAsync("input#fechaDesde", desde ?? fechaDesde);
                await page.FillAsync("input#fechaHasta", hasta ?? fechaHasta);

                // 3. Clic y Espera
                _logger.LogInformation("Enviando formulario...");
                await page.ClickAsync("input#procesa");
                
                // Esperar tabla (sin RunAndWaitForRequestFinished que puede causar timeout)
                await page.Locator("#TABLE_13").WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 30000 });
                await Task.Delay(1000); // Pequeña pausa extra para renderizado

                // 4. Extracción
                var filas = await ExtraerTablaAsync(page);

                // 5. RETORNAR OBJETO DASHBOARD DATA (NO SOLO LA LISTA)
                return new DashboardData
                {
                    TotalRecords = filas.Count,
                    Status = "SUCCESS",
                    Data = filas.Select(f => 
                    {
                        // Convertir FilaTablaMeta a Dictionary<string, string>
                        var dict = new Dictionary<string, string>();
                        dict["Concepto"] = f.Concepto;
                        
                        // Agregar cada división (DC010, DC020, etc.)
                        foreach (var kvp in f.Valores)
                        {
                            dict[kvp.Key] = kvp.Value.ToString("N2", CultureInfo.InvariantCulture);
                        }

                        dict["TOTAL"] = f.Total.ToString("N2", CultureInfo.InvariantCulture);
                        return dict;
                    }).ToList()
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error crítico en el scraping de Meta Real");
                return new DashboardData
                {
                    TotalRecords = 0,
                    Status = "ERROR",
                    Data = new List<Dictionary<string, string>>()
                };
            }
            finally
            {
                try { await browser.CloseAsync(); } catch { }
                try { playwright.Dispose(); } catch { }
                sem.Release();
            }
        }

        private async Task<List<FilaTablaMeta>> ExtraerTablaAsync(IPage page)
        {
            var resultados = new List<FilaTablaMeta>();

            // Cabeceras (Códigos DCXXX)
            var headerCells = await page.Locator("#TABLE_13 thead tr td").AllAsync();
            var codigosDivision = new List<string>();

            foreach (var cell in headerCells)
            {
                var texto = (await cell.TextContentAsync())?.Trim();
                if (!string.IsNullOrEmpty(texto) && texto != "TOTAL")
                {
                    codigosDivision.Add(texto);
                }
            }

            // Filas de Datos
            var dataRows = await page.Locator("#TABLE_13 tbody tr").AllAsync();

            foreach (var row in dataRows)
            {
                var celdas = await row.Locator("td").AllAsync();
                if (celdas.Count < 2) continue;

                var conceptoRaw = await celdas[0].TextContentAsync();
                var concepto = conceptoRaw?.Trim().Replace("\n", "").Replace("\r", "") ?? "";
                if (string.IsNullOrEmpty(concepto)) continue;

                var fila = new FilaTablaMeta
                {
                    Concepto = concepto,
                    Valores = new Dictionary<string, decimal>(),
                    Total = 0
                };

                for (int i = 1; i < celdas.Count; i++)
                {
                    var rawText = await celdas[i].TextContentAsync();
                    
                    // Lógica para columna TOTAL al final
                    if (i - 1 >= codigosDivision.Count)
                    {
                        fila.Total = ParsearValor(rawText);
                        continue;
                    }

                    var codigo = codigosDivision[i - 1];
                    var valor = ParsearValor(rawText);
                    fila.Valores[codigo] = valor;
                }
                resultados.Add(fila);
            }

            return resultados;
        }

        private decimal ParsearValor(string? rawText)
        {
            if (string.IsNullOrEmpty(rawText)) return 0;
            // Elimina HTML, espacios y caracteres no numéricos (menos signos y decimales)
            var cleanText = Regex.Replace(rawText, @"[^\d\-,.]", "");
            
            if (decimal.TryParse(cleanText, NumberStyles.Any, CultureInfo.InvariantCulture, out var valor))
            {
                return valor;
            }
            return 0;
        }
    }

    // Modelo de Datos
    public class FilaTablaMeta
    {
        public string Concepto { get; set; } = "";
        public Dictionary<string, decimal> Valores { get; set; } = new();
        public decimal Total { get; set; }
    }
    
    public class DashboardData 
{
    public int TotalRecords { get; set; }
    public string Status { get; set; } = "";
    // Esta es la propiedad clave que el controlador usa ahora
    public List<Dictionary<string, string>> Data { get; set; } = new(); 
}
}   