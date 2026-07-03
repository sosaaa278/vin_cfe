namespace DashboardAPI.Services;

public class BcryptPasswordHasher : IPasswordHasher
{
    private const int WorkFactor = 12;

    public string Hash(string password) =>
        BCrypt.Net.BCrypt.EnhancedHashPassword(password, WorkFactor);

    public bool Verify(string password, string hash) =>
        BCrypt.Net.BCrypt.EnhancedVerify(password, hash);
}
