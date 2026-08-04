    using System.Text;
using System.Threading.RateLimiting;
using DashboardAPI.Data;
using DashboardAPI.Models;
using DashboardAPI.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;

// Permite enviar DateTime con Kind=Unspecified a PostgreSQL (columnas timestamptz).
// Sin esto, Npgsql 6+ rechaza cualquier DateTime que no sea explícitamente UTC.
AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

// Carga los secretos desde el archivo .env (JWT y usuarios) antes de construir
// el host, para que sobreescriban appsettings.json vía variables de entorno.
DashboardAPI.DotEnv.Load();

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseWindowsService(); // Permite correr como Windows Service

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();

// ✅ CORRECCIÓN: Registrar SOLO como Scoped.
// Esto asegura que cada petición HTTP tenga su propia instancia de MetaRealService
// y su propio navegador Playwright, evitando conflictos de concurrencia.
builder.Services.AddScoped<MetaRealService>(); 

// Swagger con soporte de Bearer token
builder.Services.AddSwaggerGen(c =>
{
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name         = "Authorization",
        Type         = SecuritySchemeType.Http,
        Scheme       = "bearer",
        BearerFormat = "JWT",
        In           = ParameterLocation.Header
    });
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" }
            },
            Array.Empty<string>()
        }
    });
});

builder.Services.AddHttpClient();
builder.Services.AddScoped<WebScraperService>();
builder.Services.AddScoped<ReporteStore>();
builder.Services.AddScoped<SisquemService>();
builder.Services.AddSingleton<FullCompareService>();
builder.Services.AddSingleton<IPasswordHasher, BcryptPasswordHasher>();
builder.Services.AddSingleton<IJwtService, JwtService>();
builder.Services.AddSingleton<EmailReportService>();
builder.Services.AddHostedService<DailyReportBackgroundService>();

// Configuración de opciones MetaReal
builder.Services.Configure<DashboardAPI.Models.MetaRealOptions>(builder.Configuration.GetSection("MetaReal"));
builder.Services.AddSingleton(sp => sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<DashboardAPI.Models.MetaRealOptions>>().Value);

// CORS
// Rate limiting: máx 5 intentos de login por IP por minuto
builder.Services.AddRateLimiter(options =>
{
    options.AddFixedWindowLimiter("login", opt =>
    {
        opt.PermitLimit          = 5;
        opt.Window               = TimeSpan.FromMinutes(1);
        opt.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
        opt.QueueLimit           = 0;
    });
    options.RejectionStatusCode = 429;
});

var allowedOrigins = builder.Configuration
    .GetSection("Cors:AllowedOrigins")
    .Get<string[]>() ?? ["http://localhost:4200"];

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAngular",
        policy =>
        {
            policy.WithOrigins(allowedOrigins)
                  .AllowAnyHeader()
                  .AllowAnyMethod();
        });
});

// Database
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
if (!string.IsNullOrEmpty(connectionString))
{
    builder.Services.AddDbContext<AppDbContext>(options =>
        options.UseNpgsql(connectionString)
               .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning)));
}
else
{
    builder.Services.AddDbContext<AppDbContext>(options =>
        options.UseSqlite("Data Source=inconformidades.db")
               .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning)));
}

// JWT Authentication
var jwtSettings = builder.Configuration.GetSection("Jwt").Get<JwtSettings>()!;
jwtSettings.Validate(); // truena al arranque si Key falta o mide < 32 chars

builder.Services.AddSingleton(jwtSettings);

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer           = true,
            ValidateAudience         = true,
            ValidateLifetime         = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer              = jwtSettings.Issuer,
            ValidAudience            = jwtSettings.Audience,
            IssuerSigningKey         = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSettings.Key)),
            ClockSkew                = TimeSpan.FromSeconds(30),
        };
    });

builder.Services.AddAuthorization();

var app = builder.Build();

