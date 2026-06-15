namespace DashboardAPI.Models
{
    public class ScrapeCache
    {
        public int Id { get; set; }
        public string Clave { get; set; } = "";      // ej. "meta-real", o "causas:2026:00000"
        public string Json { get; set; } = "";        // el payload serializado
        public DateTime FechaGuardadoUtc { get; set; } // para calcular el TTL
    }
}