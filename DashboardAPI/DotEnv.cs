namespace DashboardAPI;

/// <summary>
/// Minimal .env loader (no external dependency).
/// Reads KEY=VALUE lines and sets them as process environment variables,
/// which ASP.NET Core's default configuration then picks up
/// (using the "__" → ":" nesting convention, e.g. Jwt__Key → Jwt:Key).
///
/// Must be called BEFORE WebApplication.CreateBuilder so the values are
/// available to the default AddEnvironmentVariables() source.
/// Existing environment variables are NOT overwritten (real env wins over .env).
/// </summary>
public static class DotEnv
{
    public static void Load()
    {
        // Look for .env next to the working dir and next to the binary.
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

            // Strip optional surrounding quotes.
            if (value.Length >= 2 &&
                ((value[0] == '"' && value[^1] == '"') ||
                 (value[0] == '\'' && value[^1] == '\'')))
            {
                value = value[1..^1];
            }

            // Real environment variables take precedence over .env entries.
            if (Environment.GetEnvironmentVariable(key) is null)
                Environment.SetEnvironmentVariable(key, value);
        }
    }
}
