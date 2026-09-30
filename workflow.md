# Workflow — High-Performance Image/Data Processing API

> Мікросервіс на C#/.NET, який приймає великі файли (зображення, CSV, матриці), обробляє їх у фоновому режимі й віддає результат потоково.
> Головна мета — показати роботу з пам'яттю (`Span<T>`, `Memory<T>`, `ArrayPool<T>`, `ref struct`) та коректну асинхронність (`Channel<T>`, `BackgroundService`, streaming).

---

## 1. Стек

| Шар | Технологія | Навіщо |
|---|---|---|
| Runtime | **.NET 8 LTS** (або .NET 10 LTS) | base для всього |
| Web | **ASP.NET Core Minimal API** | контролери не потрібні, менше бойлерплейту |
| Черга задач | **`System.Threading.Channels.Channel<T>`** (bounded) | in-memory async queue + backpressure |
| Фонові воркери | **`BackgroundService` (IHostedService)** | споживання черги, життєвий цикл керований хостом |
| Обмеження конкурентності | **`SemaphoreSlim`** + bounded channel | сервіс не падає від піку навантаження |
| Зображення | **SixLabors.ImageSharp** | pure-managed, API побудоване на `Span<T>`/`ref struct` — ідеально для zero-allocation фільтрів |
| CSV | **CsvHelper** + власний `Span<T>`-парсер для benchmarks | парсинг без аллокацій |
| Pooled буфери | **`ArrayPool<byte>.Shared`**, `Microsoft.IO.RecyclableMemoryStream` | перевикористання буферів, менше Gen0 |
| Конфігурація | `appsettings.json` + **Options pattern** (`IOptions<ProcessingOptions>`) | ліміти без перекомпіляції |
| Логування | `ILogger` (Serilog — опційно) | structured logs із JobId |
| Docs/OpenAPI | built-in OpenAPI + Swagger UI | контракт API |
| Тести | **xUnit** + `FluentAssertions` | unit-тести обробки та черги |
| Benchmarks | **BenchmarkDotNet** (`[MemoryDiagnoser]`) | доказ "−80% GC" для резюме |
| Metrics | `GC.GetTotalAllocatedBytes`, EventCounters (`dotnet counters`) | аллокації per job, collected Gen0/1/2 |
| Опційно (Phase 2) | **RabbitMQ** або **Redis (StackExchange.Redis)** | зовнішня черга, горизонтальне масштабування |
| Деплой | **Docker** (multi-stage), `dotnet publish` | портфоліо-проєкт має запускатися однією командою |

---

## 2. Архітектура (потік даних)

```
Client
  │  POST /api/v1/jobs  (multipart: file + type + params)
  ▼
ASP.NET Core Minimal API
  │  1. валідація (тип, розмір ≤ MaxUploadBytes)
  │  2. стрімінговий збереження file → temp storage (НЕ в RAM цілком)
  │  3. JobRecord { Id=Guid, Status=Queued } → IJobStore
  │  4. channel.Writer.TryWrite(jobId)
  │  5. одразу 202 Accepted + Location: /api/v1/jobs/{id}/status
  ▼
Channel<JobLease> (bounded, FullMode.Wait)   ← backpressure: черга повна → 503
  ▼
ProcessingConsumer : BackgroundService
  │  await foreach (lease in reader.ReadAllAsync(ct))
  │  semaphore.WaitAsync() → task = ProcessAsync(lease)
  ▼
Processor (Image / Csv / Matrix)
  │  ArrayPool rent → Span<T> / ref struct обробка → Write результат у pooled stream
  ▼
temp storage: /results/{id}.out, status → Succeeded|Failed
  ▲
GET /api/v1/jobs/{id}/result   → Results.File(stream) / CopyToAsync — стрімінг на клієнта, файл НЕ завантажується в RAM
GET /api/v1/jobs/{id}/status   → JSON {status, progress}
GET /api/v1/jobs/{id}/rows     → IAsyncEnumerable<Row> (CSV) як NDJSON-стрім
```

Принципи:
- API **ніколи не тримає весь файл у пам'яті** — тільки `Stream` у temp storage + pooled буфери 64–128 KB.
- Обробка — **push-based через `IAsyncEnumerable`/channel**, не polling.
- Усе long-running — з `CancellationToken`, який приходить із `TaskStoppingTrigger` хоста.
- Стан задачі lives у `IJobStore` (in-memory `ConcurrentDictionary`; інтерфейс — щоб потім замінити на Redis/Postgres без переписування).

