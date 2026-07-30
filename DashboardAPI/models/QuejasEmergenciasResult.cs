namespace DashboardAPI.Models
{
    /// <summary>
    /// Resultado combinado del reporte "Quejas y Emergencias" (sistema sisquem,
    /// http://10.4.17.45/sisquem/...). A diferencia de los demás reportes CFE, esta
    /// fuente responde JSON en vez de HTML de página completa: GraficasQuejasEmergencias
    /// mapea directo los arrays de ese JSON; las demás listas vienen de parsear los
    /// fragmentos HTML embebidos en la misma respuesta (tablas ya renderizadas por sisquem).
    /// </summary>
    public class QuejasEmergenciasResult
    {
        public GraficasQuejasEmergencias Graficas { get; set; } = new();
        public List<Dictionary<string, string>> ResumenEmergencias { get; set; } = new();
        public List<Dictionary<string, string>> ResumenQuejas { get; set; } = new();
        public List<Dictionary<string, string>> DetalleEmergencias { get; set; } = new();
        public List<Dictionary<string, string>> DetalleQuejas { get; set; } = new();
        public List<Dictionary<string, string>> Listado { get; set; } = new();

        // Resumen por Estado y por Municipio — vienen concatenados en un mismo bloque HTML
        // por tipo (Emergencias / Quejas), cada uno con 2 tablas (Estado, luego Municipio).
        public List<Dictionary<string, string>> ResumenEstadoEmergencias { get; set; } = new();
        public List<Dictionary<string, string>> ResumenMunicipioEmergencias { get; set; } = new();
        public List<Dictionary<string, string>> ResumenEstadoQuejas { get; set; } = new();
        public List<Dictionary<string, string>> ResumenMunicipioQuejas { get; set; } = new();

        /// <summary>Viene de integridad.ok en el JSON — false indica que sisquem no pudo descargar todas sus fuentes internas.</summary>
        public bool IntegridadOk { get; set; } = true;

        /// <summary>Resumen de texto libre ("Al momento se cuenta con...") que sisquem imprime al final del HTML de tablas — mismos totales que ResumenEmergencias/ResumenQuejas, solo que ya vienen sumados con Vencidas/En atención incluidos.</summary>
        public ResumenGlobalQuejasEmergencias ResumenGlobal { get; set; } = new();
    }

    /// <summary>Los 6 números del párrafo "Al momento se cuenta con: Emergencias: N pendientes, N vencidas y N en atención. Quejas: ...".</summary>
    public class ResumenGlobalQuejasEmergencias
    {
        public int EmergenciasPendientes { get; set; }
        public int EmergenciasVencidas { get; set; }
        public int EmergenciasEnAtencion { get; set; }
        public int QuejasPendientes { get; set; }
        public int QuejasVencidas { get; set; }
        public int QuejasEnAtencion { get; set; }
    }

    /// <summary>Series de tiempo horarias tal como las devuelve sisquem.php (arrays paralelos a EjeX).</summary>
    public class GraficasQuejasEmergencias
    {
        public List<string> EjeX { get; set; } = new();
        public List<double> EmergenciasPendientes { get; set; } = new();
        public List<double> QuejasPendientes { get; set; } = new();
        public List<double> EmergenciasAtendidas { get; set; } = new();
        public List<double> QuejasAtendidas { get; set; } = new();
        public List<double> EmergenciasGeneradas { get; set; } = new();
        public List<double> QuejasGeneradas { get; set; } = new();
    }
}
