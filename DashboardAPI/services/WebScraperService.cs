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
        private const string ColoniasPlaywrightDir = "playwright-data-colonias";

        // Confirmado por DevTools (Network → Payload) contra statusAtencion.asp: el campo
        // "Agrupar" en realidad se llama cvePivote, y su valor para agrupar por colonia es
        // el nombre del propio campo de colonia — mismo patrón que cvePivoteTerminacion en causas.
        private const string AgruparColoniaValue = "cveColonia";

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

            // Colonias — confirmados por DevTools contra statusAtencion.asp
            public const string Colonia = "select[name='cveColonia']";
            public const string Agrupar = "select[name='cvePivote']";
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
        /// Cierra el contexto de Playwright con un tope de tiempo. Visto en producción:
        /// ctx.CloseAsync() a veces se queda colgado (sin lanzar excepción) después de que
        /// el scrape ya terminó y los datos ya están en memoria — eso bloqueaba la respuesta
        /// HTTP para siempre (el navegador del usuario se quedaba "cargando" aunque el log
        /// del backend ya mostrara el scrape exitoso). Perder la limpieza del contexto no
        /// pierde datos del usuario, así que después de este tope simplemente se sigue.
        /// </summary>
        private async Task CloseBrowserSafeAsync(IPlaywright pw, IBrowserContext ctx)
        {
            try
            {
                await ctx.CloseAsync().WaitAsync(TimeSpan.FromSeconds(15));
            }
            catch (Exception ex)
            {
                _logger.LogWarning("ctx.CloseAsync() tardó más de 15s o falló, se continúa sin bloquear la respuesta: {Err}", ex.Message);
            }
            finally
            {
                pw.Dispose();
            }
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

        /// <summary>Carpeta donde se guardan los volcados de HTML de diagnóstico (fuera de wwwroot).</summary>
        public const string DebugDumpDir = "debug-dumps";

        /// <summary>
        /// Guarda el HTML de la página actual para diagnóstico cuando un selector esperado
        /// no aparece (ej. cambió el portal). Nunca lanza — un fallo al volcar no debe
        /// enmascarar el error real del scraping. El archivo se puede leer luego vía
        /// GET /api/data/colonias/debug sin necesitar acceso SSH al servidor.
        /// </summary>
        private async Task DumpDebugHtmlAsync(IPage page, string tag)
        {
            try
            {
                var dir = Path.Combine(Directory.GetCurrentDirectory(), DebugDumpDir);
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, $"debug_{tag}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.html");
                var html = await page.ContentAsync();
                await File.WriteAllTextAsync(path, html);
                _logger.LogWarning("Volcado de diagnóstico guardado en {Path}", path);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("No se pudo guardar el volcado de diagnóstico para {Tag}: {Err}", tag, ex.Message);
            }
        }

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

        /// <summary>
        /// Lee la tabla de colonias (id="mytable"). El encabezado es de 3 niveles
        /// (colspan/rowspan), igual que ParseMultiHeaderTable — pero a diferencia de esa
        /// tabla, el portal DUPLICA cada celda de métrica en el body: una copia oculta
        /// (acumulado histórico, class *T/*S, style "display: none") y una visible (el
        /// rango de fechas elegido, class *I, style "display: table-cell"). Si mapeáramos
        /// por posición sin filtrar, cada columna quedaría desalineada con la siguiente.
        /// Confirmado por DevTools contra statusAtencion.asp (ver HTML real de la tabla).
        /// </summary>
        private static List<Dictionary<string, string>> ParseColoniasTable(HtmlNode table)
        {
            var result = new List<Dictionary<string, string>>();

            var thead = table.SelectSingleNode(".//thead");
            var tbody = table.SelectSingleNode(".//tbody");
            if (thead == null || tbody == null) return result;

            var headerRows = thead.SelectNodes("./tr")?.ToList() ?? new List<HtmlNode>();
            var dataRows   = tbody.SelectNodes("./tr")?.ToList() ?? new List<HtmlNode>();
            if (headerRows.Count == 0) return result;

            // Rejilla de encabezado. NOTA: a diferencia de ParseMultiHeaderTable, este
            // recorrido NO se detiene en la primera columna sin rowspan pendiente — sigue
            // revisando columna por columna mientras haya celdas reales por colocar O
            // arrastres de rowspan todavía activos más adelante. Esto es necesario porque
            // esta tabla tiene VARIAS columnas seguidas con distinto rowspan al final de una
            // fila corta (Pendientes rowspan=2, Con óptico rowspan=3, Reiterativas rowspan=2,
            // justo después de una fila que solo define Rechazadas/Canceladas/Terminadas) —
            // el algoritmo simple de ParseMultiHeaderTable corta el arrastre demasiado pronto
            // ahí y desalinea todas las columnas desde "Pendientes" en adelante.
            var grid       = new Dictionary<(int row, int col), string>();
            var rowSpan    = new Dictionary<int, (string text, int left)>();

            for (int r = 0; r < headerRows.Count; r++)
            {
                var cells = headerRows[r].SelectNodes("./td|./th") ?? new HtmlNodeCollection(null);
                int col = 0;
                int cellIdx = 0;

                while (cellIdx < cells.Count || rowSpan.Any(kv => kv.Key >= col && kv.Value.left > 0))
                {
                    if (rowSpan.TryGetValue(col, out var occ) && occ.left > 0)
                    {
                        grid[(r, col)] = occ.text;
                        rowSpan[col] = (occ.text, occ.left - 1);
                        col++;
                        continue;
                    }

                    if (cellIdx >= cells.Count) break;

                    var cell  = cells[cellIdx++];
                    var text  = Normalize(cell);
                    int cspan = int.TryParse(cell.GetAttributeValue("colspan", "1"), out var cs) ? cs : 1;
                    int rspan = int.TryParse(cell.GetAttributeValue("rowspan", "1"), out var rs) ? rs : 1;

                    for (int c = 0; c < cspan; c++)
                    {
                        grid[(r, col)] = text;
                        if (rspan > 1) rowSpan[col] = (text, rspan - 1);
                        col++;
                    }
                }
            }

            int totalCols = grid.Keys.Count == 0 ? 0 : grid.Keys.Max(k => k.col) + 1;

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
                if (seen.TryGetValue(name, out var n)) { seen[name] = n + 1; name = $"{name} ({n + 1})"; }
                else seen[name] = 1;
                columns.Add(name);
            }

            // Filas de datos: descartamos las celdas ocultas (duplicado "T"/"S" del
            // acumulado histórico) y nos quedamos solo con las visibles ("I" = intervalo,
            // el rango de fechas elegido) — así el conteo de celdas visibles cuadra 1:1
            // con las columnas del encabezado.
            foreach (var tr in dataRows)
            {
                var allCells = tr.SelectNodes("./td");
                if (allCells == null || allCells.Count < 2) continue;

                var visibleCells = allCells
                    .Where(td => !td.GetAttributeValue("style", "").Replace(" ", "")
                                     .Contains("display:none", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                var item = new Dictionary<string, string>();
                for (int c = 0; c < visibleCells.Count && c < columns.Count; c++)
                    item[columns[c]] = Normalize(visibleCells[c]);

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

        // ══════════════════════════════════════════════════════════════════════════════
        // ÁREAS POR ZONA  (mismo patrón que zonas por división, pero anidado un nivel más)
        // ══════════════════════════════════════════════════════════════════════════════

        private static readonly Dictionary<string, List<Dictionary<string, string>>> _areasCache = new();
        private bool _areasRefreshedThisRequest = false;

        /// <summary>
        /// Dispara el AJAX de áreas para <paramref name="cveZona"/> (análogo a
        /// TryCacheZonasAsync, pero repoblando select[name='cveArea']) y actualiza el caché.
        /// Se ejecuta una vez por petición HTTP.
        /// </summary>
        private async Task TryCacheAreasAsync(IPage page, string cveZona)
        {
            if (_areasRefreshedThisRequest) return;
            _areasRefreshedThisRequest = true;
            try
            {
                await page.SelectOptionAsync(Sel.Zona, cveZona);
                try { await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 5_000 }); }
                catch { }

                var json = await page.EvaluateAsync<string>(
                    "() => JSON.stringify(" +
                    "  Array.from(document.querySelectorAll(\"select[name='cveArea'] option\"))" +
                    "  .map(o => ({ value: o.value.trim(), label: o.text.trim() }))" +
                    ")");
                if (string.IsNullOrWhiteSpace(json)) return;
                var list = System.Text.Json.JsonSerializer.Deserialize<List<Dictionary<string, string>>>(json);
                if (list?.Count > 1)
                    _areasCache[cveZona] = list;
            }
            catch { /* no romper el scraping principal */ }
        }

        /// <summary>
        /// Devuelve las áreas cacheadas para la zona (pobladas como efecto secundario del
        /// scraping de colonias). Si aún no se ha hecho ningún scraping, devuelve solo "Todas las áreas".
        /// </summary>
        public Task<List<Dictionary<string, string>>> GetAreasForZonaAsync(string cveZona)
        {
            _areasCache.TryGetValue(cveZona, out var hit);
            return Task.FromResult(hit ?? [new() { ["value"] = "00000", ["label"] = "Todas las áreas" }]);
        }

        /// <summary>
        /// Consulta EN VIVO las áreas de una zona (sin depender de que ya se haya hecho
        /// un scrape completo antes). Navega al formulario de colonias, selecciona
        /// División→Zona (disparando el AJAX que repuebla cveArea) y lee las opciones
        /// resultantes. Más lento que el caché (una navegación real, ~2-5s), pero
        /// funciona la primera vez que se elige una zona nueva.
        /// </summary>
        public async Task<List<Dictionary<string, string>>> GetAreasLiveAsync(string cveZona, string cveDivision = "DC000")
        {
            _logger.LogInformation("GetAreasLive zona={Zona} division={Div}", cveZona, cveDivision);
            var (pw, ctx) = await CreateBrowserAsync(dirName: ColoniasPlaywrightDir);
            try
            {
                var page = ctx.Pages.FirstOrDefault() ?? await ctx.NewPageAsync();

                if (!await NavigateWithRetryAsync(page, ColoniasReportUrl))
                    throw new InvalidOperationException($"No se pudo cargar {ColoniasReportUrl}");

                await page.WaitForSelectorAsync(Sel.FechaDesde, new() { Timeout = 30_000 });
                try { await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 15_000 }); } catch { }

                await page.SelectOptionAsync(Sel.Division, cveDivision);
                try { await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 5_000 }); } catch { }
                await page.WaitForTimeoutAsync(400); // margen extra por si el AJAX del portal tiene debounce

                await page.SelectOptionAsync(Sel.Zona, cveZona);
                try { await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 5_000 }); } catch { }
                await page.WaitForTimeoutAsync(400);

                var json = await page.EvaluateAsync<string>(
                    "() => JSON.stringify(" +
                    "  Array.from(document.querySelectorAll(\"select[name='cveArea'] option\"))" +
                    "  .map(o => ({ value: o.value.trim(), label: o.text.trim() }))" +
                    ")");

                var list = string.IsNullOrWhiteSpace(json)
                    ? null
                    : System.Text.Json.JsonSerializer.Deserialize<List<Dictionary<string, string>>>(json);

                _logger.LogInformation("GetAreasLive zona={Zona} devolvió {Count} áreas", cveZona, list?.Count ?? 0);

                if (list?.Count > 1)
                {
                    _areasCache[cveZona] = list; // también sirve como caché para la próxima vez
                    return list;
                }
                return [new() { ["value"] = "00000", ["label"] = "Todas las áreas" }];
            }
            finally
            {
                await CloseBrowserSafeAsync(pw, ctx);
            }
        }

        // ══════════════════════════════════════════════════════════════════════════════
        // COLONIAS — detalle de solicitudes agrupado por colonia
        // ══════════════════════════════════════════════════════════════════════════════

        private const string ColoniasReportUrl =
            "https://cssnal.cfe.mx/iessInformesV2/statusAtencion.asp";

        /// <summary>
        /// Scrapea el reporte "Análisis Integral de Atención de Solicitudes de Servicio"
        /// del portal CFE, agrupado siempre por Colonia (el usuario elige Zona/Área/fechas,
        /// pero el agrupador es fijo para este reporte). Devuelve la tabla ya parseada
        /// (encabezados de dos niveles, vía ParseMultiHeaderTable).
        /// </summary>
        public async Task<List<Dictionary<string, string>>> GetColoniasReportAsync(
            string fechaDesde, string fechaHasta,
            string cveZona = "00000", string cveArea = "00000", string cveDivision = "DC000")
        {
            var (pw, ctx) = await CreateBrowserAsync(dirName: ColoniasPlaywrightDir);
            try
            {
                var page = ctx.Pages.FirstOrDefault() ?? await ctx.NewPageAsync();
                return await ScrapeColoniasPivotAsync(page, fechaDesde, fechaHasta, cveZona, cveArea, cveDivision);
            }
            finally
            {
                await CloseBrowserSafeAsync(pw, ctx);
            }
        }

        /// <summary>
        /// Igual que GetColoniasReportAsync (agrupado por colonia), pero filtrado a UN solo
        /// tipo de inconformidad (ej. "E03") — tabla completa con todas sus columnas (Rechazadas/
        /// Canceladas/Terminadas/Pendientes/etc.), no solo "Recibidas" como en el resumen del
        /// modal. Usado por el detalle en vivo que se abre al hacer clic en una inconformidad
        /// dentro del modal de Colonias.
        /// </summary>
        public async Task<List<Dictionary<string, string>>> GetColoniasPorInconformidadAsync(
            string fechaDesde, string fechaHasta, string cveZona, string cveArea, string cveDivision,
            string tipoSolTermino)
        {
            var (pw, ctx) = await CreateBrowserAsync(dirName: ColoniasPlaywrightDir);
            try
            {
                var page = ctx.Pages.FirstOrDefault() ?? await ctx.NewPageAsync();
                return await ScrapeColoniasPivotAsync(page, fechaDesde, fechaHasta, cveZona, cveArea, cveDivision, tipoSolTermino);
            }
            finally
            {
                await CloseBrowserSafeAsync(pw, ctx);
            }
        }

        /// <summary>
        /// Núcleo del reporte de colonias (statusAtencion.asp), reutilizable con una página ya
        /// abierta. Agrupa siempre por Colonia (cvePivote='cveColonia'); <paramref name="tipoSolTermino"/>
        /// filtra por tipo de orden/inconformidad ('T' = Todos, por defecto). Confirmado por DevTools
        /// que este filtro es independiente del pivote — permite pedir, para una consulta ya agrupada
        /// por colonia, solo las solicitudes de un código específico (ej. E02), sin poder filtrar a
        /// una colonia individual (cveColonia solo tiene la opción "Todas").
        /// </summary>
        private async Task<List<Dictionary<string, string>>> ScrapeColoniasPivotAsync(
            IPage page, string fechaDesde, string fechaHasta,
            string cveZona, string cveArea, string cveDivision, string tipoSolTermino = "T")
        {
            _logger.LogInformation("GetColoniasReport {Desde} -> {Hasta} zona={Zona} area={Area} tipoSolTermino={Tipo}", fechaDesde, fechaHasta, cveZona, cveArea, tipoSolTermino);

            if (!await NavigateWithRetryAsync(page, ColoniasReportUrl))
                throw new InvalidOperationException($"No se pudo cargar {ColoniasReportUrl}");

            try
            {
                await page.WaitForSelectorAsync(Sel.FechaDesde, new() { Timeout = 30_000 });
            }
            catch (Exception ex)
            {
                await DumpDebugHtmlAsync(page, "colonias_initial");
                throw new InvalidOperationException(
                    "La página de colonias no cargó el formulario (sesión expirada o error de red). Revisa el volcado de diagnóstico.", ex);
            }

            // Igual que en causas: esperamos a que se asiente el AJAX inicial de combos
            // antes de tocar cualquier <select>, para no perder la selección.
            try
            {
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 15_000 });
            }
            catch (Exception ex)
            {
                _logger.LogWarning("NetworkIdle (carga inicial de combos) timeout en colonias: {Err}", ex.Message);
            }
            await page.WaitForTimeoutAsync(600);

            // Cachea zonas (por división) y áreas (por zona), en ese orden (área anida bajo zona).
            await TryCacheZonasAsync(page, cveDivision);
            await TryCacheAreasAsync(page, cveZona);

            // Confirmado por DevTools: esta página usa fechas con GUION (yyyy-MM-dd),
            // a diferencia de causas/IMU que usan diagonal (yyyy/MM/dd).
            var fechaDesdeGuion = fechaDesde.Replace("/", "-");
            var fechaHastaGuion = fechaHasta.Replace("/", "-");

            var formState = await page.EvaluateAsync<Dictionary<string, string>>(
                "([d, h, z, a, ag, ts]) => {" +
                "  var setField = function(name, val) {" +
                "    var el = document.querySelector(\"select[name='\" + name + \"']\");" +
                "    if (el) el.value = val;" +
                "  };" +
                "  var injectAndSet = function(name, val) {" +
                "    var el = document.querySelector(\"select[name='\" + name + \"']\");" +
                "    if (!el) return 'NO_SELECT';" +
                "    var existe = Array.prototype.some.call(el.options, function(o){ return o.value === val; });" +
                "    if (!existe) { var opt = document.createElement('option'); opt.value = val; opt.text = val; el.add(opt); }" +
                "    el.value = val;" +
                "    return el.value;" +
                "  };" +
                "  var checkOn = function(name) {" +
                "    var el = document.querySelector(\"input[name='\" + name + \"']\");" +
                "    if (el) el.checked = true;" +
                "  };" +
                "  var fd = document.querySelector(\"input[name='fechaDesde']\");" +
                "  var fh = document.querySelector(\"input[name='fechaHasta']\");" +
                "  if (fd) fd.value = d;" +
                "  if (fh) fh.value = h;" +
                "  setField('cveDivision',       '" + cveDivision + "');" +
                "  var zonaSet = injectAndSet('cveZona', z);" +
                "  var areaSet = injectAndSet('cveArea', a);" +
                "  setField('entidadFederativa', '0');" +
                "  setField('cveMunicipio',      'T');" +
                "  setField('cveColonia',        'T');" +
                "  setField('tipoSolTermino',    ts);" +
                "  var agSet = injectAndSet('cvePivote', ag);" +
                "  checkOn('fechaResueltas');" +
                "  checkOn('fechaPendientes');" +
                "  return { zonaSet: zonaSet, areaSet: areaSet, pivoteSet: agSet };" +
                "}",
                new[] { fechaDesdeGuion, fechaHastaGuion, cveZona, cveArea, AgruparColoniaValue, tipoSolTermino });

            _logger.LogInformation("Colonias form state before submit: {@State}", formState);

            await page.ClickAsync(Sel.Submit);
            try
            {
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 25_000 });
            }
            catch (Exception ex)
            {
                _logger.LogWarning("NetworkIdle after submit timed out en colonias: {Err}", ex.Message);
            }
            await page.WaitForTimeoutAsync(1500);

            var html = await page.ContentAsync();
            var doc  = new HtmlDocument();
            doc.LoadHtml(html);

            var table = doc.DocumentNode.SelectSingleNode("//table[@id='mytable']");
            if (table == null)
            {
                _logger.LogWarning("No se encontró la tabla #mytable en el reporte de colonias");
                await DumpDebugHtmlAsync(page, "colonias_no_table");
                return [];
            }

            var rows = ParseColoniasTable(table);
            _logger.LogInformation("GetColoniasReport devolvió {Count} filas", rows.Count);
            return rows;
        }

        // Tope de páginas simultáneas para el desglose por colonia (16 códigos). Todas comparten
        // el MISMO navegador/contexto persistente (mismas cookies/sesión) — no se abren navegadores
        // nuevos, solo pestañas adicionales dentro de uno solo — así que el costo extra de RAM/CPU
        // es acotado y mucho menor que el de los Chromium huérfanos que vimos antes.
        private const int ColoniasScrapeConcurrency = 4;

        /// <summary>
        /// Desglose por tipo de inconformidad (E02, E03, ...) agrupado por colonia. Como el portal
        /// no permite filtrar a UNA colonia (cveColonia solo tiene "Todas"), se repite el reporte de
        /// colonias una vez por código (filtrando tipoSolTermino) y se combinan los resultados por
        /// Clave de colonia.
        /// El primer código corre solo, en serie: TryCacheZonasAsync/TryCacheAreasAsync usan banderas
        /// de instancia ("una vez por request") para decidir si repueblan el caché estático de
        /// zonas/áreas — si dos páginas paralelas las dispararan a la vez, ambas verían la bandera en
        /// false y escribirían al mismo Dictionary no thread-safe al mismo tiempo. Corriendo el primer
        /// código antes de lanzar el resto, la bandera ya queda en true para cuando arranca el lote
        /// paralelo, así que esas llamadas regresan de inmediato sin tocar el caché compartido.
        /// Los códigos 2..N sí corren en paralelo (hasta <see cref="ColoniasScrapeConcurrency"/> a la
        /// vez, cada uno en su propia pestaña) — recorta el tiempo total de "varios minutos" a una
        /// fracción, sin abrir navegadores adicionales.
        /// </summary>
        public async Task<List<Dictionary<string, string>>> GetColoniaInconformidadesAsync(
            string fechaDesde, string fechaHasta,
            string cveZona, string cveArea, string cveDivision,
            IEnumerable<string> codes)
        {
            var merged    = new Dictionary<string, Dictionary<string, string>>();
            var mergeLock = new object();

            // Además de Recibidas (item[code], igual que antes — no se toca para no romper a
            // quien ya lo consume así), ahora también se guardan Rechazadas/Pendientes/Cumplidas
            // por código, con el mismo criterio de coincidencia difusa que usa el frontend
            // (LEAF_DEFS en colonias.component.ts) para que ambos lados queden alineados. Esto
            // habilita el filtro por métrica dentro del modal sin tener que volver a scrapear
            // nada — ScrapeColoniasPivotAsync ya traía todas estas columnas, antes solo se
            // aprovechaba "Recibidas" y se descartaba el resto.
            void MergeRows(List<Dictionary<string, string>> rows, string code)
            {
                foreach (var row in rows)
                {
                    var clave = row.FirstOrDefault(kv => kv.Key.Contains("CLAVE", StringComparison.OrdinalIgnoreCase)).Value ?? "";
                    var desc  = row.FirstOrDefault(kv => kv.Key.Contains("DESCRIP", StringComparison.OrdinalIgnoreCase)).Value ?? "";
                    if (clave.Trim().Equals("TOTAL", StringComparison.OrdinalIgnoreCase)) continue; // salta el tfoot

                    var recibidas  = row.FirstOrDefault(kv => kv.Key.Contains("RECIBIDA", StringComparison.OrdinalIgnoreCase)).Value ?? "0";
                    var rechazadas = row.FirstOrDefault(kv => kv.Key.Contains("RECHAZADA", StringComparison.OrdinalIgnoreCase) && kv.Key.Contains("TOTAL", StringComparison.OrdinalIgnoreCase)).Value ?? "0";
                    var pendientes = row.FirstOrDefault(kv => kv.Key.Contains("PENDIENTE", StringComparison.OrdinalIgnoreCase) && kv.Key.Contains("TOTAL", StringComparison.OrdinalIgnoreCase)).Value ?? "0";
                    var cumplidas  = row.FirstOrDefault(kv => kv.Key.Contains("TERMINADA", StringComparison.OrdinalIgnoreCase)
                                                            && kv.Key.Contains("CUMPLID", StringComparison.OrdinalIgnoreCase)
                                                            && !kv.Key.Contains("NO CUMPLID", StringComparison.OrdinalIgnoreCase)).Value ?? "0";

                    lock (mergeLock)
                    {
                        if (!merged.TryGetValue(clave, out var item))
                            item = merged[clave] = new Dictionary<string, string> { ["Clave"] = clave, ["Descripcion"] = desc };
                        item[code] = recibidas;
                        item[$"{code}_RECHAZADAS"] = rechazadas;
                        item[$"{code}_PENDIENTES"] = pendientes;
                        item[$"{code}_CUMPLIDAS"]  = cumplidas;
                    }
                }
            }

            var (pw, ctx) = await CreateBrowserAsync(dirName: ColoniasPlaywrightDir);
            try
            {
                var codesList = codes.ToList();
                if (codesList.Count == 0) return [];

                var page = ctx.Pages.FirstOrDefault() ?? await ctx.NewPageAsync();

                var firstCode = codesList[0];
                try
                {
                    var firstRows = await ScrapeColoniasPivotAsync(page, fechaDesde, fechaHasta, cveZona, cveArea, cveDivision, firstCode);
                    MergeRows(firstRows, firstCode);
                }
                catch (CfePortalUnreachableException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("GetColoniaInconformidades code {Code} failed: {Err}", firstCode, ex.Message);
                }

                var restCodes = codesList.Skip(1).ToList();
                if (restCodes.Count > 0)
                {
                    using var throttle = new SemaphoreSlim(ColoniasScrapeConcurrency);
                    var tasks = restCodes.Select(async code =>
                    {
                        await throttle.WaitAsync();
                        IPage? codePage = null;
                        try
                        {
                            codePage = await ctx.NewPageAsync();
                            var rows = await ScrapeColoniasPivotAsync(codePage, fechaDesde, fechaHasta, cveZona, cveArea, cveDivision, code);
                            MergeRows(rows, code);
                        }
                        catch (CfePortalUnreachableException)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning("GetColoniaInconformidades code {Code} failed: {Err}", code, ex.Message);
                        }
                        finally
                        {
                            if (codePage != null) await codePage.CloseAsync();
                            throttle.Release();
                        }
                    });

                    await Task.WhenAll(tasks);
                }
            }
            finally
            {
                await CloseBrowserSafeAsync(pw, ctx);
            }

            return merged.Values.ToList();
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
