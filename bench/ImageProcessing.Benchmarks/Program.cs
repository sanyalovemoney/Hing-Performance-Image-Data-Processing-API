using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;

// Placeholder harness — real naive-vs-pooled benchmarks land in a later stage.
BenchmarkRunner.Run<PlaceholderBench>();

public class PlaceholderBench
{
    [Benchmark]
    public int Sum()
    {
        var total = 0;
        for (var i = 0; i < 100; i++)
        {
            total += i;
        }

        return total;
    }
}
