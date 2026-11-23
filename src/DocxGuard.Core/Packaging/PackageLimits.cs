namespace DocxGuard.Core.Packaging;

/// <summary>
/// Resource limits applied before an untrusted package is parsed as Open XML.
/// These defaults are product policy, not limits imposed by the DOCX format.
/// </summary>
public sealed record PackageLimits
{
    public static PackageLimits Default { get; } = new();

    public long MaxArchiveBytes { get; init; } = 50L * 1024 * 1024;

    public int MaxEntryCount { get; init; } = 2_048;

    public int MaxEntryNameCharacters { get; init; } = 1_024;

    public long MaxEntryUncompressedBytes { get; init; } = 64L * 1024 * 1024;

    public long MaxTotalUncompressedBytes { get; init; } = 256L * 1024 * 1024;

    public double MaxCompressionRatio { get; init; } = 100d;

    internal void Validate()
    {
        ValidatePositive(MaxArchiveBytes, nameof(MaxArchiveBytes));
        ValidatePositive(MaxEntryCount, nameof(MaxEntryCount));
        ValidatePositive(MaxEntryNameCharacters, nameof(MaxEntryNameCharacters));
        ValidatePositive(MaxEntryUncompressedBytes, nameof(MaxEntryUncompressedBytes));
        ValidatePositive(MaxTotalUncompressedBytes, nameof(MaxTotalUncompressedBytes));

        if (double.IsNaN(MaxCompressionRatio)
            || double.IsInfinity(MaxCompressionRatio)
            || MaxCompressionRatio <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxCompressionRatio),
                MaxCompressionRatio,
                "The maximum compression ratio must be a finite positive number.");
        }
    }

    private static void ValidatePositive(long value, string parameterName)
    {
        if (value <= 0)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                "Package limits must be positive.");
        }
    }
}
