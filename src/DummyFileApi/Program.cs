using DummyFileApi.Generators;
using DummyFileApi.Options;
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
builder.Services.AddSwaggerGen();
builder.Services.Configure<FileGenerationOptions>(
    builder.Configuration.GetSection(FileGenerationOptions.SectionName));

foreach (var (key, impl) in FileGeneratorRegistry.All)
{
    builder.Services.AddKeyedSingleton(typeof(IFileGenerator), key, impl);
}

var app = builder.Build();

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
