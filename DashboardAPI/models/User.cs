namespace DashboardAPI.Models;

public class User
{
    public int      Id           { get; set; }
    public string   Rpe          { get; set; } = "";
    public string   PasswordHash { get; set; } = "";
    public string   DivisionCode { get; set; } = "";
    public string   Role         { get; set; } = "User";
    public string   Status       { get; set; } = "active";
    public DateTime CreatedAt    { get; set; } = DateTime.UtcNow;
}
