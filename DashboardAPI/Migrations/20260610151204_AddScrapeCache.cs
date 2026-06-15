using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DashboardAPI.Migrations
{
    /// <inheritdoc />
    public partial class AddScrapeCache : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "HechosReportes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Fuente = table.Column<string>(type: "TEXT", nullable: false),
                    FechaConsulta = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Anio = table.Column<int>(type: "INTEGER", nullable: false),
                    Mes = table.Column<int>(type: "INTEGER", nullable: true),
                    ZonaFiltro = table.Column<string>(type: "TEXT", nullable: false),
                    Cve = table.Column<string>(type: "TEXT", nullable: false),
                    Area = table.Column<string>(type: "TEXT", nullable: false),
                    Categoria = table.Column<string>(type: "TEXT", nullable: false),
                    Metrica = table.Column<string>(type: "TEXT", nullable: false),
                    Descripcion = table.Column<string>(type: "TEXT", nullable: false),
                    Valor = table.Column<double>(type: "REAL", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HechosReportes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ScrapeCaches",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Clave = table.Column<string>(type: "TEXT", nullable: false),
                    Json = table.Column<string>(type: "TEXT", nullable: false),
                    FechaGuardadoUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScrapeCaches", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Inconformidades_Fecha_Codigo",
                table: "Inconformidades",
                columns: new[] { "FechaConsulta", "Codigo" });

            migrationBuilder.CreateIndex(
                name: "UX_Inconformidades_Key",
                table: "Inconformidades",
                columns: new[] { "FechaConsulta", "SEC", "AREA", "Codigo" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Hechos_Fuente_Periodo_Zona",
                table: "HechosReportes",
                columns: new[] { "Fuente", "Anio", "Mes", "ZonaFiltro" });

            migrationBuilder.CreateIndex(
                name: "IX_ScrapeCaches_Clave",
                table: "ScrapeCaches",
                column: "Clave",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HechosReportes");

            migrationBuilder.DropTable(
                name: "ScrapeCaches");

            migrationBuilder.DropIndex(
                name: "IX_Inconformidades_Fecha_Codigo",
                table: "Inconformidades");

            migrationBuilder.DropIndex(
                name: "UX_Inconformidades_Key",
                table: "Inconformidades");
        }
    }
}
