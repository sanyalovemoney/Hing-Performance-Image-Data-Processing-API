# High-Performance Image/Data Processing API

An ASP.NET Core 8 (.NET 8) microservice that accepts large file uploads,
processes them in background workers, and streams results back — built to
demonstrate allocation-conscious C#: `Span<T>`/`ref struct` parsing,
`ArrayPool<T>` chunked I/O, SIMD matrix ops, bounded `Channel<T>` queues,
backpressure (413/503/`/readyz`), cooperative cancellation, and `IAsyncEnumerable`
NDJSON streaming.

```
POST upload ──▶ 202 Accepted ──▶ bounded queue ──▶ worker pool (≤N, timeouts, retry)
                                        │ full                  │
                                     503 + Retry-After    Succeeded / Failed / Canceled
GET /status  poll job state (progress, allocatedBytes, error)
GET /result  stream the artifact straight from disk
GET /rows    CSV → NDJSON, streamed with backpressure
```

## Features

- **Streaming multipart uploads** — pooled 80KB buffers; the file never lands
  in a `byte[]`. Hard cap → `413` JSON problem (Kestrel limit sits 64KB above
  the app cap so the JSON error wins).
- **Three processing engines**
  - `image` — grayscale / threshold / box-blur over ImageSharp row spans
    (`PixelSpanFilters`), blur keeps a pooled 3-row window;
  - `csv` — ref-struct UTF-8 parser (no strings per field), aggregates
    `sum|avg|min|max|count|p95` with optional `groupBy`, streamed
    row-by-row as NDJSON via `Utf8JsonWriter` into the response `PipeWriter`;
  - `matrix` — binary format (8-byte LE header + float32 rows), SIMD
    (`Vector<float>`) `scale|normalize|transpose|colmean`.
- **Bounded queue + worker gate** — `Channel<T>` (capacity 128) and a
  `SemaphoreSlim` (concurrency 2); saturation → `503` + `Retry-After`, and
  `/readyz` flips at the 90% high-watermark before the queue overflows.
- **Reliability** — per-job timeout CTS, one IOException retry, cooperative
  `DELETE` cancellation (running → `Canceled`, queued → skipped, terminal →
  files+record removed), graceful shutdown drain, TTL cleanup worker with
  orphan sweep.
- **Fail-fast config** — every limit in `appsettings.json → Processing`,
  validated at startup (`ValidateOnStart`).

## Quickstart

```bash
git clone https://github.com/sanyalovemoney/Hing-Performance-Image-Data-Processing-API.git
cd Hing-Performance-Image-Data-Processing-API
dotnet run --project src/ImageProcessing.Api        # http://localhost:5000 (or see launchSettings)
```

```bash
# grayscale a PNG
curl -F "file=@photo.png" -F "type=image" -F 'params={"filter":"grayscale"}' \
     http://localhost:5000/api/v1/jobs
# → 202 {"id":"...","status":"Queued",...}

curl http://localhost:5000/api/v1/jobs/<id>/status
curl -o out.png http://localhost:5000/api/v1/jobs/<id>/result

# aggregate a CSV column, then stream its rows as NDJSON
curl -F "file=@data.csv" -F "type=csv" -F 'params={"column":1,"aggregate":"p95","groupBy":0}' \
     http://localhost:5000/api/v1/jobs
curl http://localhost:5000/api/v1/jobs/<id>/rows
```

### Docker

```bash
docker build -t imageprocessing-api .
docker run --rm -p 8080:8080 -v imgproc-storage:/app/storage imageprocessing-api
curl http://localhost:8080/healthz && curl http://localhost:8080/readyz
```

Multi-stage build (sdk:8.0 → aspnet:8.0), non-root `appuser`, server GC,
`EXPOSE 8080`, storage on a volume.

## API

