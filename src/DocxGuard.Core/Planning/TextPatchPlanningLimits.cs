namespace DocxGuard.Core.Planning;

public sealed record TextPatchPlanningLimits
{
    public static TextPatchPlanningLimits Default { get; } = new();

    public int MaxExpectedTextCharacters { get; init; } = 65_536;

    public int MaxReplacementTextCharacters { get; init; } = 65_536;

    public int MaxCandidateMatches { get; init; } = 10_000;

    internal void Validate()
    {
        ValidatePositive(
            MaxExpectedTextCharacters,
            nameof(MaxExpectedTextCharacters));
        ValidatePositive(
            MaxReplacementTextCharacters,
            nameof(MaxReplacementTextCharacters));
        ValidatePositive(MaxCandidateMatches, nameof(MaxCandidateMatches));
    }

    internal TextLocationLimits ToLocationLimits() => new()
    {
        MaxQueryCharacters = MaxExpectedTextCharacters,
        MaxMatches = MaxCandidateMatches,
    };

    private static void ValidatePositive(int value, string parameterName)
    {
        if (value <= 0)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                "Text patch planning limits must be positive.");
        }
    }
}
