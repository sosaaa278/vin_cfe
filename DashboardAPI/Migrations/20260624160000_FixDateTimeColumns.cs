using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DashboardAPI.Migrations
{
    public partial class FixDateTimeColumns : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Las migraciones anteriores usaron type:"TEXT" (SQLite). En PostgreSQL
            // las columnas quedaron como TEXT, lo que impide comparaciones con DateTime.
            if (migrationBuilder.ActiveProvider == "Npgsql.EntityFrameworkCore.PostgreSQL")
            {
                migrationBuilder.Sql(@"
                    ALTER TABLE ""Inconformidades""
                    ALTER COLUMN ""FechaConsulta"" TYPE TIMESTAMP WITHOUT TIME ZONE
                    USING ""FechaConsulta""::TIMESTAMP WITHOUT TIME ZONE;
                ");

                migrationBuilder.Sql(@"
                    ALTER TABLE ""HechosReportes""
                    ALTER COLUMN ""FechaConsulta"" TYPE TIMESTAMP WITHOUT TIME ZONE
                    USING ""FechaConsulta""::TIMESTAMP WITHOUT TIME ZONE;
                ");

                migrationBuilder.Sql(@"
                    ALTER TABLE ""ScrapeCaches""
                    ALTER COLUMN ""FechaGuardadoUtc"" TYPE TIMESTAMP WITHOUT TIME ZONE
                    USING ""FechaGuardadoUtc""::TIMESTAMP WITHOUT TIME ZONE;
                ");
            }
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider == "Npgsql.EntityFrameworkCore.PostgreSQL")
            {
                migrationBuilder.Sql(@"ALTER TABLE ""Inconformidades"" ALTER COLUMN ""FechaConsulta"" TYPE TEXT USING ""FechaConsulta""::TEXT;");
                migrationBuilder.Sql(@"ALTER TABLE ""HechosReportes"" ALTER COLUMN ""FechaConsulta"" TYPE TEXT USING ""FechaConsulta""::TEXT;");
                migrationBuilder.Sql(@"ALTER TABLE ""ScrapeCaches"" ALTER COLUMN ""FechaGuardadoUtc"" TYPE TEXT USING ""FechaGuardadoUtc""::TEXT;");
            }
        }
    }
}
