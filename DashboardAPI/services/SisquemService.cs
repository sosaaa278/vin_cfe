using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using DashboardAPI.Models;

namespace DashboardAPI.Services
{
    /// <summary>
    /// Consume el reporte "Quejas y Emergencias" del sistema sisquem
    /// (http://10.4.17.45/sisquem/modulos/reportes/). A diferencia de los demás scrapers
    /// del proyecto, este sistema es público (sin login) y sus dos endpoints AJAX
    /// responden JSON directo a un POST normal — no hace falta Playwright, un HttpClient
    /// (con cookies de sesión) basta.
    /// </summary>
    public class SisquemService
    {
        // sisquem.php es solo la página que carga el formulario — confirmado leyendo el JS
        // real del portal (volcado de diagnóstico): el botón "Actualizar" en realidad llama
        // a dos endpoints AJAX distintos, cada uno con su propio subconjunto de parámetros:
        //   - GetAtend  → JSON (objeto) con las series de las gráficas + integridad.
        //   - Tablas    → JSON (ARREGLO de 4 strings HTML): [0]=resumen+detalle por zona,
        //                 [1]=listado grande, [2]/[3]=resumen por estado/municipio (no
        //                 consumido todavía, ver TODO más abajo).
        private const string BaseUrl     = "http://10.4.17.45/sisquem/modulos/reportes/";
        private const string PageUrl     = BaseUrl + "sisquem.php";
        private const string GraficasUrl = BaseUrl + "reportes_sicoss_pendientes_getAtend.php";
        private const string TablasUrl   = BaseUrl + "reportes_sicoss_pendientes_tabla_v3.php";

        // Los 16 códigos de "Tipos de orden" que expone el portal — se usan como default
        // cuando el usuario no filtra (equivalente a "todos seleccionados", que es el
        // estado por defecto real del multi-select del portal).
        private static readonly string[] TodosLosTipos =
            ["E01", "E02", "E03", "E04", "E05", "E06", "E07",
             "Q01", "Q02", "Q03", "Q04", "Q06", "Q07", "Q08", "QC2", "QC7"];

        private readonly IHttpClientFactory _httpFactory;
        private readonly ILogger<SisquemService> _logger;

        public SisquemService(IHttpClientFactory httpFactory, ILogger<SisquemService> logger)
        {
            _httpFactory = httpFactory;
            _logger = logger;
        }

        public async Task<QuejasEmergenciasResult> GetReporteAsync(
            string fechaDesde, string fechaHasta, string cveDivisionCorta,
            string[]? zonas, string[]? tiposOrden)
        {
            _logger.LogInformation(
                "SisquemService {Desde} -> {Hasta} division={Div} zonas={Zonas} tipos={Tipos}",
                fechaDesde, fechaHasta, cveDivisionCorta,
                zonas is { Length: > 0 } ? string.Join(",", zonas) : "(todas)",
                tiposOrden is { Length: > 0 } ? string.Join(",", tiposOrden) : "(todos)");

            var camposComunes = new List<KeyValuePair<string, string>>
            {
                new("fecha", fechaDesde),
                new("fechafin", fechaHasta),
                new("selectdivision", cveDivisionCorta),
                new("tablas_detalle", "true"),
                new("reporte_ejecutivo", "false"),
                new("conv_q07_e", "true"),
                new("conv_qc2_q", "false"),
                new("conv_qc7_q", "false"),
            };
            foreach (var t in tiposOrden is { Length: > 0 } ? tiposOrden : TodosLosTipos)
                camposComunes.Add(new("arreglotiposordenes[]", t));
            // "selectzona" (confirmado en el JS real) — se omite por completo cuando no hay
            // zonas seleccionadas, igual que hace el portal con "Zona(s): -Todas-".
            // OJO: sisquem usa un código de zona más CORTO que cssnal.cfe.mx — el resto de
            // la app usa códigos tipo "DC270" (con cero final, formato de AuthService /
            // cssnal), pero sisquem espera "DC27" (sin el cero). Confirmado comparando
            // contra el portal real: la zona "DC270 - Gomez Palacio" ahí aparece como
            // "DC27 - Gomez Palacio". Sin este ajuste, sisquem no encuentra la zona y
            // regresa eje_x válido pero todos los valores en cero (sin error visible).
            if (zonas is { Length: > 0 })
                foreach (var z in zonas) camposComunes.Add(new("selectzona[]", NormalizarZonaSisquem(z)));

            var graficasContent = new List<KeyValuePair<string, string>>(camposComunes)
            {
                new("check_listados_atendidas", "false"),
                new("req_atendidas", "true"),
                new("req_generadas", "true"),
            };
            var tablasContent = new List<KeyValuePair<string, string>>(camposComunes)
            {
                new("mostrar_resumen", "true"),
            };

            var client = _httpFactory.CreateClient();
            // El propio portal advierte "Recopilando datos de SICOSS. Puede tardar unos
            // minutos..." (visto en su JS, beforeSend del ajax a getAtend.php) — consulta
            // en vivo otro sistema interno, así que 100s (el default) o menos se queda corto.
            client.Timeout = TimeSpan.FromMinutes(5);

            // Intento best-effort de conseguir la cookie de sesión de sisquem.php — es una
            // hipótesis no confirmada (no sabemos si los endpoints AJAX realmente la
            // requieren), así que si tarda o falla NO debe tumbar el resto del reporte:
            // seguimos directo a los POST reales con un timeout corto propio.
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await client.GetAsync(PageUrl, cts.Token);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("SisquemService: no se pudo pre-cargar sisquem.php para la cookie de sesión (se continúa sin ella): {Err}", ex.Message);
            }

