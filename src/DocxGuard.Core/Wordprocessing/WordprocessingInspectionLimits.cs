namespace DocxGuard.Core.Wordprocessing;

/// <summary>
/// Controlled-template limits for the WordprocessingML main document layer.
/// </summary>
public sealed record WordprocessingInspectionLimits
{
    public static WordprocessingInspectionLimits Default { get; } = new();

    public int MaxMainDocumentXmlBytes { get; init; } = 16 * 1024 * 1024;

    public long MaxXmlCharacters { get; init; } = 16L * 1024 * 1024;

    public int MaxXmlDepth { get; init; } = 256;

    public int MaxXmlNodes { get; init; } = 1_000_000;

    public int MaxBodyChildElements { get; init; } = 100_000;

    public int MaxAttributesPerElement { get; init; } = 256;

    public int MaxAttributeValueCharacters { get; init; } = 65_536;

    internal void Validate()
    {
        ValidatePositive(MaxMainDocumentXmlBytes, nameof(MaxMainDocumentXmlBytes));
        ValidatePositive(MaxXmlCharacters, nameof(MaxXmlCharacters));
        ValidatePositive(MaxXmlDepth, nameof(MaxXmlDepth));
        ValidatePositive(MaxXmlNodes, nameof(MaxXmlNodes));
        ValidatePositive(MaxBodyChildElements, nameof(MaxBodyChildElements));
        ValidatePositive(MaxAttributesPerElement, nameof(MaxAttributesPerElement));
        ValidatePositive(
            MaxAttributeValueCharacters,
            nameof(MaxAttributeValueCharacters));
    }

    private static void ValidatePositive(long value, string parameterName)
    {
        if (value <= 0)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                "WordprocessingML inspection limits must be positive.");
        }
    }
}
