using DummyFileApi.Data;
using DummyFileApi.Generators;
using DummyFileApi.Options;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File(
        Path.Combine("logs", "dummyfileapi-.log"),
        rollingInterval: RollingInterval.Day));

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    var xmlFile = Path.Combine(AppContext.BaseDirectory, $"{typeof(Program).Assembly.GetName().Name}.xml");
    if (File.Exists(xmlFile))
    {
        options.IncludeXmlComments(xmlFile);
    }
});
builder.Services.Configure<FileGenerationOptions>(
    builder.Configuration.GetSection(FileGenerationOptions.SectionName));

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("Default") ?? "Data Source=dummyfileapi.db"));

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    // ClientId only drives history and rate-limit fairness, not security, so
    // trusting X-Forwarded-For without a proxy allowlist is acceptable here.
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
