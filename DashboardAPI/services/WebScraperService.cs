using HtmlAgilityPack;
using Microsoft.Playwright;
using DashboardAPI.Data;
using DashboardAPI.Models;
using Microsoft.EntityFrameworkCore;

namespace DashboardAPI.Services
{
    /// <summary>
    /// Se lanza cuando no se puede contactar el portal de CFE por un problema de
    /// red/DNS (p.ej. ERR_NAME_NOT_RESOLVED): no tiene sentido reintentar y el
    /// usuario debe revisar su conexión/VPN a la red interna de CFE.
    /// </summary>
    public class CfePortalUnreachableException : Exception
    {
        public CfePortalUnreachableException(string message) : base(message) { }
    }

    /// <summary>
    /// Extrae (scraping) los datos de inconformidades del portal interno de CFE.
    /// Usa Playwright (contexto persistente) porque las páginas se generan con JavaScript.
    /// La sesión/cookies se comparten entre llamadas reutilizando el mismo userDataDir.
    /// </summary>
    public class WebScraperService
    {
        // ── Configuración ───────────────────────────────────────────────────────────
        private const string UserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36";

        private const int MaxRetries            = 3;
        private const int BaseRetryMs           = 2000;   // se duplica en cada reintento (espera exponencial)
        private const int SlowMoMs              = 300;
        private const string PlaywrightDir      = "playwright-data-scraper";
        private const string CausasPlaywrightDir = "playwright-data-causas";

        // ── Selectores (centralizados para detectar cambios del portal) ──────────────
        private static class Sel
        {
            // Compartidos
            public const string Division   = "select[name='cveDivision']";
            public const string Zona       = "select[name='cveZona']";
            public const string Area       = "select[name='cveArea']";
            public const string FechaDesde = "input[name='fechaDesde']";
            public const string FechaHasta = "input[name='fechaHasta']";
            public const string Submit     = "#procesa";

            // Inconformidades
            public const string Proceso      = "select[name='cveProceso']";
            public const string ProcImproc   = "select[name='cveProcImproc']";
            public const string ResultTable  = "#TABLE_12";

            // Causas
            public const string Entidad          = "select[name='entidadFederativa']";
            public const string GrupoSolicitud   = "select[name='grupoSolicitud']";
            public const string CausaTerminacion = "select[name='cveCausaTerminacion']";
        }

        private readonly AppDbContext              _context;
        private readonly ILogger<WebScraperService> _logger;

        public WebScraperService(AppDbContext context, ILogger<WebScraperService> logger)
        {
            _context = context;
            _logger  = logger;
        }

        // ══════════════════════════════════════════════════════════════════════════════
        // AYUDANTES DEL NAVEGADOR
        // ══════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Crea un contexto persistente de Playwright que comparte cookies/sesión
        /// entre llamadas. Siempre liberar los recursos con la tupla devuelta.
        /// </summary>
        private async Task<(IPlaywright pw, IBrowserContext ctx)> CreateBrowserAsync(
            bool headless = true, int slowMo = SlowMoMs, string? dirName = null)
        {
            var pw      = await Playwright.CreateAsync();
            var dataDir = Path.Combine(Directory.GetCurrentDirectory(), dirName ?? PlaywrightDir);

            var ctx = await pw.Chromium.LaunchPersistentContextAsync(dataDir,
                new BrowserTypeLaunchPersistentContextOptions
                {
                    Headless          = headless,
                    UserAgent         = UserAgent,
                    SlowMo            = slowMo,
                    IgnoreHTTPSErrors = true,
                    Args              = ["--no-sandbox", "--disable-setuid-sandbox"]
                });

            return (pw, ctx);
        }