---

## 3. Структура розв'язку (файли)

```
ImageProcessing.sln
├─ src/
│  ├─ ImageProcessing.Api/                  # веб-шар (Minimal API)
│  │   ├─ Program.cs                        # DI, Kestrel limits, endpoints map
│  │   ├─ Endpoints/
│  │   │   ├─ JobsEndpoints.cs              # POST /jobs, GET /jobs/{id}/status|result|rows
│  │   │   └─ HealthEndpoints.cs            # /healthz, /readyz
│  │   ├─ Upload/
│  │   │   └─ MultipartStreamReader.cs      # стрімінг upload → temp file через ArrayPool
│  │   ├─ Middleware/
│  │   │   └─ QueueFullMiddleware.cs        # 503 Retry-When коли channel переповнений
│  │   └─ appsettings.json                  # ліміти (див. §6)
│  │
│  ├─ ImageProcessing.Core/                 # домени + контракменти (без залежностей)
│  │   ├─ Jobs/
│  │   │   ├─ JobRecord.cs                  # Id, Type, Status, Params, CreatedAt, Error
│  │   │   ├─ JobStatus.cs                  # enum Queued/Running/Succeeded/Failed/Canceled
│  │   │   └─ IJobStore.cs                  # Get/Set/List — реалізація in-memory
│  │   ├─ Queue/
│  │   │   ├─ IJobQueue.cs                  # Enqueue / ReadAllAsync abstraction
│  │   │   └─ ChannelJobQueue.cs            # Channel.CreateBounded обгортка
│  │   ├─ Processing/
│  │   │   ├─ IJobProcessor.cs              # ProcessAsync(JobRecord, ctx, ct)
│  │   │   └─ ProcessorRegistry.cs          # route job.Type → processor
│  │   └─ Storage/
│  │       ├─ IFileStorage.cs               # Save/Get stream
│  │       └─ TempFileStorage.cs            # /uploads, /results
│  │
│  ├─ ImageProcessing.Workers/              # фонове виконання
│  │   ├─ JobConsumerWorker.cs              # BackgroundService: reader + SemaphoreSlim
│  │   ├─ TempCleanupWorker.cs              # BackgroundService: видаляє старі файли (retention)
│  │   └─ WorkerExtensions.cs               # AddQueue/AddWorkers DI helpers
│  │
│  ├─ ImageProcessing.Processing/           # ВСЯ heavy-lifting логіка (тут Span/ArrayPool)
│  │   ├─ Images/
│  │   │   ├─ ImageProcessor.cs             # IJobProcessor: grayscale/blur/resize/threshold
│  │   │   ├─ PixelSpanFilters.cs           # власні фільтри через Span<T> / ref struct
│  │   │   └─ ImageParams.cs                # запис-параметри (readonly struct)
│  │   ├─ Csv/
│  │   │   ├─ CsvProcessor.cs               # map-reduce над рядками, pooled buffer
│  │   │   ├─ SpanCsvParser.cs              # ref struct parser: split по ',' без string[]
│  │   │   └─ CsvRowStream.cs               # IAsyncEnumerable<CsvRow> для /rows
│  │   ├─ Matrices/
│  │   │   └─ MatrixProcessor.cs            # SIMD (Vector<T>) операції над Span<float>
│  │   └─ Buffers/
│  │       ├─ PooledBufferStream.cs         # Stream поверх ArrayPool<byte>
│  │       └─ CopyToPooled.cs               # extension: Stream→Stream з rented buffer
│  │
│  └─ (опційно Phase 2) ImageProcessing.RabbitMq/  # IJobQueue через RabbitMQ.Client
│
├─ tests/ImageProcessing.Tests/
│  ├─ Processing/ImageFilterTests.cs         # відомі входи/виходи фільтрів
│  ├─ Processing/SpanCsvParserTests.cs       # fuzz-подібні кейси парсера
│  ├─ Queue/ChannelJobQueueTests.cs          # backpressure, TryComplete, cancel
│  └─ Api/JobsEndpointsTests.cs              # WebApplicationFactory: 202 → status → result
│
├─ bench/ImageProcessing.Benchmarks/
│  ├─ Program.cs                             # BenchmarkRunner
│  ├─ ImageFilterBench.cs                    # naive (new byte[]) vs Span+ArrayPool
│  └─ CsvParserBench.cs                      # string.Split vs ref-struct parser
│
├─ docs/
│  ├─ architecture.md
│  └─ results/gc-report.md                   # цифри з бенчмарків для README
├─ .github/workflows/ci.yml                  # build + test + format
├─ Dockerfile                                # multi-stage
├─ .editorconfig                             # стилі, nullable, warnings-as-errors
└─ README.md
```

