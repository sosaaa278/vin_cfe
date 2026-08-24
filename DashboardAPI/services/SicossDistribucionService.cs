using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using Microsoft.EntityFrameworkCore;
using DashboardAPI.Data;
using DashboardAPI.Helpers;

namespace DashboardAPI.Services
{
    /// <summary>
    /// Consume "SICOSS Distribución — Solicitudes pendientes de atención"
    /// (http://10.4.14.1/cgi-bin/sicossweb/solicitudes/pendctosa.cgi) — un tercer sistema
    /// CFE distinto a cssnal.cfe.mx y sisquem. Es un CGI clásico: GET con zona/cen, HTML
    /// en ISO-8859-1, sin JSON y sin filtro de fechas (siempre es el estado "ahora mismo").
    /// El scrape del propio SICOSS es HttpClient puro (sin Playwright); solo recurre a
    /// WebScraperService (Playwright) como respaldo puntual para mapear nombres de colonia
    /// cuando el apartado Colonias nunca se ha consultado para esa zona — ver GetPendientesAsync.
    /// </summary>
    public class SicossDistribucionService
    {
        private const string Url = "http://10.4.14.1/cgi-bin/sicossweb/solicitudes/pendctosa.cgi";

        // Página de detalle de UNA solicitud (el enlace onclick=AbrirVentana(...) de cada fila
        // de pendctosa.cgi apunta aquí, con el folio como query string sin nombre de parámetro:
        // .../consulta.cgi?C2206407416). Confirmado con HTML real del portal.
        private const string DetalleUrl = "http://10.4.14.1/cgi-bin/sicossweb/consulta/consulta.cgi";

        private readonly IHttpClientFactory _httpFactory;
        private readonly AppDbContext _db;
        private readonly WebScraperService _scraper;
        private readonly ReporteStore _store;
        private readonly ILogger<SicossDistribucionService> _logger;

        public SicossDistribucionService(
            IHttpClientFactory httpFactory, AppDbContext db, WebScraperService scraper, ReporteStore store,
            ILogger<SicossDistribucionService> logger)
        {
            _httpFactory = httpFactory;
            _db = db;
            _scraper = scraper;
            _store = store;
            _logger = logger;
        }

        // ── Catálogo Zona → Centros ──────────────────────────────────────────────────
        // Tomado literal del HTML real del portal (confirmado por el usuario, dos capturas
        // completas) — son los <option> de los <select name=zona> y de los <textarea
        // id='txtCXX'> ocultos que el JS del portal copia al <select name=cen> según la
        // zona elegida. Es un catálogo organizacional estable (no depende del usuario ni
        // de su división), así que se hardcodea en vez de volver a scrapear los textareas
        // en cada consulta — evita una petición extra y un parser frágil para algo que
        // casi no cambia.
        private static readonly (string Value, string Label)[] ZonasCatalogo =
        [
            ("C01", "CHIHUAHUA 010"),
            ("C02", "CUAUHTEMOC 020"),
            ("C04", "JUAREZ 040"),
            ("C06", "DELICIAS 060"),
            ("C14", "CASAS GRANDES 140"),
            ("C22", "TORREON 220"),
            ("C24", "PARRAL 240"),
            ("C26", "DURANGO 260"),
            ("C27", "GOMEZ PALACIO 270"),
        ];