        /// <summary>
        /// Navega a <paramref name="url"/> con reintentos de espera exponencial.
        /// Devuelve true si tuvo éxito, false si fallan todos los intentos.
        /// </summary>
        private async Task<bool> NavigateWithRetryAsync(IPage page, string url)
        {
            for (int attempt = 0; attempt < MaxRetries; attempt++)
            {
                try
                {
                    await page.GotoAsync(url, new PageGotoOptions
                    {
                        WaitUntil = WaitUntilState.DOMContentLoaded,
                        Timeout   = 60_000
                    });
                    _logger.LogInformation("Navegado a {Url} (intento {A})", url, attempt + 1);
                    return true;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Intento de navegación {A}/{M} falló: {Err}", attempt + 1, MaxRetries, ex.Message);

                    // Errores de red/DNS: reintentar no ayuda. Falla rápido con mensaje claro.
                    if (ex.Message.Contains("ERR_NAME_NOT_RESOLVED") ||
                        ex.Message.Contains("ERR_INTERNET_DISCONNECTED") ||
                        ex.Message.Contains("ERR_CONNECTION") ||
                        ex.Message.Contains("ERR_ADDRESS_UNREACHABLE") ||
                        ex.Message.Contains("ERR_PROXY_CONNECTION_FAILED"))
                    {
                        throw new CfePortalUnreachableException(
                            "No se pudo conectar al portal de CFE (cssnal.cfe.mx). " +
                            "Verifica tu conexión a internet o la VPN/red interna de CFE.");
                    }

                    if (attempt < MaxRetries - 1)
                        await Task.Delay(BaseRetryMs * (int)Math.Pow(2, attempt));
                }
            }
            _logger.LogError("Fallaron los {M} intentos de navegación a {Url}", MaxRetries, url);
            return false;
        }

        // ══════════════════════════════════════════════════════════════════════════════
        // AYUDANTES PARA LEER EL HTML
        // ══════════════════════════════════════════════════════════════════════════════

        /// <summary>Decodifica entidades HTML y elimina los espacios duros (non-breaking).</summary>
        private static string Normalize(HtmlNode node) =>
            System.Net.WebUtility.HtmlDecode(node.InnerText)
                .Replace(" ", " ")
                .Trim();

        /// <summary>
        /// Lee la tabla de causas (TABLE_12).
        /// La fila de encabezado mezcla &lt;td&gt; (Sec, Grafica) y &lt;th&gt; (Clave, Descripcion…),
        /// así que leer solo los &lt;th&gt; desalinea todas las columnas. Este método lee TODAS
        /// las celdas del encabezado por índice, omite Grafica, y mapea cada celda de datos
        /// usando su posición exacta.
        /// </summary>
        private static List<Dictionary<string, string>> ParseCausasTable(HtmlNode table)
        {
            var rows = new List<Dictionary<string, string>>();

            var headerRow = table.SelectSingleNode(".//thead/tr")
                         ?? table.SelectSingleNode(".//tr");
            if (headerRow == null) return rows;

            var allCells = headerRow.SelectNodes("./td|./th");
            if (allCells == null) return rows;

            var colMap = new List<(int idx, string name)>();
            for (int i = 0; i < allCells.Count; i++)
            {
                var name = Normalize(allCells[i]);
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (name.Contains("GRAF", StringComparison.OrdinalIgnoreCase)) continue;
                colMap.Add((i, name.Trim()));
            }

            if (colMap.Count == 0) return rows;

            // Solo leemos las filas del tbody para no tratar el encabezado como dato
            var bodyRows = table.SelectNodes(".//tbody/tr");
            if (bodyRows == null) return rows;

            foreach (var tr in bodyRows)
            {
                var cells = tr.SelectNodes("./td");
                if (cells == null || cells.Count < 2) continue;

                var item = new Dictionary<string, string>();
                foreach (var (idx, name) in colMap)
                {
                    if (idx < cells.Count)
                        item[name] = Normalize(cells[idx]);
                }

                if (item.Count > 0) rows.Add(item);
            }

            return rows;
        }

        // ══════════════════════════════════════════════════════════════════════════════
        // GUARDADO EN BASE DE DATOS
        // ══════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Guarda en la base de datos las filas extraídas.
        /// Carga las claves ya existentes para <paramref name="fechaConsulta"/> en una sola
        /// consulta para evitar N+1 llamadas a AnyAsync.
        /// </summary>
        private async Task PersistRowsAsync(
            List<Dictionary<string, string>> rows,
            DateTime fechaConsulta)
        {
            // Una sola consulta para traer todas las claves ya existentes en esta fecha
            var existing = await _context.Inconformidades
                .Where(x => x.FechaConsulta.Date == fechaConsulta.Date)
                .Select(x => new { x.SEC, x.AREA, x.Codigo })
                .ToListAsync();

            var existingSet = existing
                .Select(x => $"{x.SEC}|{x.AREA}|{x.Codigo}")
                .ToHashSet(StringComparer.Ordinal);

            int added = 0;
            foreach (var row in rows)
            {
                if (!row.TryGetValue("SEC",  out var sec))  continue;
                if (!row.TryGetValue("AREA", out var area)) continue;

                foreach (var (codigo, valor) in row)
                {
                    if (codigo == "SEC" || codigo == "AREA") continue;

                    var key = $"{sec}|{area}|{codigo}";
                    if (!existingSet.Add(key)) continue; // ya existe o está duplicado en este lote

                    _context.Inconformidades.Add(new Inconformidad
                    {
                        FechaConsulta = fechaConsulta,
                        SEC           = sec,
                        AREA          = area,
                        Codigo        = codigo,
                        Valor         = valor
                    });
                    added++;
                }
            }

            if (added > 0) await _context.SaveChangesAsync();
            _logger.LogInformation("Guardados {Count} registros nuevos del {Date:yyyy-MM-dd}", added, fechaConsulta);
        }

