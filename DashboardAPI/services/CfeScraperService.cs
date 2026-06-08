using Microsoft.Playwright;
using System.Text.Json;

public class CfeScraperService
{
    public async Task<DashboardData> ScrapeInconformidadesAsync()
    {
        Console.WriteLine("🚀 Iniciando scraper C#...");

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new()
        {
            Headless = false, // true para producción
            SlowMo = 50
        });

        var context = await browser.NewContextAsync(new()
        {
            ViewportSize = new ViewportSize { Width = 1920, Height = 1080 }
        });

        var page = await context.NewPageAsync();

        try
        {
            // 1. Navegar
            Console.WriteLine("🔗 Navegando a CFE...");
            await page.GotoAsync(
                "https://cssnal.cfe.mx/Inconformidades/gInconformidadesMetaReal.asp",
                new() { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 60000 }
            );

            // 2. Configurar Filtros
            Console.WriteLine("⚙️ Configurando filtros...");
            
            // División: NORTE
            await page.Locator("select[name='cveDivision']").SelectOptionAsync(new SelectOptionValue { Value = "DC000" });
            
            // Proceso: DISTRIBUCION
            await page.Locator("select[name='cveProceso']").SelectOptionAsync(new SelectOptionValue { Value = "D" });
            
            // Fechas
            await page.Locator("input[name='fechaDesde']").FillAsync("2026/01/01");
            await page.Locator("input[name='fechaHasta']").FillAsync("2026/06/02");

            // 3. Click en PROCESA
            Console.WriteLine("🔘 Ejecutando búsqueda...");
            await page.Locator("input[name='procesa'][value='PROCESA']").ClickAsync();

            // 4. Esperar carga de tabla
            Console.WriteLine("⏳ Esperando resultados...");
            await page.WaitForSelectorAsync("table", new() { Timeout = 30000 });
            await Task.Delay(2000);

            // 5. Extraer Datos de la Tabla
            Console.WriteLine("📊 Extrayendo datos...");
            
            var tableData = await page.EvaluateAsync<TableData>(@"() => {
                const tables = document.querySelectorAll('table');
                if (tables.length === 0) return null;

                const table = tables[0];
                const headers = [];
                const rows = [];

                // Headers
                const headerRow = table.querySelector('tr');
                if (headerRow) {
                    const ths = headerRow.querySelectorAll('th, td');
                    ths.forEach(th => headers.push(th.textContent.trim()));
                }

                // Filas
                const dataRows = table.querySelectorAll('tr');
                dataRows.forEach((tr, index) => {
                    if (index === 0) return;
                    const cells = tr.querySelectorAll('td');
                    if (cells.length > 0) {
                        const rowData = {};
                        cells.forEach((cell, i) => {
                            const key = headers[i] || `Columna${i}`;
                            rowData[key] = cell.textContent.trim();
                        });
                        rows.push(rowData);
                    }
                });

                return { headers, rows };
            }");

            // 6. Preparar datos para Dashboard
            var dashboardData = new DashboardData
            {
                TotalRecords = tableData?.Rows?.Count ?? 0,
                TableDetected = (tableData?.Rows?.Count > 0) ? "YES" : "NO",
                Status = tableData != null ? "SUCCESS" : "ERROR",
                Timestamp = DateTime.UtcNow.ToString("o"),
                Filters = new Filters
                {
                    Division = "NORTE",
                    Proceso = "DISTRIBUCION",
                    FechaDesde = "2026/01/01",
                    FechaHasta = "2026/06/02"
                },
                Headers = tableData?.Headers ?? new List<string>(),
                Data = tableData?.Rows ?? new List<Dictionary<string, string>>()
            };

            Console.WriteLine($"✅ Éxito! Registros: {dashboardData.TotalRecords}");
            return dashboardData;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Error: {ex.Message}");
            return new DashboardData
            {
                TotalRecords = 0,
                TableDetected = "NO",
                Status = "ERROR",
                Error = ex.Message,
                Timestamp = DateTime.UtcNow.ToString("o")
            };
        }
    }
}

// Modelos
public class DashboardData
{
    public int TotalRecords { get; set; }
    public string TableDetected { get; set; } = "";
    public string Status { get; set; } = "";
    public string? Error { get; set; }
    public string Timestamp { get; set; } = "";
    public Filters Filters { get; set; } = new();
    public List<string> Headers { get; set; } = new();
    public List<Dictionary<string, string>> Data { get; set; } = new();
}

public class Filters
{
    public string Division { get; set; } = "";
    public string Proceso { get; set; } = "";
    public string FechaDesde { get; set; } = "";
    public string FechaHasta { get; set; } = "";
}

public class TableData
{
    public List<string> Headers { get; set; } = new();
    public List<Dictionary<string, string>> Rows { get; set; } = new();
}   