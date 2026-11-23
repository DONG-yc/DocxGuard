namespace DocxGuard.Core.Execution;

public sealed record TextPatchExecutionLimits
{
    public static TextPatchExecutionLimits Default { get; } = new();

    public int MaxOperations { get; init; } = 1;

    public int MaxAffectedRuns { get; init; } = 1_024;

    public int MaxGeneratedTextNodes { get; init; } = 16_384;

    public int MaxOutputMainDocumentXmlBytes { get; init; } = 16 * 1024 * 1024;

    internal void Validate()
    {
        ValidatePositive(MaxOperations, nameof(MaxOperations));
        ValidatePositive(MaxAffectedRuns, nameof(MaxAffectedRuns));
        ValidatePositive(MaxGeneratedTextNodes, nameof(MaxGeneratedTextNodes));
        ValidatePositive(
            MaxOutputMainDocumentXmlBytes,
            nameof(MaxOutputMainDocumentXmlBytes));
    }

    private static void ValidatePositive(int value, string parameterName)
    {
        if (value <= 0)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                "Text patch execution limits must be positive.");
        }
    }
}
