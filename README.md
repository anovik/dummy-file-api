# dummy-file-api

ASP.NET Core Web API that generates structurally valid dummy files of an exact requested byte size

# Supported file formats
- TXT
- CSV
- PDF *(in progress)*
- JPEG *(in progress)*
- PNG *(in progress)*

# API endpoints
- `GET /api/files/generate?type=txt&size=100KB&seed=42` — streams a dummy file of the exact requested byte size as a download (`seed` is optional)
- `GET /api/files/types` — lists supported types with MIME type and min/max allowed size
- `GET /api/files/history?page=1&pageSize=20` — paged history of your past generation requests, newest first (clients are identified by IP, no auth)

Size units are binary: `KB` = 1024 bytes, `MB` = 1024² bytes (`KiB`/`MiB` are accepted as aliases). Decimal values like `1.5MB` work too.

```bash
curl -OJ "http://localhost:5119/api/files/generate?type=txt&size=100KB"
```

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