        // ══════════════════════════════════════════════════════════════════════════════
        // API PÚBLICA
        // ══════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Extrae la tabla resumen de inconformidades para el rango de fechas dado,
        /// guarda las filas nuevas en la BD y devuelve los datos crudos.
        /// </summary>
        public async Task<List<Dictionary<string, string>>> GetTableData(
            string url, string fechaDesde, string fechaHasta, string cveDivision = "DC000")
        {
            _logger.LogInformation("GetTableData {Desde} → {Hasta}", fechaDesde, fechaHasta);
            var sem = PlaywrightDirLock.For(PlaywrightDir);
            await sem.WaitAsync(TimeSpan.FromSeconds(120));
            var (pw, ctx) = await CreateBrowserAsync();

            try
            {
                var page = ctx.Pages.FirstOrDefault() ?? await ctx.NewPageAsync();

                if (!await NavigateWithRetryAsync(page, url))
                    throw new InvalidOperationException($"Could not load {url} after {MaxRetries} attempts.");

                // Validamos el selector crítico antes de continuar
                await page.WaitForSelectorAsync(Sel.Division, new() { Timeout = 60_000 });

                // ── Llenar el formulario ──────────────────────────────────────────
                await page.SelectOptionAsync(Sel.Division,  cveDivision); await page.WaitForTimeoutAsync(1000);
                await page.SelectOptionAsync(Sel.Zona,      "00000"); await page.WaitForTimeoutAsync(800);
                await page.SelectOptionAsync(Sel.Area,      "00000"); await page.WaitForTimeoutAsync(800);
                await page.SelectOptionAsync(Sel.Proceso,   "D");     await page.WaitForTimeoutAsync(800);
                await page.SelectOptionAsync(Sel.ProcImproc,"T");     await page.WaitForTimeoutAsync(800);
                await page.FillAsync(Sel.FechaDesde, fechaDesde);
                await page.FillAsync(Sel.FechaHasta, fechaHasta);
                await page.WaitForTimeoutAsync(500);

                // ── Enviar y esperar los resultados ───────────────────────────────
                await page.ClickAsync(Sel.Submit);

                try
                {
                    await page.WaitForSelectorAsync(Sel.ResultTable, new() { Timeout = 30_000 });
                }
                catch
                {
                    _logger.LogWarning("No se detectó la tabla de resultados {Sel} — puede estar vacía o cambió el selector", Sel.ResultTable);
                }
                await page.WaitForTimeoutAsync(2000);

                // ── Leer el HTML ──────────────────────────────────────────────────
                var html = await page.ContentAsync();
                var doc  = new HtmlDocument();
                doc.LoadHtml(html);

                var table = doc.DocumentNode.SelectSingleNode("//table[@id='TABLE_12']");
                if (table == null)
                {
                    _logger.LogWarning("No se encontró TABLE_12 — el scraping no devolvió datos");
                    return [];
                }

                // Estructura de TABLE_12: fila 1 = título, fila 2 = encabezados <th>, fila 3+ = datos
                var headerCells = table.SelectNodes(".//tr[2]/th");
                if (headerCells == null) return [];

                var headers = new List<string> { "SEC", "AREA" };
                foreach (var h in headerCells)
                {
                    var t = h.InnerText.Trim();
                    if (!string.IsNullOrWhiteSpace(t)) headers.Add(t);
                }

                var result  = new List<Dictionary<string, string>>();
                var allRows = table.SelectNodes(".//tr");
                if (allRows == null) return result;

                for (int ri = 2; ri < allRows.Count; ri++)
                {
                    var tr    = allRows[ri];
                    var cells = tr.SelectNodes("./td");
                    if (cells == null || cells.Count < 3) continue;

                    var item = new Dictionary<string, string>
                    {
                        ["SEC"]  = cells[0].InnerText.Trim(),
                        ["AREA"] = cells[1].InnerText.Trim()
                    };

                    for (int i = 2; i < cells.Count && i < headers.Count; i++)
                        item[headers[i]] = cells[i].InnerText.Trim();

                    result.Add(item);
                }

                // ── Guardar en BD ─────────────────────────────────────────────────
                await PersistRowsAsync(result, DateTime.Parse(fechaHasta));
                _logger.LogInformation("GetTableData devolvió {Count} filas", result.Count);
                return result;
            }
            finally
            {
                try { await ctx.CloseAsync(); } catch { }
                try { pw.Dispose(); } catch { }
                sem.Release();
            }
        }

