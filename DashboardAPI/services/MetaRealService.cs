using Microsoft.Playwright;
using System.Text.RegularExpressions;
using System.Globalization;
using Microsoft.Extensions.Logging; 

namespace DashboardAPI.Services
{
    public class MetaRealService
    {
        private readonly ILogger<MetaRealService> _logger;

        public MetaRealService(ILogger<MetaRealService> logger)
        {
            _logger = logger;
        }

        public async Task<DashboardData> ObtenerDatosAsync(string? desde = null, string? hasta = null)
        {
            _logger.LogInformation("Iniciando scraping Meta Real (Playwright)...");

            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
            var page = await browser.NewPageAsync();
            var fechaDesde = DateTime.Now.ToString("yyyy//");
            var fechaHasta = DateTime.Now.ToString("yyyy/MM/dd");

            try
            {
                // 1. Navegación
                await page.GotoAsync("https://cssnal.cfe.mx/Inconformidades/gInconformidadesMetaReal.asp");
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

                // 2. Configuración de Filtros
                await page.SelectOptionAsync("select[name='cveDivision']", "DC000");
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
                            dict[kvp.Key] = kvp.Value.ToString("N2"); // Formato con decimales
                        }
                        
                        dict["TOTAL"] = f.Total.ToString("N2");
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