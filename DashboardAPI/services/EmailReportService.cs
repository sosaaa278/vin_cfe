using System.Text;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using DashboardAPI.Models;

namespace DashboardAPI.Services
{
    /// <summary>
    /// Arma y envía el reporte diario por correo con el resumen del dashboard
    /// principal de Inconformidades — mismos datos y agrupación que "Todas
    /// (agrupado)" en el frontend (ver dashboard.component.ts buildGroupedCompareData()),
    /// reutilizando FullCompareService en vez de scrapear de nuevo.
    /// </summary>
    public class EmailReportService
    {
        // Mismos códigos visibles que dashboard.component.ts VISIBLE_COMPARE_CODES —
        // iterar sobre TODAS las claves crudas del scrape duplicaría el total (ya
        // pasó una vez con el dropdown "Todas (agrupado)" del dashboard).
        private static readonly string[] VisibleCodes =
            ["30202", "E02", "E03", "E04", "E05", "E06", "E07", "Q07"];

        private readonly FullCompareService _compare;
        private readonly IConfiguration _config;
        private readonly ILogger<EmailReportService> _logger;

        public EmailReportService(FullCompareService compare, IConfiguration config, ILogger<EmailReportService> logger)
        {
            _compare = compare;
            _config  = config;
            _logger  = logger;
        }

        /// <param name="overrideTo">Si se pasa, manda el reporte a esta dirección en vez de
        /// la configurada en Email:To — usado por el botón "Enviar reporte" del frontend,
        /// donde el usuario teclea a dónde quiere que llegue ese envío puntual.</param>
        public async Task SendDailyReportAsync(string? overrideTo = null)
        {
            var division = _config["Email:Division"] ?? "DC000";
            var resp = await _compare.GetFullCompareAsync(cveDivision: division);
            var html = BuildHtml(resp);
            await SendAsync($"Reporte diario de Inconformidades — {DateTime.Now:dd/MM/yyyy}", html, overrideTo);
        }

        /// <summary>
        /// Correo mínimo sin datos ("hola mundo") — para probar que la conexión SMTP y las
        /// credenciales funcionan, sin depender de que el scraping/VPN esté disponible.
        /// </summary>
        public async Task SendTestEmailAsync(string to)
        {
            var html = "<p>Hola mundo — este es un correo de prueba del DashboardAPI. " +
                       $"Si lo estás leyendo, la conexión SMTP funciona correctamente.</p>" +
                       $"<p>Enviado: {DateTime.Now:dd/MM/yyyy HH:mm}</p>";
            await SendAsync("Prueba — DashboardAPI", html, to);
        }

        private static string BuildHtml(FullCompareResponse resp)
        {
            var codes = VisibleCodes.Where(c => resp.Compare.TryGetValue(c, out var l) && l.Count > 0).ToList();
            if (codes.Count == 0)
                return "<p>No se encontraron datos para armar el reporte de hoy.</p>";

            var areas = resp.Compare[codes[0]].Select(c => c.AREA).ToList();

            var rows = new List<(string Area, double Total2025, double Total2026, double Variacion)>();
            foreach (var area in areas)
            {
                double total2025 = 0, total2026 = 0;
                foreach (var code in codes)
                {
                    var row = resp.Compare[code].FirstOrDefault(c => c.AREA == area);
                    if (row == null) continue;
                    total2025 += row.Total2025;
                    total2026 += row.Total2026;
                }
                var variacion = total2025 > 0
                    ? Math.Round((total2026 - total2025) / total2025 * 10000) / 100
                    : (total2026 > 0 ? 100 : 0);
                rows.Add((area, total2025, total2026, variacion));
            }

            var granTotal2025 = rows.Sum(r => r.Total2025);
            var granTotal2026 = rows.Sum(r => r.Total2026);
            var granVariacion = granTotal2025 > 0
                ? Math.Round((granTotal2026 - granTotal2025) / granTotal2025 * 10000) / 100
                : (granTotal2026 > 0 ? 100 : 0);

            var sb = new StringBuilder();
            sb.Append($"<h2>Resumen diario de Inconformidades — {DateTime.Now:dd/MM/yyyy}</h2>");
            sb.Append("<table border='1' cellpadding='6' cellspacing='0' style='border-collapse:collapse;font-family:sans-serif;font-size:13px'>");
            sb.Append("<tr style='background:#006341;color:#fff'><th>Zona</th><th>2025</th><th>2026</th><th>Variación</th></tr>");
            foreach (var r in rows.OrderByDescending(r => r.Total2026))
                sb.Append($"<tr><td>{r.Area}</td><td>{r.Total2025:N0}</td><td>{r.Total2026:N0}</td><td>{r.Variacion:+0.##;-0.##;0}%</td></tr>");
            sb.Append($"<tr style='font-weight:bold;background:#f1f1f1'><td>TOTAL</td><td>{granTotal2025:N0}</td><td>{granTotal2026:N0}</td><td>{granVariacion:+0.##;-0.##;0}%</td></tr>");
            sb.Append("</table>");
            return sb.ToString();
        }

        private async Task SendAsync(string subject, string html, string? overrideTo = null)
        {
            var host        = _config["Email:SmtpHost"] ?? "smtp.gmail.com";
            var port        = _config.GetValue<int>("Email:SmtpPort", 587);
            var user        = _config["Email:User"];
            var appPassword = _config["Email:AppPassword"];
            // overrideTo (tecleado por el usuario en el frontend) manda sobre el Email:To
            // configurado — sigue haciendo falta el usuario/contraseña de la cuenta que envía.
            var to = !string.IsNullOrWhiteSpace(overrideTo) ? overrideTo : _config["Email:To"];

            if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(appPassword) || string.IsNullOrWhiteSpace(to))
            {
                _logger.LogWarning("EmailReportService: faltan credenciales (Email:User/AppPassword) o destinatario — correo no enviado.");
                throw new InvalidOperationException("Faltan credenciales de correo o destinatario.");
            }

            // Nombre para mostrar en vez de la dirección pelona (ej. "Reportes CFE Torreón")
            // — la dirección real sigue siendo visible si el destinatario la revisa a detalle,
            // pero así no se ve directamente en el resumen de la bandeja de entrada.
            var fromName = _config["Email:FromName"];
            var message  = new MimeMessage();
            message.From.Add(string.IsNullOrWhiteSpace(fromName)
                ? MailboxAddress.Parse(user)
                : new MailboxAddress(fromName, user));
            message.To.Add(MailboxAddress.Parse(to));
            message.Subject = subject;
            message.Body = new TextPart("html") { Text = html };

            using var client = new SmtpClient
            {
                // Default de MailKit es 2 min — si la red bloquea el puerto saliente
                // (típico en redes empresariales), preferimos fallar rápido en vez de
                // colgar cada prueba 2 minutos.
                Timeout = 15_000
            };
            await client.ConnectAsync(host, port, SecureSocketOptions.StartTls);
            await client.AuthenticateAsync(user, appPassword);
            await client.SendAsync(message);
            await client.DisconnectAsync(true);

            _logger.LogInformation("EmailReportService: correo enviado a {To}", to);
        }
    }
}