---

## 4. Етапи реалізації (workflow по фізах)

### Phase 0 — Scaffold (0.5 дня)
```bash
dotnet new sln -n ImageProcessing
dotnet new web   -o src/ImageProcessing.Api
dotnet new classlib -o src/ImageProcessing.Core
dotnet new classlib -o src/ImageProcessing.Workers
dotnet new classlib -o src/ImageProcessing.Processing
dotnet new xunit -o tests/ImageProcessing.Tests
dotnet new classlib -o bench/ImageProcessing.Benchmarks
dotnet sln add (Get-ChildItem -Recurse -Filter *.csproj)
dotnet add tests/ImageProcessing.Tests reference src/...
```
- ✅ `dotnet run` піднімає Kestrel, `/healthz` = 200.
- ✅ `.editorconfig` + `Directory.Build.props` (`<Nullable>enable</Nullable>`, `<LangVersion>latest</LangVersion>`, `TreatWarningsAsErrors`).

### Phase 1 — Jobs + queue + worker (1–2 дні)
- `JobRecord`/`IJobStore` (ConcurrentDictionary), `ChannelJobQueue` (bounded 128, `FullMode.Wait`).
- `POST /api/v1/jobs` → зберігає upload-стрім у temp file (`MultipartStreamReader`, копіювання через `ArrayPool.Rent(81920)`), пише record, `TryWrite` → **202 Accepted** + `Location`.
- `JobConsumerWorker : BackgroundService` — споживає чергу, поки просто "ковяпає" job і ставить Succeeded.
- GET `/status` повертає JSON стану.
- ✅ Тест: завантажив → 202 → через 1 сек статус Succeeded; черга повна → 503.

### Phase 2 — Image processing без аллокацій (2–3 дні)
- ImageSharp: `Image.Load<Bgra32>(stream)`, далі `image.Mutate(ctx => ...)` власними операторами.
- `PixelSpanFilters`: `unsafe`-free реалізації grayscale / box blur / threshold через `image.ProcessPixelRowsAccessors` — `Span<TPixel>` по рядках, без нових масивів на рядок; `ref struct`-курсор для 3×3 вікна.
- Результат: `image.SaveAsPngAsync(pooledStream, ct)`.
- ✅ **Ключова вимога:** `GC.GetTotalAllocatedBytes` на job ≤ 1.2× розміру вхідного зображення (для naive-версії ~10–15× — це твій доказ у README).
- ✅ Unit-тести фільтрів на контрольних зображеннях 8×8.

### Phase 3 — CSV + IAsyncEnumerable (1–2 дні)
- `SpanCsvParser` : `readonly ref struct` — `Parse(ReadOnlySpan<byte> line, RowCallback)`, split по позиціях `,` через `span.IndexOf`, жодного `string[]`.
- `CsvRowStream : IAsyncEnumerable<CsvRow>` — читає файл буферизовано (pooled 64 KB), парсить рядки, `yield return` нетримаючи весь файл.
- Endpoint `GET /jobs/{id}/rows` — пише NDJSON у `Response.Body` через `PipeWriter` (`System.IO.Pipelines`).
- Aggregate job: sum/avg/percentile по числовій колонці.
- ✅ Тест: 100k рядків — аллокації < 2× розміру файлу; `/rows` віддає перший рядок до того, як файл дочитаний (перевіряється часовим мітком).

### Phase 4 — Ліміти, backpressure, грейсфул-шатдаун (1 день)
- `SemaphoreSlim(MaxConcurrency)` у воркері + bounded channel; `ProcessorRegistry` route за типом.
- Таймаут job (`LinkedTokenSource + CancellationTokenSource.Delay`), retry 1× при IO-помилці.
- `IHostApplicationLifetime.ApplicationStopping` → `channel.Writer.TryComplete()`, воркер дочитує залишок (grace period 30 с).
- `/readyz`: готовність = хост стартували та черга < high-watermark.
- ✅ Навантаження `k6`/`wrk`: 500 паралельних upload ≤ 20 MB — сервіс стабільний, пам'ять не росте лінійно.

### Phase 5 — Streaming result + cleanup (0.5–1 день)
- `GET /result`: `Results.File(stream, "image/png")` — без `File.ReadAllBytes`.
- `PooledBufferStream` для посередників; `CopyToPooled` extension для прямих copy.
- `TempCleanupWorker`: retention 1 год, job status TTL 24 год.

