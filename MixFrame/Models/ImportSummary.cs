namespace MixFrame.Models;

public sealed record ImportSummary(
    int TotalCount,
    int ValidCount,
    int ReadFailedCount,
    int UnsupportedCount,
    int DuplicateCount);
