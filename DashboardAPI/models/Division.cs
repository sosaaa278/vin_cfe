namespace DashboardAPI.Models;

public record Division(string Code, string Name);

public static class Divisions
{
    public static readonly IReadOnlyList<Division> All = new Division[]
    {
        new("DA000", "BAJA CALIFORNIA"),
        new("DB000", "NOROESTE"),
        new("DC000", "NORTE"),
        new("DD000", "GOLFO NORTE"),
        new("DF000", "CENTRO OCCIDENTE"),
        new("DG000", "CENTRO SUR"),
        new("DJ000", "ORIENTE"),
        new("DK000", "SURESTE"),
        new("DL000", "VM NORTE"),
        new("DM000", "VM CENTRO"),
        new("DN000", "VM SUR"),
        new("DP000", "BAJIO"),
        new("DU000", "GOLFO CENTRO"),
        new("DV000", "CENTRO ORIENTE"),
        new("DW000", "PENINSULAR"),
        new("DX000", "JALISCO"),
    };

    public static bool IsValid(string code) =>
        All.Any(d => d.Code == code);
}