### Phase 6 — Benchmarks + метрики + доказ "−80% GC" (1–2 дні)
- BenchmarkDotNet `[MemoryDiagnoser]`:
  - `ImageFilterBench` — naive `new byte[w*h*4]`/рядок-копія **vs** Span+ArrayPool версія.
  - `CsvParserBench` — `File.ReadAllLines+string.Split` **vs** ref-struct стрімінг-парсер.
- `dotnet-counters monitor --counters System.GC,ImageProcessing` під навантаженням — записати криві Gen0 collections.
- Результати → `docs/results/gc-report.md` → цифри в README.
- ✅ Критерій: Allocated Bytes/job ↓ ≥ 75–80%, Gen0 collections ↓; час обробки не гірший за naive.

### Phase 7 — Docker, CI, README (1 день)
- `Dockerfile`: multi-stage (`sdk` build → `aspnet` runtime), non-root user, `read-only` filesystem.
- `ci.yml`: restore → format --verify-changed → build → test → benchmarks (weekly/manual job).
- README: діаграма архітектури, quickstart, **бенчмарк-таблиця**, формулювання для резюме (див. §8).
- ✅ `docker build -t imgproc . && docker run -p 8080:8080 imgproc` — працює з нуля.

### Phase 8 (опційно, +2–3 дні) — зовнішня черга
- Реалізувати `IJobQueue` через **RabbitMQ** (`AsyncEventingBasicConsumer`, prefetch = MaxConcurrency) за feature-flag `QueueProvider=RabbitMq`.
- Або Redis Streams (`XADD/XREADGROUP`) + `StackExchange.Redis`.
- Другий воркер-інстанс, що споживає ту саму чергу → доведений horizontal scaling.

---

## 5. Контракт API

| Метод | Path | Опис | Codes |
|---|---|---|---|
| POST | `/api/v1/jobs` | multipart: `file`, `type=image\|csv\|matrix`, `params` (JSON) | **202** + `Location`; 400 невалідний type/params; 413 > ліміту; 503 черга повна |
| GET | `/api/v1/jobs` | список jobів (пагінація) | 200 |
| GET | `/api/v1/jobs/{id}/status` | `{id, type, status, progress, error?, createdAt}` | 200; 404 |
| GET | `/api/v1/jobs/{id}/result` | стрім результат (PNG/CSV-агрегат/бінарна матриця) | 200 `application/octet-stream`; 409 ще не готовий; 404 |
| GET | `/api/v1/jobs/{id}/rows` | CSV: `IAsyncEnumerable` → NDJSON стрім | 200 `application/x-ndjson`; 404 |
| DELETE | `/api/v1/jobs/{id}` | cancel + cleanup | 202 |
| GET | `/healthz`, `/readyz` | liveness / readiness | 200/503 |

`params` приклади:
```json
{ "filter": "grayscale", "resize": { "width": 1024, "mode": "max-dimension" } }
{ "column": 3, "aggregate": "p95", "group_by": 0 }
```

`status` response:
```json
{ "id": "1a2b...", "type": "image", "status": "Succeeded",
  "progress": 100, "allocatedBytes": 4718592,
  "createdAt": "...", "finishedAt": "..." }
```

---

## 6. Конфігурація та ліміти (`appsettings.json`)

```json
{
  "Kestrel": { "Limits": { "MaxRequestBodySize": 104857600 } },
  "Processing": {
    "MaxUploadBytes": 52428800,
    "MaxQueueLength": 128,
    "MaxConcurrency": 4,
    "JobTimeoutSeconds": 120,
    "TempRetentionMinutes": 60,
    "CopyBufferSizeBytes": 81920,
    "Image":   { "MaxPixels": 40000000 },
    "Csv":     { "MaxColumns": 256 }
  }
}
```
- `record ProcessingOptions` + `builder.Services.Configure<ProcessingOptions>(...)`, валідація через `IValidateOptions<T>` (старт падає на некоректних значеннях).
- `MaxConcurrency` ≈ `Environment.ProcessorCount` / 2 — підібрати на бенчі.
- `SemaphoreSlim(options.MaxConcurrency)` **і** `BoundedChannelOptions(options.MaxQueueLength)` — два рівні захисту.

---

## 7. Ключові патерни (зорі коду)

