namespace DocxGuard.Core.Packaging;

/// <summary>
/// Implements the ASCII-only case folding required by OPC identifiers.
/// Non-ASCII code units are compared exactly.
/// </summary>
internal sealed class AsciiCaseInsensitiveComparer :
    IEqualityComparer<string>,
    IComparer<string>
{
    public static AsciiCaseInsensitiveComparer Instance { get; } = new();

    private AsciiCaseInsensitiveComparer()
    {
    }

    public bool Equals(string? left, string? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left is null || right is null || left.Length != right.Length)
        {
            return false;
        }

        for (int index = 0; index < left.Length; index++)
        {
            if (Fold(left[index]) != Fold(right[index]))
            {
                return false;
            }
        }

        return true;
    }

    public int GetHashCode(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        HashCode hash = new();
        foreach (char character in value)
        {
            hash.Add(Fold(character));
        }

        return hash.ToHashCode();
    }

    /// <summary>
    /// Orders path descendants directly after their parent by sorting '/'
    /// before every valid segment character. This enables allocation-free
    /// adjacent-prefix checks after sorting.
    /// </summary>
    public int Compare(string? left, string? right)
    {
        if (ReferenceEquals(left, right))
        {
            return 0;
        }

        if (left is null)
        {
            return -1;
        }

        if (right is null)
        {
            return 1;
        }

        int commonLength = Math.Min(left.Length, right.Length);
        for (int index = 0; index < commonLength; index++)
        {
            char leftValue = Fold(left[index]);
            char rightValue = Fold(right[index]);
            if (leftValue == rightValue)
            {
                continue;
            }

            if (leftValue == '/')
            {
                return -1;
            }

            if (rightValue == '/')
            {
                return 1;
            }

            return leftValue.CompareTo(rightValue);
        }

        return left.Length.CompareTo(right.Length);
    }

    public bool IsPrefixAtSegmentBoundary(string prefix, string value)
    {
        if (value.Length <= prefix.Length || value[prefix.Length] != '/')
        {
            return false;
        }

        for (int index = 0; index < prefix.Length; index++)
        {
            if (Fold(prefix[index]) != Fold(value[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static char Fold(char value) => value is >= 'a' and <= 'z'
        ? (char)(value - ('a' - 'A'))
        : value;
}
