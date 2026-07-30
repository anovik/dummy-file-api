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

Requested size must be between the type's minimum (from `/api/files/types`) and a configurable max, `100MB` by default (`FileGeneration:MaxSizeBytes`) — outside that range returns `400`.

`/api/files/generate` is rate-limited per client IP: a sliding 1-hour window, `100` requests by default (`RateLimiting:MaxPerHour`). Over the limit returns `429` with a `Retry-After` header (seconds until the oldest counted request ages out of the window).

If deployed behind a reverse proxy (e.g. a PaaS host that terminates TLS in front of the app), set `Proxy:TrustForwardedHeaders` to `true` so client IPs (used for history and rate limiting) come from `X-Forwarded-For` instead of the proxy's own address. Leaving it `false` while actually behind a proxy pools every real client into the proxy's single IP, sharing one rate-limit bucket and history. Leave it `false` (the default) when running directly — trusting that header without an actual proxy in front lets a client spoof its IP to bypass the rate limit; even with a proxy, this app trusts whatever the immediate hop sends with no proxy IP allowlist, so it's only appropriate when the app isn't also reachable directly around that proxy.

# How exact sizes are achieved

Every generator hits the requested byte count exactly while staying a structurally valid, openable file. The technique differs per format:

- **txt** — deterministic filler text written in bounded chunks, with the final chunk truncated to land on the exact byte count.
- **csv** — full deterministic rows (fixed columns, CRLF line endings) until less than one more full row remains, then the **last row's final field** is a variable-length ASCII filler sized to consume the remaining bytes exactly. Plain ASCII with no embedded commas/quotes/newlines means any size at or above the minimum is achievable.
- **pdf** — a minimal valid PDF (Catalog → Pages → Page) with a **dedicated padding object placed last**, just before the xref table, as a legal unreferenced-but-well-formed indirect object. Its own `/Length N` is printed as decimal digits inside the file, so a digit-count change (e.g. `9999` → `10000`) shifts the total size — the generator computes a candidate `N`, builds, checks the actual size against the target, and adjusts by the delta until it converges (1-2 iterations).
- **png** — an RGBA checkerboard scaled to the requested size, with IDAT written as **hand-rolled "stored" (uncompressed) deflate blocks** — never `DeflateStream`/`ZlibStream`, since the BCL doesn't guarantee byte-stable stored-block output across versions. Because stored-block length depends only on pixel dimensions, not pixel content, the canvas size can be chosen by binary search and the remainder padded exactly with a single `tEXt` chunk (PNG chunk lengths are a 32-bit field, so one chunk always suffices).
- **jpeg** — the same checkerboard as `png`, drawn as a real baseline JPEG on whole 8×8 blocks so every block is uniform (all AC coefficients zero, so the entropy-coded scan is just DC diffs and end-of-block codes). Its exact byte length — including 0xFF bit-stuffing — is measured with a counting pre-pass through the same bit writer that later streams it, since JPEG's entropy coding is otherwise content-dependent and can't be predicted analytically. Canvas size is chosen by binary search on the measured size; fine padding uses COM marker segments (a 16-bit length field, ~65,533 bytes per segment, chained back to back past that cap).

All three binary generators are hand-rolled byte writers rather than built on an imaging/PDF library, since general-purpose encoders don't expose an exact-byte-count knob — the padding mechanism itself is the project's central teaching point.

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