            string graficasJson, tablasJson;
            try
            {
                var graficasTask = PostAjaxAsync(client, GraficasUrl, graficasContent);
                var tablasTask   = PostAjaxAsync(client, TablasUrl, tablasContent);
                await Task.WhenAll(graficasTask, tablasTask);
                graficasJson = graficasTask.Result;
                tablasJson   = tablasTask.Result;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
            {
                throw new CfePortalUnreachableException(
                    "No se pudo conectar al sistema de Quejas y Emergencias (sisquem). " +
                    $"Verifica tu conexión/VPN a la red interna de CFE. Detalle: {ex.Message}");
            }

            // Volcado SIEMPRE (no solo cuando algo falla), a un nombre fijo que se
            // sobreescribe en cada consulta — así se puede leer directo el HTML/JSON crudo
            // de la última consulta real para diagnosticar discrepancias sin depender de
            // que el usuario copie el HTML a mano desde DevTools.
            await DumpDebugJsonAsync(graficasJson, "quejas_emergencias_LATEST_graficas", overwrite: true);
            await DumpDebugJsonAsync(tablasJson, "quejas_emergencias_LATEST_tablas", overwrite: true);

            JsonNode? graficasRoot;
            try { graficasRoot = JsonNode.Parse(graficasJson); }
            catch (Exception ex)
            {
                await DumpDebugJsonAsync(graficasJson, "quejas_emergencias_graficas_parse_error");
                throw new InvalidOperationException(
                    "La respuesta de getAtend.php no es JSON válido. Revisa el volcado de diagnóstico.", ex);
            }

            JsonNode? tablasRoot;
            try { tablasRoot = JsonNode.Parse(tablasJson); }
            catch (Exception ex)
            {
                await DumpDebugJsonAsync(tablasJson, "quejas_emergencias_tablas_parse_error");
                throw new InvalidOperationException(
                    "La respuesta de tabla_v3.php no es JSON válido. Revisa el volcado de diagnóstico.", ex);
            }

            var result = new QuejasEmergenciasResult();
            if (graficasRoot != null)
            {
                result.Graficas = ParseGraficas(graficasRoot);
                result.IntegridadOk = SafeBool(graficasRoot["integridad"]?["ok"], true);
            }

            // tabla_v3.php responde un ARREGLO (no un objeto): [0]=divResumen (resumen por
            // zona de Emergencias, resumen por zona de Quejas, detalle Emergencias, detalle
            // Quejas — las 4 tablas concatenadas en ese orden), [1]=divReporte (listado
            // grande), [2]=divResumen_e_edomun (Emergencias por Estado + por Municipio, 2
            // tablas concatenadas), [3]=divResumen_q_edomun (mismo par para Quejas).
            // "as" en vez de .AsArray(): si la respuesta no fuera exactamente un arreglo
            // (ej. un objeto de error del servidor), .AsArray() LANZA una excepción — con
            // "as" simplemente da null y caemos a listas vacías sin romper la respuesta.
            var tablasArray = tablasRoot as JsonArray;
            if (tablasArray == null)
            {
                _logger.LogWarning("SisquemService: la respuesta de tabla_v3.php no es un arreglo JSON — volcando para diagnóstico");
                await DumpDebugJsonAsync(tablasJson, "quejas_emergencias_tablas_no_es_arreglo");
            }
            string ArrayHtml(int i) => tablasArray != null && tablasArray.Count > i ? (tablasArray[i]?.GetValue<string>() ?? "") : "";
            var divResumenHtml     = ArrayHtml(0);
            var divReporteHtml     = ArrayHtml(1);
            var divResumenEEdomun  = ArrayHtml(2);
            var divResumenQEdomun  = ArrayHtml(3);

            var tablasEnResumen = ParseAllFlatTables(divResumenHtml);
            result.ResumenEmergencias = tablasEnResumen.Count > 0 ? tablasEnResumen[0] : new();
            result.ResumenQuejas      = tablasEnResumen.Count > 1 ? tablasEnResumen[1] : new();
            result.DetalleEmergencias = tablasEnResumen.Count > 2 ? tablasEnResumen[2] : new();
            result.DetalleQuejas      = tablasEnResumen.Count > 3 ? tablasEnResumen[3] : new();
            result.Listado            = ParseListadoTable(divReporteHtml);
            result.ResumenGlobal      = ParseResumenGlobal(divResumenHtml);

            var tablasEEdomun = ParseAllFlatTables(divResumenEEdomun);
            result.ResumenEstadoEmergencias    = tablasEEdomun.Count > 0 ? tablasEEdomun[0] : new();
            result.ResumenMunicipioEmergencias = tablasEEdomun.Count > 1 ? tablasEEdomun[1] : new();

            var tablasQEdomun = ParseAllFlatTables(divResumenQEdomun);
            result.ResumenEstadoQuejas    = tablasQEdomun.Count > 0 ? tablasQEdomun[0] : new();
            result.ResumenMunicipioQuejas = tablasQEdomun.Count > 1 ? tablasQEdomun[1] : new();

            if (tablasEnResumen.Count == 0 && result.Listado.Count == 0)
            {
                _logger.LogWarning("SisquemService: tabla_v3.php no devolvió tablas reconocibles — volcando para diagnóstico");
                await DumpDebugJsonAsync(tablasJson, "quejas_emergencias_no_tablas");
            }

            return result;
        }