        private static readonly Dictionary<string, (string Value, string Label)[]> CentrosCatalogo = new()
        {
            ["C01"] =
            [
                ("2A", "SUCURSAL CHIHUAHUA"), ("2B", "SUCURSAL 20 NOVIEMBRE"), ("2C", "AGENCIA GRAL TRIAS"),
                ("2D", "AGENCIA ALDAMA"), ("2E", "AGENCIA EL SAUZ"), ("2F", "SUCURSAL INDUSTRIAS"),
                ("2G", "SUCURSAL TECNOLOGICO"), ("2P", "AREA OJINAGA"), ("4A", "SB AG CHIHUAHUA"),
                ("4B", "SB AG 20 N0VIEMBRE"), ("4C", "SB AG GRAL TRIAS"), ("4D", "SB AG ALDAMA"),
                ("4E", "SB AG EL SAUZ"), ("4F", "SB AG INDUSTRIAS"), ("4G", "SB AG TECNOGOLICO"),
                ("AN", "DIST AREA NORTE"), ("AS", "DIST AREA SUR"), ("CH", "ZONA CHIHUAHUA"),
                ("CS", "CENT SERV CLIENTE"), ("DC", "DEPTO COMERCIAL"), ("DM", "DEPTO MEDICION"),
                ("DP", "DEPTO PLANEACION"), ("OC", "TODOS LOS CENTROS"),
            ],
            ["C02"] =
            [
                ("2A", "AGENCIA CUAUHTEMOC"), ("2B", "AG ALVARO OBREGON"), ("2C", "AGENCIA CREEL"),
                ("2E", "AGENCIA BAHUICHIVO"), ("2F", "AGENCIA BASASEACHI"), ("2G", "AGENCIA TEMORIS"),
                ("2J", "AGENCIA GUERRERO"), ("2L", "AGENCIA MADERA"), ("2M", "AGENCIA G FARIAS"),
                ("2N", "AGENCIA MOLINO"), ("2P", "AREA CREEL"), ("4A", "SB AG CUAUHTEMOC"),
                ("4B", "SB AG ALVARO OBREGO"), ("4C", "SB AG CREEL"), ("4E", "SB AG BAHUICHIVO"),
                ("4F", "SB AG BASASEACHI"), ("4G", "SB AG TEMORIS"), ("4J", "SB AG GUERRERO"),
                ("4L", "SB AG MADERA"), ("4M", "SB AG G FARIAS"), ("4N", "SB AG MOLINO"),
                ("CC", "CTRO DE CONTINUIDAD"), ("CU", "ZONA CUAUHTEMOC"), ("D1", "AREA GUERRERO"),
                ("D2", "AREA MOLINO"), ("D3", "DEPTO DISTRIBUCION"), ("D4", "AREA BASESEACHI"),
                ("DC", "DEPTO COMERIAL"), ("DM", "DEPTO DE MEDICION"), ("MC", "MEDICION CUAUHTEMOC"),
                ("OC", "TODOS LOS CENTROS"), ("PL", "DEPTO. PLANEACION"), ("PM", "PROCC AUT MEDICION"),
            ],
            ["C04"] =
            [
                ("4A", "SB AG REFORMA"), ("4B", "SB AG FUENTES"), ("4C", "SB AG GUADALUPE"),
                ("4E", "SB AG VILLA AHUMADA"), ("4H", "SB AG BELLAVISTA"), ("4J", "SB AG AEROPUERTO"),
                ("4K", "SB AG MIRADOR"), ("AA", "AGENCIA REFORMA"), ("AB", "AGENCIA FUENTES"),
                ("AC", "AGENCIA GUADALUPE"), ("AE", "AGENCIA VILLA AHUMAD"), ("AH", "AGENCIA BELLAVISTA"),
                ("AJ", "AGENCIA AEROPUERTO"), ("AK", "AGENCIA MIRADOR"), ("CC", "CENTRO DE CONTINUID"),
                ("CF", "CCC FUENTES"), ("CM", "CCC MIRADOR"), ("CS", "CSC"), ("CU", "CCC SUR"),
                ("CZ", "CCC ZARAGOZA"), ("DD", "DEPTO. DISTRIBUCION"), ("DE", "AREA MIRADOR"),
                ("DF", "AREA ZARAGOZA"), ("DM", "DEPTO. MEDICION"), ("GG", "AREA GUADALUPE"),
                ("OC", "TODOS LOS CENTROS"), ("OL", "OPERATIVO LIRE"), ("PL", "PLANEACION"),
                ("VV", "AREA VILLA AHUMADA"),
            ],
            ["C06"] =
            [
                ("4A", "SB AG DELICIAS"), ("4B", "SB AG MEOQUI"), ("4C", "SB AG SAUCILLO"),
                ("4E", "SB AG CAMARGO"), ("6A", "AGENCIA DELICIAS"), ("6B", "AGENCIA MEOQUI"),
                ("6C", "AGENCIA SAUCILLO"), ("6E", "AGENCIA CAMARGO"), ("AC", "AREA CAMARGO"),
                ("AD", "AREA DELICIAS"), ("CS", "CCC DELICIAS"), ("DD", "DISTRIB DELICIAS"),
                ("MD", "MEDICION DELICIAS"), ("OC", "TODOS LOS CENTROS"), ("PL", "PLANEACION DELICIAS"),
            ],
            ["C14"] =
            [
                ("2D", "AG BUENAVENTURA"), ("2G", "AG CASAS GRANDES"), ("2J", "AG ASCENSION"),
                ("4D", "SB AG BUENAVENTURA"), ("4G", "SB AG CASAS GRANDES"), ("4J", "SB AG ASCENSION"),
                ("AA", "AREA ASCENCION"), ("CG", "CCC CASAS GRANDES"), ("CZ", "DEPTO COMERCIAL"),
                ("DM", "DEPARTAMENTO DE MED"), ("DZ", "DEPTO DISTRIB"), ("MA", "MEDICION ASCENCION"),
                ("MZ", "MEDICION CASAS GRAND"), ("OC", "TODOS LOS CENTROS"), ("PL", "DEPTO. PLANEACION"),
            ],
            ["C22"] =
            [
                ("2A", "AGENCIA CENTRO"), ("2B", "AGENCIA ORIENTE"), ("2F", "AGENCIA ABASTOS"),
                ("2H", "AGENCIA SAULO"), ("2R", "AGENCIA REVOLUCION"), ("2S", "AGENCIA SENDEROS"),
                ("4A", "SB AG CENTRO"), ("4B", "SB AG MEXICO"), ("4C", "SB AREA PARRAS"),
                ("4D", "SB AREA SAN PEDRO"), ("4E", "SB AREA MATAMOROS"), ("4F", "SB AG ABASTOS"),
                ("4G", "SB AREA FCO I MADER"), ("4H", "SB AG SAULO"), ("4R", "SB AG REVOLUCION"),
                ("4S", "SB AG SENDEROS"), ("CO", "CONTRATISTAS CCC"), ("CT", "COMERCIAL TRN ZONA"),
                ("DT", "DISTRIBUCION TRN"), ("FM", "AREA FCO I MADERO"), ("LI", "LIRE"),
                ("MA", "AREA MATAMOROS"), ("MT", "MEDICION TORREON"), ("OC", "TODOS LOS CENTROS"),
                ("PA", "AREA PARRAS"), ("PT", "PLANEACION TORREON"), ("SP", "AREA SAN PEDRO"),
                ("TC", "CCC REVOLUCION"), ("TM", "CCC MEXICO"), ("TR", "CCC CENTRO"),
            ],
            ["C24"] =
            [
                ("2A", "PRL AG PARRAL"), ("2B", "PRL AG BALLEZA"), ("2C", "PRL AG STA BARBARA"),
                ("2G", "PRL AG STA MA ORO"), ("2H", "PRL AG LAS NIEVES"), ("2K", "PRL AG GPE Y CALVO"),
                ("2L", "PRL AG V ALLENDE"), ("2M", "PRL AG GUACHOCHI"), ("2N", "PRL AG JIMENEZ"),
                ("4A", "SB AG PARRAL"), ("4B", "SB AG BALLEZA"), ("4C", "SB AG STA BARBARA"),
                ("4G", "SB AG STA MA ORO"), ("4H", "SB AG LAS NIEVES"), ("4K", "SB AG GPE Y CALVO"),
                ("4L", "SB AG V ALLENDE"), ("4M", "SB AG GUACHOCHI"), ("4N", "SB AG JIMENEZ"),
                ("C1", "PRL CCC JIMENEZ"), ("CC", "PRL CCC PARRAL"), ("D2", "PRL OFNA OPER/MTTO"),
                ("D3", "PRL OFNA PLANEACION"), ("DM", "PRL DEPT MEDICION"), ("DZ", "PRL DIST ZONA"),
                ("M1", "PRL MED JIMENEZ"), ("OC", "TODOS LOS CENTROS"), ("PM", "PROCC AUT MEDICION"),
            ],
            ["C26"] =
            [
                ("2A", "AGENCIA PTE DGO"), ("2B", "AREA CANATLAN"), ("2C", "AGENCIA OTE DGO"),
                ("2D", "AREA GPE. VICTORIA"), ("2E", "AREA S. PAPASQUIARO"), ("2F", "AREA VIC. GUERRERO"),
                ("2G", "AREA EL SALTO P NVO"), ("2I", "FCO.I.MADERO"), ("2J", "AGENCIA NUEVO IDEAL"),
                ("2K", "AG SAN JUAN DEL RIO"), ("2L", "AGENCIA TEPEHUANES"), ("2M", "AGENCIA SUR DGO"),
                ("2Y", "AGENCIA VILLA UNION"), ("4A", "SB AG ALAMEDAS"), ("4B", "SB AG CANATLAN"),
                ("4C", "SB AG FELIPE PESCAD"), ("4D", "SB AG GPE. VICTORIA"), ("4E", "SB AG S. PAPASQUIAR"),
                ("4F", "SB AG VIC. GUERRERO"), ("4G", "SB AG EL SALTO P NV"), ("4I", "SB FCO I MADERO"),
                ("4J", "SB AG NUEVO IDEAL"), ("4K", "SB AG SAN JUAN DEL"), ("4L", "SB AG TEPEHUANES"),
                ("4M", "SB AG SUR DGO"), ("4Y", "SB AG VILLA UNION"), ("CO", "OFICINA CONSTRUCION"),
                ("CS", "CSC"), ("DC", "DEPTO.COM DGO"), ("DD", "DEPTO.DISTRIB. DGO."),
                ("DI", "DEPTO. INFORMATICA"), ("DM", "DEPTO.MED DGO"), ("EE", "EVENTOS ESP DGO"),
                ("MA", "OFNA.MTTO DGO"), ("OC", "TODOS LOS CENTROS"), ("OP", "OFNA.OP DGO"),
                ("PL", "PLANEACION DURANGO"), ("ZD", "ZONA DURANGO"),
            ],
            ["C27"] =
            [
                ("4A", "SB AG CENTRO"), ("4B", "SB AG LERDO"), ("4C", "SB AG TLAHUALILO"),
                ("4D", "SB AG BERMEJILLO"), ("4E", "SB AG CUENCAME"), ("4F", "SB AG RODEO"),
                ("4J", "SB AG CEBALLOS"), ("4K", "SB AG NAZAS"), ("4M", "SB AG PEDREGAL"),
                ("7A", "AGENCIA CENTRO"), ("7B", "AGENCIA LERDO"), ("7C", "AGENCIA TLAHUALILO"),
                ("7D", "AGENCIA BERMEJILLO"), ("7E", "AGENCIA CUENCAME"), ("7F", "AGENCIA RODEO"),
                ("7J", "AGENCIA CEBALLOS"), ("7K", "AGENCIA NAZAS"), ("7M", "AGENCIA PEDREGAL"),
                ("BR", "AREA BERMEJILLO"), ("CG", "COMERCIAL GPL ZONA"), ("CO", "CONTRATISTAS CCC"),
                ("CU", "AREA CUENCAME"), ("GC", "CCC CENTRO"), ("GL", "CCC LERDO"),
                ("GP", "ZONA GOMEZ PALACIO"), ("MG", "MEDICION GPL"), ("OC", "TODOS LOS CENTROS"),
                ("OL", "OPERATIVO LAGUNA"), ("PG", "PLANEACION GPL"),
            ],
        };

