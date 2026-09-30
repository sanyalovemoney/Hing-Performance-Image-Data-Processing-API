# GC / allocation report

Measured with BenchmarkDotNet v0.15.0 — **not estimates**. Every number below
comes from a run of `bench/ImageProcessing.Benchmarks` on the machine noted,
with `[MemoryDiagnoser]` attached.

## Environment

| | |
|---|---|
| OS | Windows 11 (10.0.26200.9457), X64 |
| Runtime | .NET 8.0.31 (8.0.3126.42015), RyuJIT AVX2 |
| SDK | 8.0.425 |
| Job | `LaunchCount=1, WarmupCount=2, IterationCount=6` |
| Date | 2026-09-30 |

Reproduce: `dotnet run --project bench/ImageProcessing.Benchmarks -c Release`.

## Image filters — 512×512 Bgra32, in place

Naive = per-row `ToArray()` + fresh destination row (the classic
"copy each line, transform, copy back" pattern). Span = `ProcessPixelRows`
row spans with an `ArrayPool<Bgra32>` 3-row window for blur.

| Method | Mean | Gen0 | Gen1 | Gen2 | Allocated |
|---|---:|---:|---:|---:|---:|
| Grayscale_naive_row_arrays (baseline) | 726.5 μs | 506.84 | – | – | 2,121,729 B |
| **Grayscale_span_in_place** | **561.4 μs** (×0.77) | **0** | 0 | 0 | **688 B** |
| BoxBlur_naive_row_arrays | 5,666.4 μs | 1,335.94 | 328.13 | 328.13 | 5,293,166 B |
| **BoxBlur_span_pooled_window** | **5,552.0 μs** (×0.98) | **0** | **0** | **0** | **8 B** |

- **Grayscale**: Gen0 −100% (506.84 → 0 per op), allocated −99.97%
  (2.12 MB → 688 B), and 23% faster. The residual 688 B is a constant-size
  per-call accessor allocation — it does not grow with image size, while the
  naive version allocates 2 arrays per row (1024 arrays here).
- **Box blur**: Gen0/Gen1/Gen2 −100% (5.29 MB → 8 B per op). Time is on par
  (±2%) because the workload is compute-bound — the win is pure GC pressure:
  naive blur promotes 328 Gen1+Gen2 collections-worth of bytes per call.

## CSV column scan — 50,000 rows (~1.4 MB UTF-8)

Naive = `Encoding.UTF8.GetString` + `string.Split` per line and per field.
Span = `SpanCsvParser` ref-struct enumerator walking the raw bytes, parsing
`double` straight from UTF-8 (invariant culture).

| Method | Mean | Gen0 | Gen1 | Gen2 | Allocated |
|---|---:|---:|---:|---:|---:|
| Scan_string_split (baseline) | 15.875 ms | 2,468.75 | 1,437.50 | 468.75 | 13,791,380 B |
| **Scan_span_ref_struct** | **4.712 ms** (×0.30) | **0** | **0** | **0** | **3 B** |

- **3.4× faster and −100% Gen0** (13.79 MB → 3 B per scan). The naive path
  pays in every generation, including 468 Gen2 per op — LOH churn from the
  1.4 MB intermediate string.

## Definition of Done (workflow.md §8)

> Gen0 count after processing ≥ 80% lower than the naive implementation.

| Workload | Gen0 naive | Gen0 pooled/span | Reduction |
|---|---:|---:|---:|
| Grayscale 512×512 | 506.84 | 0 | **100%** ✅ |
| Box blur 512×512 | 1,335.94 (+656.25 Gen1/2) | 0 | **100%** ✅ |
| CSV scan 50k rows | 2,468.75 (+1,906.25 Gen1/2) | 0 | **100%** ✅ |

**DoD met.** Allocation guards in the test suite (`GC.GetTotalAllocatedBytes`
on the 20k-row streaming scan) enforce this continuously, not just at report
time.

## Notes & honesty

- Benchmarks mutate one preallocated image across iterations; grayscale is
  idempotent and re-blurring does not change cost, so timings stay comparable.
- StdDev on this quick config: ≤ 0.65 ms (CSV), ≤ 120 μs (filters) — the
  allocation ratios are exact counters, the time ratios carry that slack.
- Server GC (`DOTNET_gcServer=1` in the Dockerfile) will further hide the
  naive version's cost on multi-core boxes — which is exactly why the
  allocation columns, not wall time, are the DoD metric.
