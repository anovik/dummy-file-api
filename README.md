# dummy-file-api

ASP.NET Core Web API that generates structurally valid dummy files of an exact requested byte size

# Supported file formats
- TXT
- CSV
- PDF
- JPEG
- PNG

# API endpoints
- `GET /api/files/generate?type=txt&size=100KB&seed=42` — streams a dummy file of the exact requested byte size as a download (`seed` is optional)
- `GET /api/files/types` — lists supported types with MIME type and min/max allowed size
- `GET /api/files/history?page=1&pageSize=20` — paged history of your past generation requests, newest first (clients are identified by IP, no auth)

Size units are binary: `KB` = 1024 bytes, `MB` = 1024² bytes (`KiB`/`MiB` are accepted as aliases). Decimal values like `1.5MB` work too.

`seed`'s effect depends on the type:
- `png` / `jpeg` / `pdf` — picks the checkerboard fill color from a fixed palette (omit for the first color)
- `csv` — sets the starting row `Id`, rows counting up from there (omit, or pass a non-positive value, for 1;
  a huge seed combined with a near-minimum size also falls back to 1, since the requested size can't fit
  that many Id digits)
- `txt` — ignored; exact-size filler text needs no seed-driven variation

```bash
curl -OJ "http://localhost:5119/api/files/generate?type=txt&size=100KB"
```

# Libraries used

**API** (`src/DummyFileApi`):
- [Serilog](https://serilog.net/) — structured logging (console + rolling file) and request timing
- [Swashbuckle](https://github.com/domaindrivendev/Swashbuckle.AspNetCore) — Swagger/OpenAPI UI (Development only)
- [EF Core + SQLite](https://learn.microsoft.com/ef/core/) — persistence for the generation history
- [System.IO.Hashing](https://www.nuget.org/packages/System.IO.Hashing) — CRC-32 for PNG chunk checksums

File generation itself uses no imaging or document libraries by design: generators hand-write each format's bytes, since general-purpose encoders don't expose an exact-byte-count knob.

**Tests** (`src/DummyFileApi.Tests`):
- [xUnit](https://xunit.net/) — test framework, with [coverlet](https://github.com/coverlet-coverage/coverlet) for coverage
- [CsvHelper](https://joshclose.github.io/CsvHelper/) — independent parser validating generated CSVs
- [SixLabors.ImageSharp](https://sixlabors.com/products/imagesharp/) (3.1.x, split license) — independent decoder validating generated images
- [PdfPig](https://github.com/UglyToad/PdfPig) (Apache 2.0) — independent parser validating generated PDFs

# Requirements
- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- Visual Studio 2026 (or any editor with .NET 10 support, e.g. VS Code + C# Dev Kit, Rider)

# Build & run
```bash
# Build the whole solution
dotnet build

# Run the API locally
dotnet run --project src/DummyFileApi

# Run the test suite
dotnet test

# Run a single test class/method
dotnet test --filter FullyQualifiedName~ClassName
```

In Development, Swagger UI is available at `/swagger`.

# Persistence
Generation requests are recorded in a local SQLite database (`dummyfileapi.db` next to the app), created and migrated automatically on startup — no manual DB setup needed. The path is configurable via the `ConnectionStrings:Default` setting.

To work with EF Core migrations, restore the repo-local `dotnet-ef` tool first:
```bash
dotnet tool restore
dotnet ef migrations add <MigrationName> --project src/DummyFileApi -o Data/Migrations
```
