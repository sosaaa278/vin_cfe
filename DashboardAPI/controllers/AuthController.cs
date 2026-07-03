using DashboardAPI.Data;
using DashboardAPI.Models;
using DashboardAPI.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace DashboardAPI.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly AppDbContext  _db;
    private readonly IPasswordHasher _hasher;
    private readonly IJwtService   _jwt;

    public AuthController(AppDbContext db, IPasswordHasher hasher, IJwtService jwt)
    {
        _db     = db;
        _hasher = hasher;
        _jwt    = jwt;
    }

    [EnableRateLimiting("login")]
    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        var rpe = request.Rpe.ToUpperInvariant().Replace(" ", "");

        var user = await _db.Users
            .FirstOrDefaultAsync(u => u.Rpe == rpe && u.Status == "active");

        // Tiempo constante: siempre verificamos hash aunque el usuario no exista,
        // para no revelar si el RPE existe mediante diferencia de tiempos.
        var hashToVerify = user?.PasswordHash ?? BCrypt.Net.BCrypt.GenerateSalt();
        var valid = user is not null && _hasher.Verify(request.Password, hashToVerify);

        if (!valid)
            return Unauthorized(new { message = "Credenciales incorrectas" });

        var (token, expiry) = _jwt.GenerateToken(user!);
        return Ok(new { token, expiry });
    }
}
