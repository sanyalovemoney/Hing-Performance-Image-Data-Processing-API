using Xunit;

// Allocation-guard tests measure GC.GetTotalAllocatedBytes, which is a
// process-wide counter — parallel test classes would pollute each other's
// measurements. Serial execution keeps those assertions deterministic.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