        /// <summary>
        /// Los códigos de zona del resto de la app (AuthService, para cssnal.cfe.mx) usan 5
        /// caracteres con un cero final (ej. "DC270"); sisquem espera el mismo código SIN
        /// ese cero (ej. "DC27"). Se recorta solo si el patrón coincide exactamente, para no
        /// romper otros formatos si algún día cambian.
        /// </summary>
        private static string NormalizarZonaSisquem(string zona) =>
            zona.Length == 5 && zona.EndsWith('0') ? zona[..4] : zona;

        private static async Task<string> PostAjaxAsync(HttpClient client, string url, IEnumerable<KeyValuePair<string, string>> content)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new FormUrlEncodedContent(content)
            };
            // El navegador manda esta cabecera en la petición AJAX real (jQuery la agrega
            // automático) — sin ella, el servidor puede devolver la página HTML completa
            // en vez del JSON esperado.
            request.Headers.Add("X-Requested-With", "XMLHttpRequest");
            request.Headers.Add("Accept", "application/json, text/javascript, */*; q=0.01");

            var resp = await client.SendAsync(request);
            resp.EnsureSuccessStatusCode();
            return await resp.Content.ReadAsStringAsync();
        }

        private static GraficasQuejasEmergencias ParseGraficas(JsonNode root)
        {
            // "as JsonArray" en vez de .AsArray(): si la clave existiera pero no fuera un
            // arreglo, .AsArray() lanzaría una excepción — con "as" cae a lista vacía.
            List<string> Strings(string key) =>
                (root[key] as JsonArray)?.Select(n => n?.GetValue<string>() ?? "").ToList() ?? new();
            List<double> Numbers(string key) =>
                (root[key] as JsonArray)?.Select(n => n?.GetValue<double>() ?? 0).ToList() ?? new();

            return new GraficasQuejasEmergencias
            {
                EjeX                  = Strings("eje_x"),
                EmergenciasPendientes = Numbers("valores_emergencias_pendientes"),
                QuejasPendientes      = Numbers("valores_quejas_pendientes"),
                EmergenciasAtendidas  = Numbers("valores_emergencias_atendidas"),
                QuejasAtendidas       = Numbers("valores_quejas_atendidas"),
                EmergenciasGeneradas  = Numbers("valores_emergencias_generadas"),
                QuejasGeneradas       = Numbers("valores_quejas_generadas"),
            };
        }

        // ══════════════════════════════════════════════════════════════════════════════
        // PARSERS HTML — más simples que los de WebScraperService porque ninguna de estas
        // 3 tablas tiene colspan/rowspan en el encabezado.
        // ══════════════════════════════════════════════════════════════════════════════

        /// <summary>Decodifica entidades HTML y normaliza espacios duros (non-breaking) — mismo criterio que WebScraperService.Normalize.</summary>
        private static string DecodeText(string s) =>
            System.Net.WebUtility.HtmlDecode(s).Replace(' ', ' ').Trim();

        /// <summary>
        /// tabla_v3.php devuelve varias tablas simples concatenadas en un mismo string HTML
        /// (resumen por zona de Emergencias, resumen por zona de Quejas, detalle Emergencias,
        /// detalle Quejas, en ese orden) — esta función las extrae TODAS, en orden de
        /// aparición. Cada tabla se parsea igual (header plano de una fila, sin colspan ni
        /// rowspan) y cada celda captura su "&lt;col&gt;_color" si trae background-color
        /// inline (las tablas de detalle lo usan para las alertas de tiempo del portal; las
        /// de resumen simplemente no generan esas claves porque no tienen ese estilo).
        /// </summary>
        private static List<List<Dictionary<string, string>>> ParseAllFlatTables(string html)
        {
            var result = new List<List<Dictionary<string, string>>>();
            if (string.IsNullOrWhiteSpace(html)) return result;

            var doc = new HtmlDocument();
            doc.LoadHtml(html);
            var tables = doc.DocumentNode.SelectNodes("//table");
            if (tables == null) return result;

            foreach (var table in tables)
                result.Add(ParseFlatTableNode(table));
            return result;
        }

        private static List<Dictionary<string, string>> ParseFlatTableNode(HtmlNode table)
        {
            var rows = new List<Dictionary<string, string>>();

            var headers = table.SelectNodes(".//thead//td|.//thead//th")
                ?.Select(c => DecodeText(c.InnerText)).ToList() ?? new List<string>();
            if (headers.Count == 0) return rows;

            var bodyRows = table.SelectNodes(".//tbody/tr");
            if (bodyRows != null)
            {
                foreach (var tr in bodyRows)
                {
                    var item = MapRowCells(tr.SelectNodes("./td"), headers);
                    if (item.Count > 0) rows.Add(item);
                }
            }

            // El <tfoot> con la fila Total (todas las tablas resumen/detalle lo traen) se
            // agrega como fila extra al final — el usuario la quiere ver siempre, no solo
            // cuando el cuerpo viene vacío. La tabla de detalle fusiona Clave+Zona con
            // colspan='2' en esa celda ("Total"); MapRowCells respeta el colspan real de
            // cada celda para no desalinear las columnas siguientes (En tiempo, Vencidas...).
            var footCells = table.SelectSingleNode(".//tfoot/tr")?.SelectNodes("./td");
            var footItem = MapRowCells(footCells, headers);
            if (footItem.Count > 0) rows.Add(footItem);

            return rows;
        }

        /// <summary>
        /// Mapea las celdas de una fila (&lt;tr&gt;) a los encabezados por posición, respetando
        /// el atributo colspan de cada celda — sin esto, una celda que fusiona varias columnas
        /// (ej. "Total" cubriendo Clave+Zona en el tfoot de la tabla de detalle) desalinearía
        /// todas las columnas siguientes de esa fila. El texto de una celda fusionada se
        /// escribe solo en la PRIMERA columna que cubre (las demás quedan vacías) para no
        /// duplicar el mismo valor en varios encabezados.
        /// </summary>
        private static Dictionary<string, string> MapRowCells(HtmlNodeCollection? cells, List<string> headers)
        {
            var item = new Dictionary<string, string>();
            if (cells == null) return item;

            var headerIdx = 0;
            foreach (var cell in cells)
            {
                if (headerIdx >= headers.Count) break;

                var colspanAttr = cell.GetAttributeValue("colspan", "1");
                if (!int.TryParse(colspanAttr, out var colspan) || colspan < 1) colspan = 1;

                var text = DecodeText(cell.InnerText);

                string? color = null;
                var style = cell.GetAttributeValue("style", "");
                var colorIdx = style.IndexOf("background-color:", StringComparison.OrdinalIgnoreCase);
                if (colorIdx >= 0)
                {
                    var rest = style[(colorIdx + "background-color:".Length)..];
                    var semi = rest.IndexOf(';');
                    color = (semi >= 0 ? rest[..semi] : rest).Trim();
                }

                for (var i = 0; i < colspan && headerIdx < headers.Count; i++, headerIdx++)
                {
                    var col = headers[headerIdx];
                    item[col] = i == 0 ? text : "";
                    if (i == 0 && color != null) item[$"{col}_color"] = color;
                }
            }
            return item;
        }

        /// <summary>
        /// El bloque de texto libre al final de divResumenHtml ("Al momento se cuenta con:
        /// Emergencias: N pendientes, N vencidas y N en atención. Quejas: ..."), el único
        /// lugar donde sisquem ya trae Vencidas/En atención TOTALIZADOS sin colspan que
        /// desalinee celdas (a diferencia del tfoot de la tabla de detalle). Se busca el
        /// &lt;div&gt; por su texto (no tiene id/clase propia) y se leen los 6 números con
        /// regex sobre el texto ya decodificado (sin las etiquetas &lt;b&gt;).
        /// </summary>
        private static readonly Regex ResumenGlobalEmergenciasRegex = new(
            @"Emergencias:\s*(\d+)\s*pendientes,\s*(\d+)\s*vencidas y\s*(\d+)\s*en atenci[oó]n",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex ResumenGlobalQuejasRegex = new(
            @"Quejas:\s*(\d+)\s*pendientes,\s*(\d+)\s*vencidas y\s*(\d+)\s*en atenci[oó]n",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static ResumenGlobalQuejasEmergencias ParseResumenGlobal(string html)
        {
            var result = new ResumenGlobalQuejasEmergencias();
            if (string.IsNullOrWhiteSpace(html)) return result;

            var doc = new HtmlDocument();
            doc.LoadHtml(html);
            var div = doc.DocumentNode.SelectNodes("//div")
                ?.FirstOrDefault(d => DecodeText(d.InnerText)
                    .StartsWith("Al momento se cuenta con", StringComparison.OrdinalIgnoreCase));
            if (div == null) return result;

            var text = DecodeText(div.InnerText);

            var mE = ResumenGlobalEmergenciasRegex.Match(text);
            if (mE.Success)
            {
                result.EmergenciasPendientes = int.Parse(mE.Groups[1].Value);
                result.EmergenciasVencidas   = int.Parse(mE.Groups[2].Value);
                result.EmergenciasEnAtencion = int.Parse(mE.Groups[3].Value);
            }

            var mQ = ResumenGlobalQuejasRegex.Match(text);
            if (mQ.Success)
            {
                result.QuejasPendientes = int.Parse(mQ.Groups[1].Value);
                result.QuejasVencidas   = int.Parse(mQ.Groups[2].Value);
                result.QuejasEnAtencion = int.Parse(mQ.Groups[3].Value);
            }

            return result;
        }

        /// <summary>
        /// Tabla grande "Listado de solicitudes pendientes" (id="tablatopten", plugin jQuery
        /// tablesorter) — TODAS las filas ya vienen en el DOM (paginación/orden/filtro son
        /// 100% client-side en el portal), así que solo hace falta leer la tabla completa.
        /// El encabezado real está dentro de &lt;div class="tablesorter-header-inner"&gt;,
        /// no en el texto plano del &lt;td&gt; del thead (que además incluiría la fila de
        /// filtros si no se excluyera explícitamente).
        /// </summary>
        private static List<Dictionary<string, string>> ParseListadoTable(string html)
        {
            var rows = new List<Dictionary<string, string>>();
            if (string.IsNullOrWhiteSpace(html)) return rows;

            var doc = new HtmlDocument();
            doc.LoadHtml(html);
            var table = doc.DocumentNode.SelectSingleNode("//table");
            if (table == null) return rows;

            var headerRow = table.SelectSingleNode(".//thead/tr[not(contains(@class,'tablesorter-filter-row'))]");
            if (headerRow == null) return rows;

            var rawHeaders = headerRow.SelectNodes("./td")
                ?.Select(td =>
                {
                    var inner = td.SelectSingleNode(".//div[contains(@class,'tablesorter-header-inner')]");
                    return DecodeText(inner != null ? inner.InnerText : td.InnerText);
                })
                .ToList() ?? new List<string>();
            if (rawHeaders.Count == 0) return rows;

            // Esta tabla trae DOS columnas llamadas "Tipo" (código de orden E01-E07/Q01-Q08,
            // y por separado Rural/Urbana) — sin desduplicar, la segunda sobreescribía
            // silenciosamente a la primera en el diccionario y el código de tipo de orden se
            // perdía. La 2a columna "Tipo" es siempre Rural/Urbana (orden fijo confirmado
            // contra el HTML real), así que la renombramos con un nombre claro en vez de
            // usar el sufijo genérico "(2)" que sí usan ParseMultiHeaderTable/ParseColoniasTable.
            var headers = new List<string>();
            var seen = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var name in rawHeaders)
            {
                var finalName = name;
                if (seen.TryGetValue(name, out var n))
                {
                    seen[name] = n + 1;
                    finalName = name == "Tipo" && n == 1 ? "Rural/Urbana" : $"{name} ({n + 1})";
                }
                else seen[name] = 1;
                headers.Add(finalName);
            }

            var bodyRows = table.SelectNodes(".//tbody/tr");
            if (bodyRows == null) return rows;

            foreach (var tr in bodyRows)
            {
                var cells = tr.SelectNodes("./td");
                if (cells == null) continue;
                var item = new Dictionary<string, string>();
                for (int i = 0; i < cells.Count && i < headers.Count; i++)
                    item[headers[i]] = DecodeText(cells[i].InnerText);
                if (item.Count > 0) rows.Add(item);
            }
            return rows;
        }

        /// <summary>Lee un bool de forma segura — GetValue&lt;bool&gt;() lanza excepción si el nodo existe pero no es booleano (ej. viene como string "true").</summary>
        private static bool SafeBool(JsonNode? node, bool fallback) =>
            node is JsonValue v && v.TryGetValue<bool>(out var b) ? b : fallback;

        /// <summary>
        /// Guarda el JSON/HTML crudo de la respuesta para diagnóstico — análogo a
        /// WebScraperService.DumpDebugHtmlAsync pero para JSON. Con overwrite=true usa un
        /// nombre FIJO (sin timestamp) que se sobreescribe en cada llamada — para poder leer
        /// directo la respuesta cruda de la consulta MÁS RECIENTE sin tener que buscar el
        /// archivo más nuevo entre varios, y sin depender de que el usuario copie HTML a mano.
        /// </summary>
        private async Task DumpDebugJsonAsync(string json, string tag, bool overwrite = false)
        {
            try
            {
                var dir = Path.Combine(Directory.GetCurrentDirectory(), WebScraperService.DebugDumpDir);
                Directory.CreateDirectory(dir);
                var fileName = overwrite ? $"debug_{tag}.json" : $"debug_{tag}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.json";
                var path = Path.Combine(dir, fileName);
                await File.WriteAllTextAsync(path, json);
                if (!overwrite)
                    _logger.LogWarning("Volcado de diagnóstico (JSON) guardado en {Path}", path);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("No se pudo guardar el volcado de diagnóstico JSON para {Tag}: {Err}", tag, ex.Message);
            }
        }
    }
}
