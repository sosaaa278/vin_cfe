namespace DashboardAPI;

/// <summary>
/// Cargador mínimo de archivos .env (sin dependencias externas).
/// Lee líneas KEY=VALUE y las define como variables de entorno del proceso,
/// que luego la configuración por defecto de ASP.NET Core recoge
/// (usando la convención de anidado "__" → ":", ej. Jwt__Key → Jwt:Key).
///
/// Debe llamarse ANTES de WebApplication.CreateBuilder para que los valores estén
/// disponibles para el origen por defecto AddEnvironmentVariables().
/// Las variables de entorno ya existentes NO se sobreescriben (el entorno real gana sobre el .env).
/// </summary>
public static class DotEnv
{
    public static void Load()
    {
        // Buscamos el .env junto al directorio de trabajo y junto al ejecutable.
        var candidates = new[]
        {
            Path.Combine(Directory.GetCurrentDirectory(), ".env"),
            Path.Combine(AppContext.BaseDirectory, ".env")
        };

        var path = candidates.FirstOrDefault(File.Exists);
        if (path is null) return;

        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            var idx = line.IndexOf('=');
            if (idx <= 0) continue;

            var key   = line[..idx].Trim();
            var value = line[(idx + 1)..].Trim();

            // Quitamos las comillas que rodean el valor, si las hay.
            if (value.Length >= 2 &&
                ((value[0] == '"' && value[^1] == '"') ||
                 (value[0] == '\'' && value[^1] == '\'')))
            {
                value = value[1..^1];
            }

            // Las variables de entorno reales tienen prioridad sobre las del .env.
            if (Environment.GetEnvironmentVariable(key) is null)
                Environment.SetEnvironmentVariable(key, value);
        }
    }
}
