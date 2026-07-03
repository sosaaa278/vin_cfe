using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using DashboardAPI.Models;
using Microsoft.IdentityModel.Tokens;

namespace DashboardAPI.Services;

public class JwtService : IJwtService
{
    private readonly JwtSettings _settings;

    public JwtService(JwtSettings settings) => _settings = settings;

    public (string token, DateTime expiry) GenerateToken(User user)
    {
        var key    = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_settings.Key));
        var creds  = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var expiry = DateTime.UtcNow.AddMinutes(_settings.ExpiryMinutes);

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new Claim("rpe",           user.Rpe),
            new Claim("division",      user.DivisionCode),
            new Claim(ClaimTypes.Role, user.Role),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
        };

        var token = new JwtSecurityToken(
            issuer:             _settings.Issuer,
            audience:           _settings.Audience,
            claims:             claims,
            expires:            expiry,
            signingCredentials: creds);

        return (new JwtSecurityTokenHandler().WriteToken(token), expiry);
    }
}