        public IEnumerable<(string Value, string Label)> GetZonas() => ZonasCatalogo;

        public IEnumerable<(string Value, string Label)> GetCentros(string zona) =>
            CentrosCatalogo.TryGetValue(zona, out var list) ? list : [];

        // ── Scraping ───────────────────────────────────────────────────────────────

        /// <summary>
        /// Trae las solicitudes pendientes de SICOSS para una zona/centro, filtradas a
        /// las que le interesan al usuario: Tipo (código de causa real, ej. E03/Q07/C05)
        /// que empiece con E, Q o C — sin importar bajo qué categoría del reporte (ARM,
        /// RBT, RSC, etc.) aparezcan. No hay filtro de fechas: el sistema origen no lo
        /// soporta, siempre es el estado actual ("ahora mismo").
        /// </summary>
        public async Task<List<Dictionary<string, string>>> GetPendientesAsync(string zona, string cen, string cveDivision = "DC000")
        {
            var client = _httpFactory.CreateClient();
            var url = $"{Url}?zona={Uri.EscapeDataString(zona)}&cen={Uri.EscapeDataString(cen)}&graf=Generar";

            byte[] bytes;
            try
            {
                bytes = await client.GetByteArrayAsync(url);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SicossDistribucionService: no se pudo contactar {Url}", url);
                throw new InvalidOperationException("No se pudo contactar el sistema SICOSS Distribución (¿VPN activa?).", ex);
            }

            // La página declara charset=iso-8859-1 — decodificar así, no como UTF-8, o los
            // acentos (ej. "atención") salen corruptos.
            var html = Encoding.GetEncoding("iso-8859-1").GetString(bytes);

            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            var result = new List<Dictionary<string, string>>();
            string categoria = "", categoriaCodigo = "";

            // Recorre h1 (encabezado de cada categoría: ARM/CBT/.../RSS) y las tablas de
            // detalle table[@border=1] (Urbana/Rural) en el orden real del documento —
            // Descendants() de HtmlAgilityPack es un recorrido de árbol, respeta el orden.
            foreach (var node in doc.DocumentNode.Descendants())
            {
                if (node.Name == "h1")
                {
                    var m = Regex.Match(WebUtility.HtmlDecode(node.InnerText).Trim(), @"^(.*?)\s*\(([A-Z0-9]+)\)\s*$");
                    if (m.Success)
                    {
                        categoria = m.Groups[1].Value.Trim();
                        categoriaCodigo = m.Groups[2].Value;
                    }
                    continue;
                }

                if (node.Name != "table" || node.GetAttributeValue("border", "") != "1")
                    continue;

                var caption = node.SelectSingleNode(".//caption");
                if (caption == null) continue; // tabla vacía (sin solicitudes para esa área)
                var esUrbana = caption.InnerText.Contains("Urbana", StringComparison.OrdinalIgnoreCase);

                var rows = node.SelectNodes(".//tr");
                if (rows == null) continue;

                foreach (var tr in rows)
                {
                    // Solo las filas de datos reales tienen el link de la solicitud
                    // (onclick=AbrirVentana(...)); la fila de encabezado y la de
                    // "Son N solicitudes" no lo tienen.
                    var link = tr.SelectSingleNode(".//a[contains(@onclick,'AbrirVentana')]");
                    if (link == null) continue;

                    var cells = tr.SelectNodes("./td");
                    if (cells == null || cells.Count < 7) continue;

                    var tipo = WebUtility.HtmlDecode(cells[2].InnerText).Trim();
                    if (tipo.Length == 0 || !"EQC".Contains(tipo[0])) continue;

                    // El folio real que espera consulta.cgi (usado por GetDetalleSolicitudAsync)
                    // se lee del propio onclick=AbrirVentana('/cgi-bin/.../consulta.cgi?FOLIO',...)
                    // en vez de asumir que el texto visible del link es igual — así el folio
                    // enviado siempre es exactamente el que el portal usa, sin depender de que
                    // coincidan por casualidad de formato.
                    var onclick = link.GetAttributeValue("onclick", "");
                    var folioMatch = Regex.Match(onclick, @"consulta\.cgi\?([^'""&]+)", RegexOptions.IgnoreCase);
                    var folio = folioMatch.Success
                        ? WebUtility.HtmlDecode(folioMatch.Groups[1].Value).Trim()
                        : WebUtility.HtmlDecode(link.InnerText).Trim(); // respaldo si el onclick no trae el folio

                    result.Add(new Dictionary<string, string>
                    {
                        ["Solicitud"] = folio,
                        ["Categoria"] = categoria,
                        ["CategoriaCodigo"] = categoriaCodigo,
                        ["Area"] = esUrbana ? "Urbana" : "Rural",
                        ["Col"] = WebUtility.HtmlDecode(cells[1].InnerText).Trim(),
                        ["Tipo"] = tipo,
                        ["Fecha"] = WebUtility.HtmlDecode(cells[3].InnerText).Trim(),
                        ["Hora"] = WebUtility.HtmlDecode(cells[4].InnerText).Trim(),
                        ["Horas"] = WebUtility.HtmlDecode(cells[5].InnerText).Trim(),
                        ["Status"] = WebUtility.HtmlDecode(cells[6].InnerText).Trim(),
                    });
                }
            }

            // La "Clave" que guarda el apartado Colonias (cssnal.cfe.mx) es Zona(3) + Col(3)
            // — ej. "C22T15" = zona C22 + Col "T15". Buscamos exacto (zona+Col) en vez de por
            // sufijo global para no arrastrar colisiones entre colonias de otras zonas.
            var coloniaMap = await BuildColoniaMapAsync(zona);

            // Si nadie ha consultado Colonias todavía para esta zona, la BD no tiene nada que
            // mapear — en vez de obligar al usuario a visitar Colonias primero, se dispara un
            // scrape de Colonias en el momento (todas las zonas/áreas de cssnal.cfe.mx, "00000")
            // y se guarda, igual que si el usuario lo hubiera consultado él mismo. Así el orden
            // en que se visitan los apartados deja de importar.
            if (coloniaMap.Count == 0)
            {
                try
                {
                    _logger.LogInformation("SicossDistribucionService: sin mapeo de colonias para zona={Zona}, disparando scrape de Colonias como respaldo", zona);
                    var anio = DateTime.Now.Year;
                    var desde = RangoFechas.Desde(anio);
                    var hasta = RangoFechas.Hasta(anio);
                    var coloniasRows = await _scraper.GetColoniasReportAsync(desde, hasta, "00000", "00000", cveDivision);
                    if (coloniasRows.Count > 0)
                    {
                        await _store.SaveColoniasAsync(coloniasRows, anio, "00000", "00000");
                        coloniaMap = await BuildColoniaMapAsync(zona);
                    }
                }
                catch (Exception ex)
                {
                    // No debe tumbar la respuesta de SICOSS — si el respaldo falla, simplemente
                    // se muestran los códigos "Col" crudos (ya es el comportamiento por defecto).
                    _logger.LogWarning("SicossDistribucionService: no se pudo scrapear Colonias como respaldo: {Err}", ex.Message);
                }
            }

            foreach (var row in result)
            {
                var clave = (zona + row["Col"]).ToUpperInvariant();
                row["ColoniaNombre"] = coloniaMap.TryGetValue(clave, out var nombre) ? nombre : "";
            }

            _logger.LogInformation("SicossDistribucionService: {Count} solicitudes (E/Q/C) para zona={Zona} cen={Cen}", result.Count, zona, cen);
            return result;
        }

