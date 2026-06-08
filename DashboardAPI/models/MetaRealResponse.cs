namespace DashboardAPI.Models
{
    public class MetaRealResponse
    {
        public List<Dictionary<string, string>> Data { get; set; } = new();

        public string RowLabelKey { get; set; } = "Periodo";

        public bool TableDetected { get; set; } = false;

        public string LastUpdated { get; set; } = DateTime.UtcNow.ToString("o");

        // Metadata de filtros/fechas (para export Excel)
        public string Division { get; set; } = string.Empty;
        public string Proceso { get; set; } = string.Empty;
        public string FechaDesde { get; set; } = string.Empty;
        public string FechaHasta { get; set; } = string.Empty;

    }
}
