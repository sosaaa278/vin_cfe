namespace DashboardAPI.Models
{
    /// <summary>
    /// Fila en formato "largo/tidy" pensada para reportes en Power BI.
    /// Cada número del portal se guarda como un registro con sus dimensiones
    /// (fuente, año, mes, zona, área, categoría, métrica). Unifica IMU y Causas;
    /// las Inconformidades se suman a través de la vista SQL vw_hechos.
    /// </summary>
    public class HechoReporte
    {
        public int Id { get; set; }

        public string Fuente { get; set; } = "";        // IMU | CAUSAS | COLONIAS | QUEJAS_EMERGENCIAS
        public DateTime FechaConsulta { get; set; }      // cuándo se scrapeó

        public int  Anio { get; set; }
        public int? Mes  { get; set; }                   // IMU sí tiene mes; Causas no (acumulado)

        public string ZonaFiltro { get; set; } = "";     // cveZona usada en la consulta (00000 = todas)
        public string Cve  { get; set; } = "";           // clave de la fila (ej. DC010)
        public string Area { get; set; } = "";           // nombre de zona/área

        public string Categoria   { get; set; } = "";    // IMU: COMERCIAL/MEDICION/...; Causas: código (E02…)
        public string Metrica     { get; set; } = "";    // IMU: CONS ANORM…; Causas: Clave
        public string Descripcion { get; set; } = "";    // Causas: descripción de la causa

        public double Valor { get; set; }
    }
}