        /// <summary>Alias usado por el flujo de comparación; delega en GetTableData.</summary>
        public Task<List<Dictionary<string, string>>> GetComparisonData(
            string url, string fechaDesde, string fechaHasta)
            => GetTableData(url, fechaDesde, fechaHasta);

        // ══════════════════════════════════════════════════════════════════════════════
        // INCONFORMIDADES POR CADA MIL USUARIOS (REPORTE GENERAL)
        // ══════════════════════════════════════════════════════════════════════════════

        private const string ImuReportUrl =
            "https://cssnal.cfe.mx/Inconformidades/inconformidades.asp";

        /// <summary>
        /// Scrapea el reporte general "por cada mil usuarios" (IMU) de la División Norte,
        /// para la zona, mes y año indicados, y devuelve la tabla como lista de filas.
        /// Los encabezados de varios niveles (Comercial/Medición/Distribución) se aplanan
        /// en nombres únicos. Es MENSUAL para que coincida exactamente con el portal.
        /// </summary>
        /// <param name="cveZona">Clave de zona (00000 = todas).</param>
        /// <param name="mes">Mes a dos dígitos (01–12).</param>
        /// <param name="anio">Año (ej. 2026).</param>
        public async Task<List<Dictionary<string, string>>> GetImuReportAsync(
            string cveZona = "00000", string? mes = null, int? anio = null, string cveDivision = "DC000")
        {
            var hoy     = DateTime.Now;
            var useMes  = mes  ?? hoy.Month.ToString("D2");
            var useAnio = (anio ?? hoy.Year).ToString();
            _logger.LogInformation("GetImuReport zona={Zona} mes={Mes} año={Anio}", cveZona, useMes, useAnio);

            var (pw, ctx) = await CreateBrowserAsync(dirName: "playwright-data-imu");
            try
            {
                var page = ctx.Pages.FirstOrDefault() ?? await ctx.NewPageAsync();

                if (!await NavigateWithRetryAsync(page, ImuReportUrl))
                    throw new InvalidOperationException($"No se pudo cargar {ImuReportUrl}");

                // Esperamos a que cargue el formulario (selector compartido del portal).
                await page.WaitForSelectorAsync(Sel.Division, new() { Timeout = 60_000 });

                // ── Filtros: División Norte, IMU, MENSUAL del mes/año/zona seleccionados ──
                // MENSUAL ("M") para que los números coincidan exactamente con la pantalla del portal.
                // cveZona: el portal repuebla este <select> por AJAX según la división, así que la
                // opción de la zona puede no existir. Como al servidor solo le importa el valor que
                // se envía en el POST, si la opción falta la INYECTAMOS y la seleccionamos.
                await page.EvaluateAsync(
                    "([mes, anio, zona]) => {" +
                    "  var set = function(name, val) {" +
                    "    var el = document.querySelector(\"select[name='\" + name + \"'], input[name='\" + name + \"']\");" +
                    "    if (el) el.value = val;" +
                    "  };" +
                    "  set('cveDivision', '" + cveDivision + "');" +
                    "  var z = document.querySelector(\"select[name='cveZona']\");" +
                    "  if (z) {" +
                    "    var existe = Array.prototype.some.call(z.options, function(o){ return o.value === zona; });" +
                    "    if (!existe) { var opt = document.createElement('option'); opt.value = zona; opt.text = zona; z.add(opt); }" +
                    "    z.value = zona;" +
                    "  }" +
                    "  set('mes',         mes);" +
                    "  set('anio',        anio);" +
                    "  set('acumulado',   'M');" +      // MENSUAL (un solo mes, igual que el portal)
                    "  set('calculo',     'I');" +      // IMU
                    "}",
                    new[] { useMes, useAnio, cveZona });

                await page.WaitForTimeoutAsync(500);
                await page.ClickAsync(Sel.Submit);

                try
                {
                    await page.WaitForSelectorAsync("#principal tbody tr", new() { Timeout = 30_000 });
                }
                catch
                {
                    _logger.LogWarning("No se detectó la tabla #principal en el reporte IMU");
                }
                await page.WaitForTimeoutAsync(1500);

                var html = await page.ContentAsync();
                var doc  = new HtmlDocument();
                doc.LoadHtml(html);

                var table = doc.DocumentNode.SelectSingleNode("//table[@id='principal']");
                if (table == null)
                {
                    _logger.LogWarning("No se encontró la tabla del reporte IMU");
                    return [];
                }

                var rows = ParseMultiHeaderTable(table);
                _logger.LogInformation("GetImuReport devolvió {Count} filas", rows.Count);
                return rows;
            }
            finally
            {
                await ctx.CloseAsync();
                pw.Dispose();
            }
        }

