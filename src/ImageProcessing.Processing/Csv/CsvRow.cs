namespace ImageProcessing.Processing.Csv;

/// <summary>
/// A materialized CSV row (fields decoded to strings). Produced only where rows
/// leave the process; in-process aggregation never allocates these.
/// </summary>
public readonly record struct CsvRow(int Index, string[] Fields);