**Bounded channel як черга**
```csharp
private readonly Channel<JobLease> _channel =
    Channel.CreateBounded<JobLease>(new BoundedChannelOptions(capacity: 128)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = false,
        SingleWriter = false
    });
```

**Worker із SemaphoreSlim**
```csharp
public sealed class JobConsumerWorker(ChannelJobQueue queue, IJobProcessorResolver resolver,
    IOptions<ProcessingOptions> opts, ILogger<JobConsumerWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var throttle = new SemaphoreSlim(opts.Value.MaxConcurrency);
        var inFlight = new List<Task>();
        await foreach (var lease in queue.Reader.ReadAllAsync(stoppingToken))
        {
            await throttle.WaitAsync(stoppingToken);
            inFlight.Add(Task.Run(async () =>
            {
                try { await resolver.Resolve(lease.Type).ProcessAsync(lease, stoppingToken); }
                catch (OperationCanceledException) { }
                catch (Exception ex) { log.LogError(ex, "job {JobId}", lease.JobId); }
                finally { throttle.Release(); }
            }, CancellationToken.None));
            inFlight.RemoveAll(t => t.IsCompleted);
        }
        await Task.WhenAll(inFlight); // grace drain
    }
}
```

**Фільтр без аллокацій (ImageSharp + Span)**
```csharp
public static void GrayscaleInPlace(Image<Bgra32> image) =>
    image.ProcessPixelRows(accessor =>
    {
        for (int y = 0; y < accessor.Height; y++)
        {
            Span<Bgra32> row = accessor.GetRowSpan(y);   // НЕ новий масив
            for (int x = 0; x < row.Length; x++)
            {
                ref Bgra32 p = ref row[x];
                byte g = (byte)(0.299f * p.R + 0.587f * p.G + 0.114f * p.B);
                p = new Bgra32(g, g, g, p.A);
            }
        }
    });
```

**Pooled копіювання потоку**
```csharp
public static async Task CopyToPooledAsync(this Stream from, Stream to, int bufSize, CancellationToken ct)
{
    byte[] buffer = ArrayPool<byte>.Shared.Rent(bufSize);
    try
    {
        int read;
        while ((read = await from.ReadAsync(buffer.AsMemory(0, bufSize), ct)) > 0)
            await to.WriteAsync(buffer.AsMemory(0, read), ct);
    }
    finally { ArrayPool<byte>.Shared.Return(buffer); }
}
```

**ref struct CSV-рядки**
```csharp
public readonly ref struct SpanCsvRow
{
    private readonly ReadOnlySpan<byte> _line;
    public int FieldCount { get; }
    public ReadOnlySpan<byte> this[int i] { get { ... } }   // slice по позиціях ','
    public bool TryGetInt32(int i, out int value) => ...
}
```

**Streaming endpoint**
```csharp
group.MapGet("/jobs/{id:guid}/result", async (Guid id, IJobStore store, IFileStorage fs, CancellationToken ct) =>
{
    var job = store.Get(id);
    if (job is null) return Results.NotFound();
    if (job.Status != JobStatus.Succeeded) return Results.Conflict(job);
    Stream stream = fs.OpenRead(job.ResultPath!);
    return Results.File(stream, "image/png", $"{id}.png"); // стрім, не byte[]
});
```

---

## 8. Умови (acceptance criteria / Definition of Done)

**Функціональні**
- [ ] Завантаження → `202 Accepted` < 50 мс при файлі 50 MB (стрімінговий запис не чекає обробки).
- [ ] Усі три типи jobs (image / csv / matrix) обробляються через один pipeline.
- [ ] `/result` і `/rows` — стрімінгові: RSS не стрибає на розмір файлу.
- [ ] Повна черга → `503` з `Retry-After`; невалідний тип/розмір → `400/413`.
- [ ] Cancel/kill під час обробки → коректний `Failed/Canceled`, тимчасові файли не протікають.
- [ ] Грайсфул-шатдаун: `docker stop` — поточні jobи домальовуються в межах grace period.

**Продуктивність (цифри для README)**
- [ ] На 1000 jobs × 5 MB PNG: **Gen0 GC collections ↓ ≥ 80%** проти naive-реалізації (BenchmarkDotNet `MemoryDiagnoser` + `dotnet-counters`, записати обидві таблиці в `docs/results/gc-report.md`).
- [ ] Allocated bytes на image-job ≤ 1.5× розміру зображення в пам'яті після decode.
- [ ] 4 одночасні jobи ≤ `MaxConcurrency`, RAM plateau без OOM при 100 jobах у черзі.
- [ ] Тести проходять; CI зелений; Docker образ < 150 MB (chiseled runtime).