        /// <summary>
        /// Lee una tabla con encabezado de varios niveles (con colspan/rowspan).
        /// Expande el encabezado a una rejilla, compone un nombre único por columna
        /// uniendo los textos de cada nivel (ej. "COMERCIAL PROCEDENTES CONS ANORM"),
        /// y mapea cada celda de datos por su posición de columna.
        /// </summary>
        private static List<Dictionary<string, string>> ParseMultiHeaderTable(HtmlNode table)
        {
            var result = new List<Dictionary<string, string>>();

            // Esta tabla mezcla <th> y <td> en el encabezado, así que separamos por
            // <thead>/<tbody>. Si no existen, caemos a la heurística de "fila con <th>".
            var thead = table.SelectSingleNode(".//thead");
            var tbody = table.SelectSingleNode(".//tbody");

            List<HtmlNode> headerRows;
            List<HtmlNode> dataRows;

            if (thead != null && tbody != null)
            {
                headerRows = thead.SelectNodes("./tr")?.ToList() ?? new List<HtmlNode>();
                dataRows   = tbody.SelectNodes("./tr")?.ToList() ?? new List<HtmlNode>();
            }
            else
            {
                var allRows = table.SelectNodes(".//tr");
                if (allRows == null) return result;
                headerRows = allRows.Where(r => r.SelectNodes("./th") != null).ToList();
                dataRows   = allRows.Where(r => r.SelectNodes("./th") == null
                                             && r.SelectNodes("./td") != null).ToList();
            }
            if (headerRows.Count == 0) return result;

            // Rejilla [fila][columna] con el texto de cada celda, respetando colspan/rowspan.
            var grid    = new Dictionary<(int row, int col), string>();
            var rowSpan = new Dictionary<int, (string text, int left)>(); // columnas aún ocupadas por un rowspan

            for (int r = 0; r < headerRows.Count; r++)
            {
                int col = 0;
                var cells = headerRows[r].SelectNodes("./td|./th") ?? new HtmlNodeCollection(null);
                foreach (var cell in cells)
                {
                    // Saltamos columnas que siguen ocupadas por un rowspan de filas anteriores.
                    while (rowSpan.TryGetValue(col, out var occ) && occ.left > 0)
                    {
                        grid[(r, col)] = occ.text;
                        rowSpan[col] = (occ.text, occ.left - 1);
                        col++;
                    }

                    var text    = Normalize(cell);
                    int cspan   = int.TryParse(cell.GetAttributeValue("colspan", "1"), out var cs) ? cs : 1;
                    int rspan   = int.TryParse(cell.GetAttributeValue("rowspan", "1"), out var rs) ? rs : 1;

                    for (int c = 0; c < cspan; c++)
                    {
                        grid[(r, col)] = text;
                        if (rspan > 1) rowSpan[col] = (text, rspan - 1);
                        col++;
                    }
                }
            }

            int totalCols = grid.Keys.Count == 0 ? 0 : grid.Keys.Max(k => k.col) + 1;

            // Nombre de columna = textos no vacíos de cada nivel, unidos y deduplicados.
            var columns = new List<string>();
            var seen    = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int c = 0; c < totalCols; c++)
            {
                var parts = new List<string>();
                for (int r = 0; r < headerRows.Count; r++)
                {
                    if (grid.TryGetValue((r, c), out var t) &&
                        !string.IsNullOrWhiteSpace(t) &&
                        (parts.Count == 0 || parts[^1] != t))
                        parts.Add(t);
                }
                var name = parts.Count > 0 ? string.Join(" ", parts) : $"COL{c}";
                // Garantizamos unicidad por si aún quedan nombres repetidos.
                if (seen.TryGetValue(name, out var n)) { seen[name] = n + 1; name = $"{name} ({n + 1})"; }
                else seen[name] = 1;
                columns.Add(name);
            }