| Method | Path | Purpose | Codes |
|---|---|---|---|
| POST | `/api/v1/jobs` | multipart: `file`, `type=image\|csv\|matrix\|echo`, `params` (JSON) | 202 + `Location`; 400; 413; 503 |
| GET | `/api/v1/jobs` | paginated list `?limit=&offset=`, newest first | 200 |
| GET | `/api/v1/jobs/{id}/status` | `{id,type,status,progress,allocatedBytes,error,...}` | 200; 404 |
| GET | `/api/v1/jobs/{id}/result` | stream artifact from disk | 200; 404; 409 not ready |
| GET | `/api/v1/jobs/{id}/rows` | CSV → NDJSON stream (`IAsyncEnumerable`) | 200; 404; 409 non-csv |
| DELETE | `/api/v1/jobs/{id}` | cancel (running/queued) or cleanup (terminal) | 202; 404 |
| GET | `/healthz` | liveness | 200 |
| GET | `/readyz` | readiness — 503 at queue high-watermark | 200; 503 |

## Configuration (`appsettings.json → Processing`)

| Key | Default | Meaning |
|---|---|---|
| `MaxUploadBytes` | 52428800 (50MB) | per-upload hard cap → 413 |
| `QueueCapacity` | 128 | bounded queue size → 503 when full |
| `MaxConcurrency` | 2 | simultaneous jobs in the worker |
| `JobTimeout` | 00:05:00 | per-job budget → Failed "timed out" |
| `Retention` | 24:00:00 | TTL for finished/stuck jobs + orphan files |
| `CleanupInterval` | 00:15:00 | sweep period of the cleanup worker |
| `QueueHighWatermark` | 0.9 | `/readyz` 503 threshold (fraction of capacity) |
| `ShutdownDrainTimeout` | 00:00:30 | graceful drain budget on shutdown |

Bad values crash the host at startup (`ProcessingOptionsValidator` + `ValidateOnStart`).

## Benchmarks (measured)

BenchmarkDotNet 0.15.0, .NET 8.0.31, Windows 11, X64 RyuJIT AVX2,
`LaunchCount=1 WarmupCount=2 IterationCount=6`. Full tables + methodology:
[`docs/results/gc-report.md`](docs/results/gc-report.md).

| Workload | Naive | Span/Pooled | Time | Gen0 | Allocated |
|---|---:|---:|---:|---:|---:|
| Grayscale 512×512 | 726.5 μs, 2.12 MB | **561.4 μs, 688 B** | ×0.77 | 506.8 → **0** | −99.97% |
| Box blur 512×512 | 5,666 μs, 5.29 MB | **5,552 μs, 8 B** | ×0.98 | 1,336 (+Gen1/2) → **0** | −100% |
| CSV scan 50k rows | 15.88 ms, 13.79 MB | **4.71 ms, 3 B** | **×0.30** | 2,469 (+Gen1/2) → **0** | −100% |

DoD (workflow §8): Gen0 reduction ≥ 80% — **met at 100% on all three**, and
enforced continuously by allocation guards in the test suite.

## Project layout

```
src/ImageProcessing.Core         contracts: options, job store, queue, storage, cancellation
src/ImageProcessing.Processing   engines: Images (ImageSharp+Span), Csv (ref struct), Matrix (SIMD)
src/ImageProcessing.Workers      consumer (gate/timeout/retry/drain) + TTL cleanup
src/ImageProcessing.Api          Minimal API endpoints, streaming multipart reader
tests/ImageProcessing.Tests      155 tests: unit + WebApplicationFactory integration
bench/ImageProcessing.Benchmarks naive-vs-pooled suites ([MemoryDiagnoser])
docs/architecture.md             design notes, scaling seams
workflow.md                      original project spec (UA)
```

## Development

```bash
dotnet build                     # warnings are errors (Directory.Build.props)
dotnet test                      # 155 tests, parallelization off (allocation guards)
dotnet run --project bench/ImageProcessing.Benchmarks -c Release
```

CI (GitHub Actions): build + test on push/PR; benchmarks weekly and on
`workflow_dispatch`, results uploaded as artifacts.
