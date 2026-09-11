using System.Reflection;
using DummyFileApi.Data;
using DummyFileApi.Generators;
using DummyFileApi.Models;
using DummyFileApi.Options;
using DummyFileApi.Services;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// PaaS hosts (Railway, Heroku, etc.) assign a dynamic port via $PORT rather
// than the ASPNETCORE_URLS/ASPNETCORE_HTTP_PORTS env vars ASP.NET Core reads
// natively.
var port = Environment.GetEnvironmentVariable("PORT");
if (!string.IsNullOrEmpty(port))
{
    builder.WebHost.UseUrls($"http://+:{port}");
}

builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File(
        Path.Combine("logs", "dummyfileapi-.log"),
        rollingInterval: RollingInterval.Day,
        shared: true));

builder.Services.AddControllers()
    .ConfigureApiBehaviorOptions(options =>
    {
        // Binding failures (e.g. seed=abc) are rejected before the action runs;
        // return them as ErrorResponse so every 400 shares one shape.
        options.InvalidModelStateResponseFactory = context =>
        {
            var messages = context.ModelState
                .SelectMany(entry => entry.Value!.Errors, (entry, error) => $"{entry.Key}: {error.ErrorMessage}");
            var message = string.Join(" ", messages);
            return new BadRequestObjectResult(new ErrorResponse(message.Length > 0 ? message : "Invalid request."));
        };
    });
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    // The SDK appends "+<commit>" to the informational version; show just the release number.
    var version = typeof(Program).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0];
    options.SwaggerDoc("v1", new OpenApiInfo { Title = builder.Environment.ApplicationName, Version = version });

    var xmlFile = Path.Combine(AppContext.BaseDirectory, $"{typeof(Program).Assembly.GetName().Name}.xml");
    if (File.Exists(xmlFile))
    {
        options.IncludeXmlComments(xmlFile);
    }
});
builder.Services.Configure<FileGenerationOptions>(
    builder.Configuration.GetSection(FileGenerationOptions.SectionName));
builder.Services.Configure<RateLimitingOptions>(
    builder.Configuration.GetSection(RateLimitingOptions.SectionName));
builder.Services.AddScoped<GenerationRateLimiter>();

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("Default") ?? "Data Source=dummyfileapi.db"));

// Off by default: without a real proxy overwriting it, a client could set
// X-Forwarded-For itself to spoof its ClientId and dodge the rate limit.
// Even then, this trusts the immediate hop with no proxy IP allowlist.
// Reads the flag lazily from builder.Configuration (not a captured bool) so
// config added after this line but before Build() still takes effect.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    if (!builder.Configuration.GetValue<bool>("Proxy:TrustForwardedHeaders"))
    {
        return;
    }

    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

foreach (var (key, impl) in FileGeneratorRegistry.All)
{
    builder.Services.AddKeyedSingleton(typeof(IFileGenerator), key, impl);
}

var app = builder.Build();

// Apply pending migrations on startup so a fresh clone runs with zero manual
// DB setup — fine for a single-instance SQLite deploy, wrong for multi-instance.
using (var scope = app.Services.CreateScope())
{
    scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.Migrate();
}

app.UseForwardedHeaders();
app.UseSerilogRequestLogging();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.Run();

// Exposes the top-level Program for WebApplicationFactory<Program> in integration tests.
public partial class Program;
