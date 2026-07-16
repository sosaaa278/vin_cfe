namespace DashboardAPI.Helpers
{
    /// <summary>
    /// Rango de fechas que usan el scraping y los comparativos (dashboard y causas).
    ///
    /// Por ahora es FIJO: del 1 de enero al 4 de mayo de cada año.
    /// Más adelante, cuando se agregue la lista desplegable, este rango debe venir
    /// de la selección del usuario; al estar centralizado aquí, solo habrá que
    /// cambiar este archivo (o pasar el rango como parámetro) en un único lugar.
    /// </summary>
    public static class RangoFechas
    {
        // Día/mes de corte = fin del rango. Mientras sea fijo, se ajusta aquí.
        public const int MesCorte = 5;   // mayo
        public const int DiaCorte = 4;   // día 4

        /// <summary>Inicio del rango para un año dado, p. ej. "2026/01/01".</summary>
        public static string Desde(int anio) => $"{anio}/01/01";

        /// <summary>Fin del rango para un año dado, p. ej. "2026/05/04".</summary>
        public static string Hasta(int anio) => $"{anio}/{MesCorte:D2}/{DiaCorte:D2}";

        // ── Helpers para el rango elegido por el usuario (selector de fechas) ──────────
        // Las fechas pueden venir como "yyyy-MM-dd" (input HTML) o "yyyy/MM/dd".

        /// <summary>Normaliza una fecha a "yyyy/MM/dd" (acepta guiones o diagonales).</summary>
        public static string Normaliza(string fecha) => fecha.Replace("-", "/").Trim();

        /// <summary>Devuelve la misma fecha pero con otro año, p. ej. ConAnio("2026/03/15", 2025) = "2025/03/15".</summary>
        public static string ConAnio(string fecha, int anio)
        {
            var p = Normaliza(fecha).Split('/');
            return p.Length == 3 ? $"{anio:D4}/{p[1]}/{p[2]}" : Hasta(anio);
        }

        /// <summary>Año de una fecha "yyyy/MM/dd" o "yyyy-MM-dd".</summary>
        public static int Anio(string fecha) => int.Parse(Normaliza(fecha).Split('/')[0]);

    }
}
