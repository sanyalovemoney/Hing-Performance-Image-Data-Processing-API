# Architecture

## Solution layout

```
src/
  ImageProcessing.Core/         contracts + primitives, no third-party deps except Options
    Configuration/              ProcessingOptions + fail-fast validator
    Jobs/                       JobRecord, JobStatus, IJobStore (+ InMemory), JobCancellationRegistry
    Processing/                 IJobProcessor, ProcessorRegistry
    Queue/                      IJobQueue, JobLease (+ bounded Channel implementation)
    Storage/                    IFileStorage (+ TempFileStorage: traversal-safe, pooled I/O)
  ImageProcessing.Processing/   engines that turn uploads into results
    Images/                     ImageSharp-based pipeline; PixelSpanFilters (Span<T> + ArrayPool<T>)
    Csv/                        ref-struct parser (CsvField/CsvFieldEnumerator), streaming reader,
                                zero-allocation aggregation, p95 over a pooled buffer
    Matrix/                     binary format (8-byte LE header + float32 rows), SIMD ops (Vector<float>)
  ImageProcessing.Workers/      JobConsumerWorker (bounded concurrency, timeout, retry, drain),
                                TempCleanupWorker (TTL + orphan sweep)
  ImageProcessing.Api/          Minimal API: upload → 202, status/result/rows/list/delete, healthz/readyz
tests/ImageProcessing.Tests/    unit + integration (WebApplicationFactory), allocation guards
bench/ImageProcessing.Benchmarks/  naive-vs-pooled BenchmarkDotNet suites
```

Dependency rule: `Api → Workers → Processing → Core`. Core knows nothing about
ASP.NET, ImageSharp or channels-of-concrete-type; everything above Core is
swappable behind its interfaces.

## Request lifecycle

```
POST /api/v1/jobs (multipart: file, type, params)
  │  streamed to storage via pooled 80KB buffers — never buffered whole in RAM
  │  hard cap: Processing:MaxUploadBytes → 413 (Kestrel sits 64KB above it)
  ▼
IJobStore.Add(record: Queued)  →  IJobQueue.TryEnqueue(lease)
  │                                   │ bounded Channel<T> (QueueCapacity)
  │  queue full → 503 + Retry-After   │
  ▼                                   ▼
202 Accepted + Location: /api/v1/jobs/{id}/status
                                      │
                        JobConsumerWorker (BackgroundService)
                          SemaphoreSlim gate: ≤ MaxConcurrency jobs at once
                          per-job CTS: JobTimeout budget + client cancel token
                          one IOException retry after 250ms
                                      │
                          processor.ProcessAsync(job, ct)
                          (ImageSharp / span CSV walk / SIMD matrix)
                                      │
                          record → Succeeded | Failed | Canceled
                                      │
GET /status  → JobStatusDto (progress, allocatedBytes, error)
GET /result  → Results.File(stream) — sent from disk, not from a byte[]
GET /rows    → NDJSON via Utf8JsonWriter over Response.BodyWriter (csv only)
DELETE /{id} → cancel (running/queued) or cleanup (terminal) → 202
```

## Backpressure and limits

Three independent valves, all configured in `appsettings.json → Processing`:

1. **Upload cap** (`MaxUploadBytes`) — multipart reading counts bytes into a
   pooled buffer and aborts with `PayloadTooLargeException` the moment the cap
   is crossed → 413 JSON problem.
2. **Bounded queue** (`QueueCapacity`) — `Channel.CreateBounded` with
   `TryEnqueue`; when full the API refuses work with 503 + `Retry-After: 5`
   instead of buffering it. `/readyz` flips to 503 earlier, at
   `QueueHighWatermark` (default 90%), so load balancers shed traffic before
   the queue actually overflows.
3. **Worker gate** (`MaxConcurrency`) — a `SemaphoreSlim` caps simultaneous
   jobs; the read loop parks on it, so queued leases wait in the channel
   rather than in thread-pool work items.

Startup validation (`ProcessingOptionsValidator` + `ValidateOnStart`) crashes
the host on nonsensical limits instead of discovering them at request time.

## Reliability

- **Job timeout**: each job runs under its own `CancellationTokenSource`
  (`JobTimeout`, default 5 min). Overrun → `Failed` with a readable error; the
  slot is freed for the next lease.
- **Transient I/O**: one retry after 250ms on `IOException` (antivirus locks,
  flaky volumes); anything else fails fast.
- **Cooperative cancel**: `DELETE` on a running job signals a
  `JobCancellationRegistry` token linked into the job's CTS → `Canceled`.
- **Graceful shutdown**: `StopAsync` completes the queue writer; the consumer
  keeps draining already-queued leases and gives in-flight jobs
  `ShutdownDrainTimeout` to finish (jobs deliberately ignore the host stopping
  token once started).
- **TTL cleanup**: `TempCleanupWorker` sweeps every `CleanupInterval` (and once
  at boot): terminal jobs past `Retention` lose files + record, stuck
  non-terminal jobs expire by `CreatedAt`, then orphaned storage files older
  than the cutoff are deleted.

## Zero-allocation strategy

- **Upload/result I/O**: pooled 80KB chunk buffers (`ArrayPool<byte>`), streams
  end-to-end; the file never becomes a `byte[]`.
- **CSV**: `CsvFieldEnumerator` is a `ref struct` walking the raw UTF-8 span —
  no `string` per field, no `string[]` per line. Numbers parse straight from
  bytes (`double.TryParse(span, InvariantCulture)`). Streaming NDJSON writes
  through `Utf8JsonWriter` into the response `PipeWriter` with explicit
  flush/newline/reset per record and backpressure at the pipe threshold.
- **Images**: ImageSharp `ProcessPixelRows` exposes `Span<Bgra32>` rows;
  filters mutate in place. Box blur keeps a 3-row window in one pooled buffer
  regardless of image height.
- **Matrix**: `Vector<float>` SIMD with scalar tails; pooled encode/decode
  chunk buffers; result dimensions written straight to the output stream.
- Measured, not assumed: allocation guards in the test suite
  (`GC.GetTotalAllocatedBytes`) and `[MemoryDiagnoser]` benchmarks comparing
  against naive baselines (see `docs/results/gc-report.md`).

## Concurrency model

- API: async Minimal API endpoints, no blocking calls on the request path.
- Worker: one read loop + `Task.Run` per lease behind the semaphore gate;
  in-flight tasks are tracked in a list under a lock and awaited at shutdown.
- Store: `ConcurrentDictionary` + per-record `lock` for mutation; readers get
  immutable snapshots (`record with`), so no torn reads.
- Tests: assembly-wide parallelization disabled because allocation guards use
  process-wide counters.

## Scaling out

Every seam is an interface: `IJobStore` (→ Redis/Postgres), `IJobQueue`
(→ RabbitMQ/Redis Streams), `IFileStorage` (→ S3/Azure Blob). The worker is
stateless beyond the lease, so N replicas scale horizontally once the queue is
external. `/readyz` gives the orchestrator a real saturation signal per
replica.
