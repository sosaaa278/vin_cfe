using DashboardAPI.Models;

namespace DashboardAPI.Services;

public interface IJwtService
{
    (string token, DateTime expiry) GenerateToken(User user);
}