// Cabeceras de seguridad (anti-XSS, anti-clickjacking, etc.)
app.Use(async (ctx, next) =>
{
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    ctx.Response.Headers["X-Frame-Options"]        = "DENY";
    ctx.Response.Headers["X-XSS-Protection"]       = "1; mode=block";
    ctx.Response.Headers["Referrer-Policy"]        = "strict-origin-when-cross-origin";
    ctx.Response.Headers["Permissions-Policy"]     = "camera=(), microphone=(), geolocation=()";
    ctx.Response.Headers.Remove("Server");
    await next();
});

app.UseCors("AllowAngular");

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

// Servir Angular como archivos estáticos (deploy Opción A: servidor único).
// Si el frontend se despliega aparte (Opción B), wwwroot/index.html no existe y se omite.
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapControllers();
if (File.Exists(Path.Combine(app.Environment.WebRootPath ?? "wwwroot", "index.html")))
{
    app.MapFallbackToFile("index.html"); // Para que el router de Angular funcione al hacer F5
}

// Seed Database
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

    RegisterExistingTablesAsMigrated(db);
    db.Database.Migrate();

    if (app.Environment.IsDevelopment())
    {
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
        await SeedData.SeedUsersAsync(db, hasher);
    }

    // Eliminar toda caché de meta-real (clave exacta + por división) para forzar re-scrape limpio.
    var oldMetaCache = db.ScrapeCaches.Where(c => c.Clave == "meta-real" || c.Clave.StartsWith("meta-real-"));
    db.ScrapeCaches.RemoveRange(oldMetaCache);
    await db.SaveChangesAsync();
}

static void RegisterExistingTablesAsMigrated(AppDbContext db)
{
    // Solo aplica a PostgreSQL; SQLite siempre parte de una BD vacía
    if (db.Database.ProviderName != "Npgsql.EntityFrameworkCore.PostgreSQL") return;

    // Tabla → migración que la crea
    var migrationMap = new[]
    {
        ("Inconformidades",  "20260521180804_InitialCreate"),
        ("ScrapeCaches",     "20260610151204_AddScrapeCache"),
        ("HechosReportes",   "20260623185323_ActualizacionModelo"),
    };

    var conn = db.Database.GetDbConnection();
    conn.Open();

    using var cmdCreate = conn.CreateCommand();
    cmdCreate.CommandText = @"
        CREATE TABLE IF NOT EXISTS ""__EFMigrationsHistory"" (
            ""MigrationId""    character varying(150) NOT NULL,
            ""ProductVersion"" character varying(32)  NOT NULL,
            CONSTRAINT ""PK___EFMigrationsHistory"" PRIMARY KEY (""MigrationId"")
        )";
    cmdCreate.ExecuteNonQuery();

    foreach (var (table, migrationId) in migrationMap)
    {
        using var cmdCheck = conn.CreateCommand();
        cmdCheck.CommandText = $@"SELECT COUNT(*) FROM ""__EFMigrationsHistory"" WHERE ""MigrationId"" = '{migrationId}'";
        var alreadyRecorded = Convert.ToInt64(cmdCheck.ExecuteScalar()) > 0;
        if (alreadyRecorded) continue;

        using var cmdTable = conn.CreateCommand();
        cmdTable.CommandText = $@"SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = 'public' AND table_name = '{table}'";
        var tableExists = Convert.ToInt64(cmdTable.ExecuteScalar()) > 0;

        if (tableExists)
        {
            using var cmdInsert = conn.CreateCommand();
            cmdInsert.CommandText = $@"INSERT INTO ""__EFMigrationsHistory"" (""MigrationId"", ""ProductVersion"") VALUES ('{migrationId}', '9.0.0')";
            cmdInsert.ExecuteNonQuery();
        }
    }
}

// Execution
var port = Environment.GetEnvironmentVariable("PORT") ?? "5111";
app.Run($"http://0.0.0.0:{port}");