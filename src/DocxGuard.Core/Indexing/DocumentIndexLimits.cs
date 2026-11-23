namespace DocxGuard.Core.Indexing;

public sealed record DocumentIndexLimits
{
    public static DocumentIndexLimits Default { get; } = new();

    public int MaxParagraphs { get; init; } = 100_000;

    public int MaxRuns { get; init; } = 1_000_000;

    public int MaxTables { get; init; } = 10_000;

    public int MaxTableCells { get; init; } = 100_000;

    public int MaxTableNestingDepth { get; init; } = 32;

    public int MaxTextCharacters { get; init; } = 10_000_000;

    internal void Validate()
    {
        ValidatePositive(MaxParagraphs, nameof(MaxParagraphs));
        ValidatePositive(MaxRuns, nameof(MaxRuns));
        ValidatePositive(MaxTables, nameof(MaxTables));
        ValidatePositive(MaxTableCells, nameof(MaxTableCells));
        ValidatePositive(MaxTableNestingDepth, nameof(MaxTableNestingDepth));
        ValidatePositive(MaxTextCharacters, nameof(MaxTextCharacters));
    }

    private static void ValidatePositive(int value, string parameterName)
    {
        if (value <= 0)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                "Document index limits must be positive.");
        }
    }
}