        /// <summary>
        /// Detalle en vivo de UNA solicitud (folio, ej. "C2206407416") — se pide bajo demanda
        /// (clic en la fila en el frontend), no en cada carga de la tabla de pendientes, para
        /// no disparar una petición extra por cada solicitud. Trae dos tablas de la página de
        /// consulta.cgi: BITACORA DE MOVIMIENTOS (quién preasignó, a qué cuadrilla de CFE —
        /// ej. "Cuad:C2223" en la columna Observaciones de la fila "PREASIGNO") y BITACORA DE
        /// SERVICIOS (historial de servicios en esa dirección, incluye contratista cuando
        /// "Atendido por" dice algo como "CONTT - CONTRATISTA CCC1"). El HTML es igual de
        /// "tag soup" (sin cerrar &lt;/td&gt;/&lt;/tr&gt;) que el resto de los portales CFE — HtmlAgilityPack
        /// lo tolera igual que en ParseCausasTable/ParseColoniasTable de WebScraperService.
        /// </summary>
        public async Task<Dictionary<string, List<Dictionary<string, string>>>> GetDetalleSolicitudAsync(string solicitud)
        {
            var client = _httpFactory.CreateClient();
            var url = $"{DetalleUrl}?{Uri.EscapeDataString(solicitud)}";

            byte[] bytes;
            try
            {
                bytes = await client.GetByteArrayAsync(url);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SicossDistribucionService: no se pudo contactar {Url}", url);
                throw new InvalidOperationException("No se pudo contactar el sistema SICOSS (¿VPN activa?).", ex);
            }

            // Misma codificación que pendctosa.cgi — declarada iso-8859-1 en el portal.
            var html = Encoding.GetEncoding("iso-8859-1").GetString(bytes);
            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            var movimientos = new List<Dictionary<string, string>>();
            var servicios    = new List<Dictionary<string, string>>();

            // Recorre h1 (encabezado de cada sección) y la tabla que le sigue en el orden real
            // del documento — igual patrón que GetPendientesAsync con categoria/categoriaCodigo.
            // Solo la tabla INMEDIATAMENTE después de "BITACORA DE MOVIMIENTOS"/"BITACORA DE
            // SERVICIOS" cuenta; cualquier otro h1 (INFORMACION GENERAL, etc.) apaga la captura.
            string seccion = "";
            foreach (var node in doc.DocumentNode.Descendants())
            {
                if (node.Name == "h1")
                {
                    var texto = WebUtility.HtmlDecode(node.InnerText).Trim().ToUpperInvariant();
                    seccion = texto.Contains("BITACORA DE MOVIMIENTOS") ? "MOV"
                            : texto.Contains("BITACORA DE SERVICIOS")   ? "SERV"
                            : "";
                    continue;
                }

                if (node.Name != "table" || seccion == "") continue;

                var target = seccion == "MOV" ? movimientos : servicios;
                seccion = ""; // ya se captura esta tabla; no volver a disparar con la siguiente

                var rows = node.SelectNodes(".//tr");
                if (rows == null) continue;

                List<string>? headers = null;
                foreach (var tr in rows)
                {
                    var cells = tr.SelectNodes("./td");
                    if (cells == null || cells.Count == 0) continue;

                    if (headers == null)
                    {
                        headers = cells.Select(c => WebUtility.HtmlDecode(c.InnerText).Trim()).ToList();
                        continue;
                    }

                    var item = new Dictionary<string, string>();
                    for (int i = 0; i < cells.Count && i < headers.Count; i++)
                        item[headers[i]] = WebUtility.HtmlDecode(cells[i].InnerText).Trim();
                    target.Add(item);
                }
            }

            _logger.LogInformation(
                "SicossDistribucionService: detalle {Solicitud} -> {Mov} movimientos, {Serv} servicios",
                solicitud, movimientos.Count, servicios.Count);

            return new Dictionary<string, List<Dictionary<string, string>>>
            {
                ["Movimientos"] = movimientos,
                ["Servicios"]   = servicios
            };
        }

        /// <summary>
        /// Nombre de colonia por Clave, tomado de lo que el apartado Colonias ya guardó en
        /// HechosReportes (Fuente="COLONIAS") — sin scrape nuevo. Si esa zona nunca se ha
        /// consultado desde Colonias, el mapa sale vacío y GetPendientesAsync dispara un
        /// scrape de respaldo (ver ahí).
        /// </summary>
        private async Task<Dictionary<string, string>> BuildColoniaMapAsync(string zona)
        {
            var rows = await _db.HechosReportes
                .Where(h => h.Fuente == "COLONIAS" && h.Cve.StartsWith(zona))
                .Select(h => new { h.Cve, h.Descripcion })
                .Distinct()
                .ToListAsync();

            var map = new Dictionary<string, string>();
            foreach (var r in rows)
                map[r.Cve.ToUpperInvariant()] = r.Descripcion;
            return map;
        }
    }
}
