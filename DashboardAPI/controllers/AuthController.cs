using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using DashboardAPI.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.Tokens;

namespace DashboardAPI.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly IConfiguration _config;

    public AuthController(IConfiguration config)
    {
        _config = config;
    }

    [EnableRateLimiting("login")]
    [HttpPost("login")]
    public IActionResult Login([FromBody] LoginRequest request)
    {
        var users = _config.GetSection("Users").Get<List<UserConfig>>();
        var user = users?.FirstOrDefault(u =>
            u.Username == request.Username &&
            u.Password == request.Password);

        if (user == null)
            return Unauthorized(new { message = "Credenciales incorrectas" });

        var key   = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_config["Jwt:Key"]!));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var expiry = int.Parse(_config["Jwt:ExpiryHours"] ?? "8");

        var token = new JwtSecurityToken(
            issuer:            _config["Jwt:Issuer"],
            audience:          _config["Jwt:Audience"],
            claims: [
                new Claim(ClaimTypes.Name, user.Username),
                new Claim(ClaimTypes.Role, "User")
            ],
            expires:           DateTime.UtcNow.AddHours(expiry),
            signingCredentials: creds
        );

        return Ok(new
        {
            token  = new JwtSecurityTokenHandler().WriteToken(token),
            expiry = token.ValidTo
        });
    }
}
