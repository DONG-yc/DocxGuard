namespace DocxGuard.Core.Building;

/// <summary>
/// Memory limits applied while an immutable source package is rebuilt into a
/// new in-memory DOCX artifact.
/// </summary>
public sealed record DocxPackageRebuildLimits
{
    public static DocxPackageRebuildLimits Default { get; } = new();

    public int MaxSourcePackageBytes { get; init; } = 50 * 1024 * 1024;

    public int MaxOutputPackageBytes { get; init; } = 50 * 1024 * 1024;

    internal void Validate()
    {
        ValidatePositive(MaxSourcePackageBytes, nameof(MaxSourcePackageBytes));
        ValidatePositive(MaxOutputPackageBytes, nameof(MaxOutputPackageBytes));
    }

    private static void ValidatePositive(int value, string parameterName)
    {
        if (value <= 0)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                "Package rebuild limits must be positive.");
        }
    }
}