            // Filas de datos: mapeamos cada celda a su columna por índice.
            foreach (var tr in dataRows)
            {
                var cells = tr.SelectNodes("./td");
                if (cells == null || cells.Count < 2) continue;

                var item = new Dictionary<string, string>();
                for (int c = 0; c < cells.Count && c < columns.Count; c++)
                    item[columns[c]] = Normalize(cells[c]);

                if (item.Count > 0) result.Add(item);
            }

            return result;
        }

        // ══════════════════════════════════════════════════════════════════════════════
        // CAUSAS — núcleo privado (un scrape por código, reutiliza una página existente)
        // ══════════════════════════════════════════════════════════════════════════════

        private async Task<List<Dictionary<string, string>>> ScrapeCausaCodeAsync(
            IPage page, string desde, string hasta, string tipoSolTermino, string cveZona = "00000", string cveDivision = "DC000")
        {
            const string url = "https://cssnal.cfe.mx/iessInformesV2/causasTerminacion.asp";
            _logger.LogInformation("GetCausasData {Desde} -> {Hasta} code={Code}", desde.Replace("-", "/"), hasta.Replace("-", "/"), tipoSolTermino);

            bool noDataDialog = false;
            EventHandler<IDialog> dlgHandler = async (_, dialog) =>
            {
                _logger.LogInformation("Dialog for {Code}: {Msg}", tipoSolTermino, dialog.Message);
                noDataDialog = true;
                await dialog.AcceptAsync();
            };
            page.Dialog += dlgHandler;

            try
            {
                if (!await NavigateWithRetryAsync(page, url))
                    throw new InvalidOperationException("Could not load causas page.");

                // Esperamos el campo de fecha — si se agota el tiempo, probablemente expiró la sesión
                try
                {
                    await page.WaitForSelectorAsync(
                        "input[name='fechaDesde']", new() { Timeout = 30_000 });
                }
                catch (Exception ex)
                {
                    _logger.LogError("WaitForSelector fechaDesde failed: {Err}. Check debug_causas_initial.html", ex.Message);
                    throw new InvalidOperationException(
                        "La página de causas no cargó el formulario (sesión expirada o error de red). Revisa debug_causas_initial.html", ex);
                }

                // CLAVE para el filtro por zona:
                // El portal dispara en $(document).ready un AJAX a lib/llenaCombos.asp que
                // REEMPLAZA el <select cveZona> con $("#cveZona").html(data). Si fijamos el
                // valor antes de que ese AJAX termine, la respuesta lo borra y la zona queda
                // en "00000" (Nacional) — ese era el bug de E02 (primer código, AJAX en vuelo).
                // Esperamos a que la red quede inactiva para que el combo ya esté repoblado.
                try
                {
                    await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 15_000 });
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("NetworkIdle (carga inicial de combos) timeout {Code}: {Err}", tipoSolTermino, ex.Message);
                }
                await page.WaitForTimeoutAsync(600);

                // Captura las zonas del select cveZona (ya repoblado por AJAX) para cachearlas.
                await TryCacheZonasAsync(page, cveDivision);

