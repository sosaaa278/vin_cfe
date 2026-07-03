using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DashboardAPI.Migrations
{
    public partial class FixUsersCreatedAt : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // AddUsers fue generada con SQLite: CreatedAt quedó como TEXT en PostgreSQL.
            // Npgsql no puede leer TEXT → DateTime al materializar entidades User → 500.
            if (migrationBuilder.ActiveProvider == "Npgsql.EntityFrameworkCore.PostgreSQL")
            {
                migrationBuilder.Sql(@"
                    ALTER TABLE ""Users""
                    ALTER COLUMN ""CreatedAt"" TYPE TIMESTAMP WITHOUT TIME ZONE
                    USING ""CreatedAt""::TIMESTAMP WITHOUT TIME ZONE;
                ");
            }
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider == "Npgsql.EntityFrameworkCore.PostgreSQL")
            {
                migrationBuilder.Sql(@"ALTER TABLE ""Users"" ALTER COLUMN ""CreatedAt"" TYPE TEXT USING ""CreatedAt""::TEXT;");
            }
        }
    }
}
