namespace DocxGuard.Core.Packaging;

/// <summary>
/// Resource limits for the OPC metadata layer. These are controlled-template
/// product limits, not limits from the OPC specification.
/// </summary>
public sealed record OpcInspectionLimits
{
    public static OpcInspectionLimits Default { get; } = new();

    public int MaxPackageSnapshotBytes { get; init; } = 50 * 1024 * 1024;

    public int MaxContentTypesXmlBytes { get; init; } = 4 * 1024 * 1024;

    public int MaxRelationshipPartXmlBytes { get; init; } = 4 * 1024 * 1024;

    public int MaxTotalRelationshipXmlBytes { get; init; } = 16 * 1024 * 1024;

    public long MaxXmlCharacters { get; init; } = 4L * 1024 * 1024;

    public int MaxXmlDepth { get; init; } = 16;

    public int MaxXmlNodes { get; init; } = 16_384;

    public int MaxContentTypeMappings { get; init; } = 4_096;

    public int MaxRelationshipsPerPart { get; init; } = 4_096;

    public int MaxTotalRelationships { get; init; } = 16_384;

    public int MaxAttributeValueCharacters { get; init; } = 2_048;

    public int MaxPartNameSegments { get; init; } = 128;

    internal void Validate()
    {
        ValidatePositive(MaxPackageSnapshotBytes, nameof(MaxPackageSnapshotBytes));
        ValidatePositive(MaxContentTypesXmlBytes, nameof(MaxContentTypesXmlBytes));
        ValidatePositive(MaxRelationshipPartXmlBytes, nameof(MaxRelationshipPartXmlBytes));
        ValidatePositive(MaxTotalRelationshipXmlBytes, nameof(MaxTotalRelationshipXmlBytes));
        ValidatePositive(MaxXmlCharacters, nameof(MaxXmlCharacters));
        ValidatePositive(MaxXmlDepth, nameof(MaxXmlDepth));
        ValidatePositive(MaxXmlNodes, nameof(MaxXmlNodes));
        ValidatePositive(MaxContentTypeMappings, nameof(MaxContentTypeMappings));
        ValidatePositive(MaxRelationshipsPerPart, nameof(MaxRelationshipsPerPart));
        ValidatePositive(MaxTotalRelationships, nameof(MaxTotalRelationships));
        ValidatePositive(MaxAttributeValueCharacters, nameof(MaxAttributeValueCharacters));
        ValidatePositive(MaxPartNameSegments, nameof(MaxPartNameSegments));
    }

    private static void ValidatePositive(long value, string parameterName)
    {
        if (value <= 0)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                "OPC inspection limits must be positive.");
        }
    }
}