**Код-стиль**
- [ ] `Nullable enable`, warnings-as-errors; жодного `async void`, `.Result`, `.Wait()`, `Task.Result` синхронних blocking-викликів.
- [ ] Усі публічні API processor'ів — `async` + `CancellationToken` останнім параметром.
- [ ] Ніяких `new byte[large]` у гарячому шляху — тільки `ArrayPool` або `stackalloc` (< 1 KB).
- [ ] Коментарі англійською в коді (repo для роботодавців), документи — укр.

**Формулювання для резюме (з реальними цифрами з gc-report.md):**
> "Розробив ASP.NET Core API для фону обробки зображень/CSV на `Channel<T>` + `BackgroundService`. Зменшив аллокації GC на ~80% (Gen0 collections 1 240 → 210 на 1000 jobs) завдяки `Span<T>`, `ref struct`-парсеру та `ArrayPool<T>`. Реалізував стрімінг upload/download через `IAsyncEnumerable` і `System.IO.Pipelines`, backpressure через bounded channel + `SemaphoreSlim`."

---

## 9. Тестова стратегія

| Рівень | Що | Інструмент |
|---|---|---|
| Unit | фільтри (відомі 8×8 пікселі), SpanCsvParser (edge: `""` кавички, CRLF, порожні поля), channel semantics (cancel, complete) | xUnit |
| Integration | `WebApplicationFactory` → POST → poll status → GET result; multipart 10 MB | xUnit + HttpClient |
| Memory | "allocation tests": `GC.GetTotalAllocatedBytes(true)` delta < порогу, `dotMemory`/`PerfView` профіль | BenchmarkDotNet `[MemoryDiagnoser]` |
| Load | `k6` скрипт: ramp 100 vus, mixed upload types, перевірка 503 при переповненні | k6 / `wrk` |
| Chaos (опц.) | kill -9 воркера → job переходить у Failed після рестарту (або retry) | docker |

Приклад allocation-тесту:
```csharp
[Fact]
public async Task Grayscale_Allocates_NoMore_Than_150Percent_Of_Payload()
{
    byte[] input = File.ReadAllBytes("Assets/photo5mb.png");
    await Warmup(input);
    long before = GC.GetTotalAllocatedBytes(true);
    await new ImageProcessor(...).ProcessAsync(MakeJob(input), CancellationToken.None);
    long allocated = GC.GetTotalAllocatedBytes(true) - before;
    Assert.True(allocated < input.Length * 3 / 2);
}
```

---

## 10. Git workflow

- Гілка `master`; фічі: `feat/queue-worker`, `feat/image-filters`, `feat/csv-streaming`, `perf/arraypool-copy`, `chore/ci`.
- Conventional Commits: `feat:`, `perf:` (окремо для оптимізацій пам'яті — це твій changelog для співбесіди), `test:`, `bench:`, `docs:`.
- PR: описание "what changed + GC before/after numbers" — на співбесіді це готові stories.

---

## 11. Ризики і як їх закрити

| Ризик | Захист |
|---|---|
| `File.ReadAllBytes` "примкне" у коді | code review rule: заборона; lint — own analyzer або grep у CI |
| ImageSharp decode сам аллокує великий pixel buffer | це ок: один_rent на job; не робити новий на рядок; вимірювати delta |
| `ArrayPool` + `async` — повернення в `finally` забувають | `PooledBuffer` : `struct, IDisposable` pattern (rent/return у using) |
| Unbounded dictionary jobs → RAM leak | `TempCleanupWorker` + LRU-кап на `IJobStore` |
| Single-reader optimization втрачено при scale-out | `SingleReader=false`, ідемпотентні processors |
| "−80% GC" без доказу | бенчмарк-репозиторій результатів: додати CI job, що зберігає `gc-report.md` |

---

## 12. Quickstart (після реалізації)

```bash
dotnet run --project src/ImageProcessing.Api
curl -X POST http://localhost:8080/api/v1/jobs \
  -F file=@photo.png -F type=image -F params='{"filter":"grayscale"}'
# -> 202 Accepted, Location: /api/v1/jobs/<id>/status
curl http://localhost:8080/api/v1/jobs/<id>/status
curl -o out.png http://localhost:8080/api/v1/jobs/<id>/result
dotnet run -c Release --project bench/ImageProcessing.Benchmarks   #.gc-таблиця
```
