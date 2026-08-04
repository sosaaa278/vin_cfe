namespace DashboardAPI.Services
{
    /// <summary>
    /// Envía el reporte diario de Inconformidades por correo a la hora configurada
    /// (Email:SendHourLocal, default 8am). Corre en proceso — no depende de cron ni
    /// systemd, consistente con el despliegue actual (el backend corre como proceso
    /// persistente dentro de una sesión `screen` en el servidor).
    /// </summary>
    public class DailyReportBackgroundService : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IConfiguration _config;
        private readonly ILogger<DailyReportBackgroundService> _logger;

        public DailyReportBackgroundService(
            IServiceScopeFactory scopeFactory, IConfiguration config, ILogger<DailyReportBackgroundService> logger)
        {
            _scopeFactory = scopeFactory;
            _config       = config;
            _logger       = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var hour = _config.GetValue<int>("Email:SendHourLocal", 8);
                var now  = DateTime.Now;
                var next = now.Date.AddHours(hour);
                if (next <= now) next = next.AddDays(1);

                try
                {
                    await Task.Delay(next - now, stoppingToken);
                }
                catch (TaskCanceledException)
                {
                    break;
                }

                if (stoppingToken.IsCancellationRequested) break;

                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var email = scope.ServiceProvider.GetRequiredService<EmailReportService>();
                    await email.SendDailyReportAsync();
                    _logger.LogInformation("Reporte diario enviado por correo.");
                }
                catch (Exception ex)
                {
                    // No debe tumbar el proceso — solo loguear y reintentar mañana.
                    _logger.LogError(ex, "Falló el envío del reporte diario: {Err}", ex.Message);
                }
            }
        }
    }
}
