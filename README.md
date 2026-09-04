# dummy-file-api

ASP.NET Core Web API that generates structurally valid dummy files of an exact requested byte size

**Live demo:** https://dummy-file-api-production.up.railway.app

1. See supported types and their size limits: [`/api/files/types`](https://dummy-file-api-production.up.railway.app/api/files/types)
2. Download a generated file: [`/api/files/generate?type=txt&size=10KB`](https://dummy-file-api-production.up.railway.app/api/files/generate?type=txt&size=10KB)
3. See it recorded in your history: [`/api/files/history`](https://dummy-file-api-production.up.railway.app/api/files/history)

# Supported file formats
- TXT
- CSV
- PDF
- JPEG
- PNG
- ZIP
- DOCX
- XLSX
- JSON

# API endpoints
- `GET /api/files/generate?type=txt&size=100KB&seed=42` — streams a dummy file of the exact requested byte size as a download (`seed` is optional)
- `GET /api/files/types` — lists supported types with MIME type and min/max allowed size
- `GET /api/files/history?page=1&pageSize=20` — paged history of your past generation requests, newest first (clients are identified by IP, no auth)

Size units are binary: `KB` = 1024 bytes, `MB` = 1024² bytes (`KiB`/`MiB` are accepted as aliases). Decimal values like `1.5MB` work too.

`seed`'s effect depends on the type:
- `png` / `jpeg` / `pdf` — picks the checkerboard fill color from a fixed palette (omit for the first color)
- `zip` / `docx` — picks the filler phrase from a fixed set (omit for the first); changes the content bytes, not the size
- `csv` / `xlsx` / `json` — sets the starting row/record `Id`, counting up from there (omit, or pass a non-positive
  value, for 1; a huge seed combined with a near-minimum size also falls back to 1, since the requested size can't
  fit that many Id digits)
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
- **zip** — a single STORE-method (uncompressed) entry (`readme.txt`) of deterministic filler text, written by a small hand-rolled `ZipWriter` that streams each local header + entry data as it goes and keeps only the tiny central-directory metadata in memory. Every ZIP header field except the entry content is fixed-width binary, so the content length solves for the exact target in one step — `target − overhead`, where `overhead` is the local header + central-directory entry + end-of-central-directory record for the fixed filename. No padding container and no digit-width iteration.
- **docx** — an OOXML package: the same `ZipWriter` storing the six minimal Word parts (`[Content_Types].xml`, the two `.rels` parts, `word/document.xml`, `docProps/core.xml`, `docProps/app.xml`). Every part is fixed except one filler paragraph in `word/document.xml` whose `<w:t>` text (safe ASCII, no XML-escaping) is sized to consume the exact remainder — the same one-step solve as `zip`, with the ZIP overhead plus the fixed XML now the constant.
- **xlsx** — an OOXML package: the same `ZipWriter` storing seven minimal spreadsheet parts (`[Content_Types].xml`, the two `.rels` parts, `xl/workbook.xml`, `xl/worksheets/sheet1.xml`, `docProps/core.xml`, `docProps/app.xml`), no `styles.xml`/`sharedStrings.xml`. Only the rows in `sheet1.xml` vary: a header row, then numeric `<row>`s counting up from the `seed` Id, then — because each row's digit width drifts as the numbers grow — a **final row whose inline-string cell** is sized to consume the exact remainder (`csv`'s last-field technique). The sheet is generated once to compute its CRC for the ZIP local header, then again to stream it.
- **json** — `{"meta":{"count":N},"records":[{"id":K,"value":"..."}, ...]}`. Full records with short `"item-K"` values are written until less than one more fits, then the **final record's `value` string** is sized to land on the exact byte count (`csv`'s last-field technique again; the filler is plain ASCII so no JSON escaping perturbs the length). `count` is the record total, and its own printed digits are part of the file, so — like `pdf`'s `/Length` — the record count is settled by a short fixed-point pass (widen the reserved digits until the simulated count fits) before streaming; any leftover width the final record absorbs.

The image and PDF generators are hand-rolled byte writers rather than built on an imaging/PDF library, since general-purpose encoders don't expose an exact-byte-count knob — the padding mechanism itself is the project's central teaching point. `ZipWriter` is likewise hand-rolled, and is reused for the OOXML formats (a `.docx`/`.xlsx` is a ZIP of fixed XML parts).

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
- [DocumentFormat.OpenXml](https://github.com/dotnet/Open-XML-SDK) (MIT) — independent reader + schema validator for generated DOCX and XLSX files
- `System.IO.Compression.ZipArchive` (BCL) — independent reader validating generated ZIP, DOCX, and XLSX containers (generation never uses it)

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

# Deployment

A multi-stage `Dockerfile` at the repo root (`sdk:10.0` build stage → `aspnet:10.0` runtime stage) builds a self-contained image. The live demo runs on [Railway](https://railway.app), deployed straight from this GitHub repo. To reproduce:

1. Connect the repo to a new Railway service — it detects and builds the `Dockerfile` automatically.
2. Attach a persistent volume (e.g. mounted at `/data`) so the SQLite database survives redeploys; the container filesystem is otherwise wiped on every deploy.
3. Set these environment variables:
   - `ConnectionStrings__Default` = `Data Source=/data/dummyfileapi.db` (pointing at the mounted volume)
   - `Proxy__TrustForwardedHeaders` = `true` (Railway terminates TLS and proxies every request)
   - `ASPNETCORE_ENVIRONMENT` = `Production`
4. Generate a public domain for the service.

The app reads a `PORT` env var (set automatically by Railway and most similar PaaS hosts) and binds Kestrel to it, since ASP.NET Core doesn't read that variable natively.
