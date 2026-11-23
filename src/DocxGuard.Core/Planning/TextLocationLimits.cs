namespace DocxGuard.Core.Planning;

public sealed record TextLocationLimits
{
    public static TextLocationLimits Default { get; } = new();

    public int MaxQueryCharacters { get; init; } = 65_536;

    public int MaxMatches { get; init; } = 10_000;

    internal void Validate()
    {
        ValidatePositive(MaxQueryCharacters, nameof(MaxQueryCharacters));
        ValidatePositive(MaxMatches, nameof(MaxMatches));
    }

    private static void ValidatePositive(int value, string parameterName)
    {
        if (value <= 0)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                "Text location limits must be positive.");
        }
    }
}
