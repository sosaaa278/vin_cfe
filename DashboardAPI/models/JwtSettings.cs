namespace DashboardAPI.Models;

public class JwtSettings
{
    public string Key           { get; set; } = "";
    public string Issuer        { get; set; } = "";
    public string Audience      { get; set; } = "";
    public int    ExpiryMinutes { get; set; } = 480;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Key))
            throw new InvalidOperationException(
                "Jwt:Key no está configurado. Establécelo con: dotnet user-secrets set \"Jwt:Key\" \"<valor>\"");

        if (Key.Length < 32)
            throw new InvalidOperationException(
                $"Jwt:Key debe tener al menos 32 caracteres (actualmente tiene {Key.Length}).");

        if (string.IsNullOrWhiteSpace(Issuer))
            throw new InvalidOperationException("Jwt:Issuer no está configurado.");

        if (string.IsNullOrWhiteSpace(Audience))
            throw new InvalidOperationException("Jwt:Audience no está configurado.");
    }
}