                // Llenamos los campos del formulario y devolvemos un objeto de diagnóstico para confirmar los valores
                var formState = await page.EvaluateAsync<Dictionary<string, string>>(
                    "([d, h, ts, z]) => {" +
                    "  var setField = function(name, val) {" +
                    "    var el = document.querySelector(\"select[name='\" + name + \"']\");" +
                    "    if (el) el.value = val;" +
                    "  };" +
                    "  var fd = document.querySelector(\"input[name='fechaDesde']\");" +
                    "  var fh = document.querySelector(\"input[name='fechaHasta']\");" +
                    "  if (fd) fd.value = d;" +
                    "  if (fh) fh.value = h;" +
                    "  setField('cveDivision',          '" + cveDivision + "');" +
                    "  setField('cveZona',              z);" +
                    "  setField('cveArea',              '00000');" +
                    "  setField('entidadFederativa',    '0');" +
                    "  setField('cveMunicipio',         'T');" +
                    "  setField('grupoSolicitud',       'T');" +   // 'T' = Todos grupo solicitudes (sin filtrar por RSS)
                    "  setField('tipoSolInicio',        'T');" +
                    "  setField('tipoSolTermino',       ts);" +
                    "  setField('cveCausaTerminacion',  'T');" +
                    "  setField('cvePivoteTerminacion', 'cveCausaTerminacion');" +
                    "  var get = function(name) {" +
                    "    var el = document.querySelector(\"[name='\" + name + \"']\");" +
                    "    return el ? el.value : 'NOT_FOUND';" +
                    "  };" +
                    "  return {" +
                    "    fechaDesde:           get('fechaDesde')," +
                    "    fechaHasta:           get('fechaHasta')," +
                    "    grupoSolicitud:       get('grupoSolicitud')," +
                    "    tipoSolTermino:       get('tipoSolTermino')," +
                    "    cvePivoteTerminacion: get('cvePivoteTerminacion')" +
                    "  };" +
                    "}",
                    new[] { desde, hasta, tipoSolTermino, cveZona });

                _logger.LogInformation("Form state before submit: {@State}", formState);

                // Reafirma cveZona justo antes de enviar. El AJAX llenaCombos del portal
                // suele dejar el <select> sin la opción esperada (por eso antes obteníamos
                // ""). Como al servidor solo le importa el valor enviado en el POST, si la
                // opción no existe la INYECTAMOS y la seleccionamos: así el form envía la
                // zona correcta sin depender de lo que dejó el AJAX.
                var zonaFijada = await page.EvaluateAsync<string>(
                    "(z) => {" +
                    "  var el = document.querySelector(\"select[name='cveZona']\");" +
                    "  if (!el) return 'NO_SELECT';" +
                    "  var existe = Array.prototype.some.call(el.options, function(o){ return o.value === z; });" +
                    "  if (!existe) { var opt = document.createElement('option'); opt.value = z; opt.text = z; el.add(opt); }" +
                    "  el.value = z;" +
                    "  return el.value;" +
                    "}",
                    cveZona);
                if (zonaFijada != cveZona)
                    _logger.LogWarning("cveZona NO se fijó para {Code}: esperado={Exp} obtenido={Got}", tipoSolTermino, cveZona, zonaFijada);
                else
                    _logger.LogInformation("cveZona fijada en {Zona} para {Code}", cveZona, tipoSolTermino);

                // Esperamos a que termine el POST del formulario y la nueva página se estabilice.
                // TABLE_12 ya existe en la carga inicial, así que NO podemos esperar por ella —
                // hay que esperar a que la página se recargue tras el POST.
                await page.ClickAsync("#procesa");
                try
                {
                    await page.WaitForLoadStateAsync(LoadState.NetworkIdle,
                        new() { Timeout = 25_000 });
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("NetworkIdle after submit timed out for {Code}: {Err}", tipoSolTermino, ex.Message);
                }
                await page.WaitForTimeoutAsync(1500);

                var html = await page.ContentAsync();

                var doc = new HtmlDocument();
                doc.LoadHtml(html);

                // Si el portal mostró una alerta de "sin datos", la TABLE_12 vieja sigue
                // en el DOM — lo tratamos como resultado vacío y no como datos viejos.
                if (noDataDialog)
                {
                    _logger.LogInformation("Diálogo de 'sin datos' detectado para {Code} — devolviendo []", tipoSolTermino);
                    return [];
                }

                var table = doc.DocumentNode.SelectSingleNode("//table[@id='TABLE_12']");
                if (table == null)
                {
                    _logger.LogWarning("No se encontró TABLE_12 en el HTML de causas para {Code}", tipoSolTermino);
                    return [];
                }

