using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using ImageProcessing.Benchmarks;

if (args.Length > 0)
{
    // Full control from the CLI, e.g.:
    //   dotnet run -c Release -- --filter *ImageFilter* --join
    BenchmarkSwitcher.FromAssembly(typeof(ImageFilterBench).Assembly).Run(args);
    return;
}

// No args: a quick but honest run — one launch, short warmup, six
// measured iterations per benchmark (enough for stable allocation counts).
var quick = DefaultConfig.Instance
    .AddJob(Job.Default.WithLaunchCount(1).WithWarmupCount(2).WithIterationCount(6));

BenchmarkRunner.Run<ImageFilterBench>(quick);
BenchmarkRunner.Run<CsvParserBench>(quick);
