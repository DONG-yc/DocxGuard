using System.Diagnostics.CodeAnalysis;

namespace DocxGuard.Core.Packaging;

internal static class RelationshipTargetResolver
{
    public static bool TryResolve(
        OpcPartName? sourcePartName,
        string target,
        [NotNullWhen(true)] out OpcPartName? targetPartName)
    {
        targetPartName = null;
        if (target.Length == 0
            || target.EndsWith('/')
            || target.StartsWith("//", StringComparison.Ordinal)
            || target.Contains('\\')
            || target.Contains('?')
            || target.Contains('#')
            || target.Any(character => char.IsWhiteSpace(character)
                || char.IsControl(character)))
        {
            return false;
        }

        bool packageAbsolute = target.StartsWith('/');
        string relativePath = packageAbsolute ? target[1..] : target;
        if (relativePath.Length == 0
            || (!packageAbsolute && HasSchemeLikeFirstSegment(relativePath)))
        {
            return false;
        }

        List<string> segments = [];
        if (!packageAbsolute && sourcePartName is not null)
        {
            string sourceDirectory = sourcePartName.Value[
                1..(sourcePartName.Value.LastIndexOf('/') + 1)];
            segments.AddRange(
                sourceDirectory.Split('/', StringSplitOptions.RemoveEmptyEntries));
        }

        foreach (string segment in relativePath.Split('/'))
        {
            if (segment.Length == 0)
            {
                return false;
            }

            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (segments.Count != 0)
                {
                    segments.RemoveAt(segments.Count - 1);
                }

                continue;
            }

            segments.Add(segment);
        }

        if (segments.Count == 0)
        {
            return false;
        }

        return OpcPartName.TryParse($"/{string.Join('/', segments)}", out targetPartName);
    }

    private static bool HasSchemeLikeFirstSegment(string target)
    {
        int slash = target.IndexOf('/');
        ReadOnlySpan<char> firstSegment = slash < 0
            ? target.AsSpan()
            : target.AsSpan(0, slash);
        int colon = firstSegment.IndexOf(':');
        if (colon <= 0 || !IsAsciiLetter(firstSegment[0]))
        {
            return false;
        }

        for (int index = 1; index < colon; index++)
        {
            char character = firstSegment[index];
            if (!IsAsciiLetter(character)
                && character is not (>= '0' and <= '9')
                && character is not '+' and not '-' and not '.')
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsAsciiLetter(char value) =>
        value is >= 'A' and <= 'Z' or >= 'a' and <= 'z';
}