                // ParseCausasTable lee TODAS las celdas del encabezado (td + th) por índice,
                // lo que alinea correctamente Sec, Clave, Descripcion, Causas, %
                // omitiendo la columna Grafica.
                var rows = ParseCausasTable(table);
                _logger.LogInformation("GetCausasData({Code}) devolvió {Count} filas", tipoSolTermino, rows.Count);
                return rows;
            }
            finally
            {
                page.Dialog -= dlgHandler;
            }
        }

        // ══════════════════════════════════════════════════════════════════════════════
        // ZONAS POR DIVISIÓN  (caché en memoria; se llena como efecto secundario del scraping)
        // ══════════════════════════════════════════════════════════════════════════════

        // Caché estático: última lista de zonas conocida por división (se sobreescribe en cada Consultar)
        private static readonly Dictionary<string, List<Dictionary<string, string>>> _zonasCache = new();

        // Flag de instancia: garantiza que el refresh solo ocurra UNA VEZ por petición HTTP
        // (WebScraperService es Scoped → instancia nueva por request → flag siempre parte en false)
        private bool _zonasRefreshedThisRequest = false;

        /// <summary>
        /// Dispara el AJAX de zonas para <paramref name="cveDivision"/> usando SelectOptionAsync
        /// (que sí lanza el evento change) y actualiza el caché. Se ejecuta exactamente una vez
        /// por petición HTTP (flag de instancia), garantizando que siempre refleje el portal actual.
        /// </summary>
        private async Task TryCacheZonasAsync(IPage page, string cveDivision)
        {
            if (_zonasRefreshedThisRequest) return; // Ya refrescamos en este Consultar
            _zonasRefreshedThisRequest = true;
            try
            {
                // SelectOptionAsync lanza el evento "change" → dispara el AJAX llenaCombos.asp
                // que REEMPLAZA las opciones de cveZona con las zonas de la división seleccionada.
                await page.SelectOptionAsync(Sel.Division, cveDivision);
                try { await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 5_000 }); }
                catch { }

                var json = await page.EvaluateAsync<string>(
                    "() => JSON.stringify(" +
                    "  Array.from(document.querySelectorAll(\"select[name='cveZona'] option\"))" +
                    "  .map(o => ({ value: o.value.trim(), label: o.text.trim() }))" +
                    ")");
                if (string.IsNullOrWhiteSpace(json)) return;
                var list = System.Text.Json.JsonSerializer.Deserialize<List<Dictionary<string, string>>>(json);
                if (list?.Count > 1)   // Al menos "Todas las zonas" + 1 sub-zona
                    _zonasCache[cveDivision] = list;  // Siempre sobreescribe (sin datos viejos)
            }
            catch { /* no romper el scraping principal */ }
        }

        /// <summary>
        /// Devuelve las zonas cacheadas para la división (pobladas por el scraping de causas).
        /// Si aún no se ha hecho ningún scraping, devuelve solo "Todas las zonas".
        /// </summary>
        public Task<List<Dictionary<string, string>>> GetZonasForDivisionAsync(string cveDivision)
        {
            _zonasCache.TryGetValue(cveDivision, out var hit);
            return Task.FromResult(hit ?? [new() { ["value"] = "00000", ["label"] = "Todas las zonas" }]);
        }

        public async Task<Dictionary<string, List<Dictionary<string, string>>>> GetCausasDataAllAsync(
            string fechaDesde, string fechaHasta, IEnumerable<string> codes, string cveZona = "00000", string cveDivision = "DC000")
        {
            var desde  = fechaDesde.Replace("/", "-");
            var hasta  = fechaHasta.Replace("/", "-");
            var result = new Dictionary<string, List<Dictionary<string, string>>>();

            var (pw, ctx) = await CreateBrowserAsync(headless: true, slowMo: 300, dirName: CausasPlaywrightDir);
            try
            {
                var page = ctx.Pages.Count > 0 ? ctx.Pages[0] : await ctx.NewPageAsync();
                foreach (var code in codes)
                {
                    try
                    {
                        result[code] = await ScrapeCausaCodeAsync(page, desde, hasta, code, cveZona, cveDivision);
                    }
                    catch (CfePortalUnreachableException)
                    {
                        // Si el host no resuelve, los demás códigos también fallarán: aborta.
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning("GetCausasDataAll code {Code} zone {Zone} failed: {Err}", code, cveZona, ex.Message);
                        result[code] = [];
                    }
                }
            }
            finally
            {
                await ctx.CloseAsync();
                pw.Dispose();
            }
            return result;
        }

    }
}
