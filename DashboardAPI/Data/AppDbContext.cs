using DashboardAPI.Models;
using Microsoft.EntityFrameworkCore;

namespace DashboardAPI.Data
{
    public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

        public DbSet<Inconformidad> Inconformidades { get; set; }
        public DbSet<HechoReporte>  HechosReportes  { get; set; }
        public DbSet<ScrapeCache>   ScrapeCaches    { get; set; }
        public DbSet<User>          Users           { get; set; }
        
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Inconformidad>(entity =>
            {
                // Índice compuesto para las consultas por rango de fechas + Codigo en Compare()
                entity.HasIndex(x => new { x.FechaConsulta, x.Codigo })
                      .HasDatabaseName("IX_Inconformidades_Fecha_Codigo");

                // Índice compuesto para detectar duplicados en PersistRowsAsync()
                entity.HasIndex(x => new { x.FechaConsulta, x.SEC, x.AREA, x.Codigo })
                      .IsUnique()
                      .HasDatabaseName("UX_Inconformidades_Key");
            });

            modelBuilder.Entity<HechoReporte>(entity =>
            {
                // Índice para reemplazar/consultar el snapshot de una fuente/periodo/zona
                entity.HasIndex(x => new { x.Fuente, x.Anio, x.Mes, x.ZonaFiltro })
                      .HasDatabaseName("IX_Hechos_Fuente_Periodo_Zona");
            });

            modelBuilder.Entity<ScrapeCache>()
                .HasIndex(x => x.Clave).IsUnique();

            modelBuilder.Entity<User>(entity =>
            {
                entity.HasIndex(x => x.Rpe)
                      .IsUnique()
                      .HasDatabaseName("UX_Users_Rpe");
            });
        }
    }
}
