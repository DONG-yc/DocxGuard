using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;

namespace DocxGuard.Core.Packaging;

/// <summary>
/// A validated logical OPC part name. Equality folds ASCII letters only, as
/// required by OPC, and never applies culture-sensitive Unicode case folding.
/// </summary>
public sealed class OpcPartName : IEquatable<OpcPartName>
{
    private OpcPartName(string value)
    {
        Value = value;
    }

    public string Value { get; }

    public static OpcPartName Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        if (!TryParse(value, out OpcPartName? partName))
        {
            throw new FormatException("The value is not a valid OPC part name.");
        }

        return partName;
    }

    public static bool TryParse(
        string? value,
        [NotNullWhen(true)] out OpcPartName? partName)
    {
        if (value is null
            || !TryCanonicalize(value, decodeNonAsciiEscapes: false, out string canonicalValue))
        {
            partName = null;
            return false;
        }

        partName = new OpcPartName(canonicalValue);
        return true;
    }

    public bool Equals(OpcPartName? other) => other is not null
        && AsciiCaseInsensitiveComparer.Instance.Equals(Value, other.Value);

    public override bool Equals(object? obj) => Equals(obj as OpcPartName);

    public override int GetHashCode() =>
        AsciiCaseInsensitiveComparer.Instance.GetHashCode(Value);

    public override string ToString() => Value;

    internal static bool TryFromPackageItem(
        string packageItemName,
        [NotNullWhen(true)] out OpcPartName? partName)
    {
        ArgumentNullException.ThrowIfNull(packageItemName);

        if (packageItemName.Length == 0
            || packageItemName.StartsWith('/')
            || packageItemName.EndsWith('/')
            || packageItemName.Any(character => character > 0x7F))
        {
            partName = null;
            return false;
        }

        if (!TryCanonicalize(
            $"/{packageItemName}",
            decodeNonAsciiEscapes: true,
            out string canonicalValue))
        {
            partName = null;
            return false;
        }

        partName = new OpcPartName(canonicalValue);
        return true;
    }

    internal string? GetExtension()
    {
        int segmentStart = Value.LastIndexOf('/') + 1;
        int dotIndex = Value.LastIndexOf('.');
        return dotIndex < segmentStart || dotIndex == Value.Length - 1
            ? null
            : Value[(dotIndex + 1)..];
    }

    internal bool IsRelationshipPart()
    {
        int finalSlash = Value.LastIndexOf('/');
        if (finalSlash <= 0)
        {
            return false;
        }

        ReadOnlySpan<char> relationshipName = Value.AsSpan(finalSlash + 1);
        int previousSlash = Value.LastIndexOf('/', finalSlash - 1);
        ReadOnlySpan<char> directoryName = Value.AsSpan(
            previousSlash + 1,
            finalSlash - previousSlash - 1);
        if (!EqualsAsciiIgnoreCase(directoryName, "_rels"))
        {
            return false;
        }

        if (EqualsAsciiIgnoreCase(relationshipName, ".rels"))
        {
            return previousSlash == 0;
        }

        return relationshipName.Length > ".rels".Length
            && EndsWithAsciiIgnoreCase(relationshipName, ".rels");
    }

    internal bool TryGetRelationshipSource(
        out bool isPackageSource,
        out OpcPartName? sourcePartName)
    {
        isPackageSource = false;
        sourcePartName = null;
        if (!IsRelationshipPart())
        {
            return false;
        }

        if (AsciiCaseInsensitiveComparer.Instance.Equals(Value, "/_rels/.rels"))
        {
            isPackageSource = true;
            return true;
        }

        int finalSlash = Value.LastIndexOf('/');
        int relsDirectorySlash = Value.LastIndexOf('/', finalSlash - 1);
        string sourceDirectory = Value[..(relsDirectorySlash + 1)];
        string relationshipFileName = Value[(finalSlash + 1)..];
        string sourceValue = sourceDirectory
            + relationshipFileName[..^".rels".Length];
        return TryParse(sourceValue, out sourcePartName);
    }

    private static bool TryCanonicalize(
        string value,
        bool decodeNonAsciiEscapes,
        out string canonicalValue)
    {
        canonicalValue = string.Empty;

        if (value.Length < 2
            || value[0] != '/'
            || value[1] == '/'
            || value[^1] == '/'
            || value.Contains('\\')
            || value.Contains('?')
            || value.Contains('#'))
        {
            return false;
        }

        StringBuilder result = new(value.Length);
        result.Append('/');

        int segmentStart = 1;
        for (int index = 1; index <= value.Length; index++)
        {
            if (index < value.Length && value[index] != '/')
            {
                continue;
            }

            ReadOnlySpan<char> segment = value.AsSpan(
                segmentStart,
                index - segmentStart);
            if (segment.Length == 0
                || segment.SequenceEqual(".")
                || segment.SequenceEqual("..")
                || segment[^1] == '.'
                || !TryAppendCanonicalSegment(
                    segment,
                    decodeNonAsciiEscapes,
                    result))
            {
                return false;
            }

            if (index < value.Length)
            {
                result.Append('/');
                segmentStart = index + 1;
            }
        }

        canonicalValue = result.ToString();
        return true;
    }

    private static bool TryAppendCanonicalSegment(
        ReadOnlySpan<char> segment,
        bool decodeNonAsciiEscapes,
        StringBuilder result)
    {
        for (int index = 0; index < segment.Length;)
        {
            if (segment[index] == '%')
            {
                List<byte> escapedBytes = [];
                while (index < segment.Length && segment[index] == '%')
                {
                    if (index + 2 >= segment.Length
                        || !TryParseHexByte(segment[index + 1], segment[index + 2], out byte value))
                    {
                        return false;
                    }

                    escapedBytes.Add(value);
                    index += 3;
                }

                if (!AppendEscapedRun(
                    escapedBytes,
                    decodeNonAsciiEscapes,
                    result))
                {
                    return false;
                }

                continue;
            }

            OperationStatus status = Rune.DecodeFromUtf16(
                segment[index..],
                out Rune rune,
                out int consumed);
            if (status != OperationStatus.Done || !IsAllowedRawRune(rune))
            {
                return false;
            }

            result.Append(rune.ToString());
            index += consumed;
        }

        return true;
    }

    private static bool AppendEscapedRun(
        IReadOnlyList<byte> escapedBytes,
        bool decodeNonAsciiEscapes,
        StringBuilder result)
    {
        byte[] bytes = escapedBytes.ToArray();
        int offset = 0;

        while (offset < bytes.Length)
        {
            OperationStatus status = Rune.DecodeFromUtf8(
                bytes.AsSpan(offset),
                out Rune rune,
                out int consumed);
            if (status != OperationStatus.Done)
            {
                return false;
            }

            if (rune.IsAscii)
            {
                char value = (char)rune.Value;
                if (value is '/' or '\\'
                    || IsAsciiIunreserved(value))
                {
                    return false;
                }

                for (int byteIndex = 0; byteIndex < consumed; byteIndex++)
                {
                    result.Append('%');
                    result.Append(bytes[offset + byteIndex].ToString("X2", CultureInfo.InvariantCulture));
                }
            }
            else
            {
                if (!decodeNonAsciiEscapes || !IsAllowedRawRune(rune))
                {
                    return false;
                }

                result.Append(rune.ToString());
            }

            offset += consumed;
        }

        return true;
    }

    private static bool IsAllowedRawRune(Rune rune)
    {
        if (rune.IsAscii)
        {
            char value = (char)rune.Value;
            return IsAsciiIunreserved(value)
                || value is '!' or '$' or '&' or '\'' or '(' or ')' or '*'
                    or '+' or ',' or ':' or ';' or '=' or '@';
        }

        UnicodeCategory category = Rune.GetUnicodeCategory(rune);
        return IsRfc3987UcsChar(rune.Value)
            && category is not UnicodeCategory.Control
            and not UnicodeCategory.Format
            and not UnicodeCategory.LineSeparator
            and not UnicodeCategory.ParagraphSeparator
            and not UnicodeCategory.SpaceSeparator;
    }

    private static bool IsRfc3987UcsChar(int value) =>
        value is >= 0xA0 and <= 0xD7FF
        or >= 0xF900 and <= 0xFDCF
        or >= 0xFDF0 and <= 0xFFEF
        or >= 0x10000 and <= 0x1FFFD
        or >= 0x20000 and <= 0x2FFFD
        or >= 0x30000 and <= 0x3FFFD
        or >= 0x40000 and <= 0x4FFFD
        or >= 0x50000 and <= 0x5FFFD
        or >= 0x60000 and <= 0x6FFFD
        or >= 0x70000 and <= 0x7FFFD
        or >= 0x80000 and <= 0x8FFFD
        or >= 0x90000 and <= 0x9FFFD
        or >= 0xA0000 and <= 0xAFFFD
        or >= 0xB0000 and <= 0xBFFFD
        or >= 0xC0000 and <= 0xCFFFD
        or >= 0xD0000 and <= 0xDFFFD
        or >= 0xE1000 and <= 0xEFFFD;

    private static bool IsAsciiIunreserved(char value) =>
        value is >= 'A' and <= 'Z'
        or >= 'a' and <= 'z'
        or >= '0' and <= '9'
        or '-' or '.' or '_' or '~';

    private static bool TryParseHexByte(char high, char low, out byte value)
    {
        int highValue = HexValue(high);
        int lowValue = HexValue(low);
        if (highValue < 0 || lowValue < 0)
        {
            value = 0;
            return false;
        }

        value = (byte)((highValue << 4) | lowValue);
        return true;
    }

    private static int HexValue(char value) => value switch
    {
        >= '0' and <= '9' => value - '0',
        >= 'A' and <= 'F' => value - 'A' + 10,
        >= 'a' and <= 'f' => value - 'a' + 10,
        _ => -1,
    };

    private static bool EndsWithAsciiIgnoreCase(
        ReadOnlySpan<char> value,
        ReadOnlySpan<char> suffix)
    {
        if (value.Length < suffix.Length)
        {
            return false;
        }

        return EqualsAsciiIgnoreCase(value[^suffix.Length..], suffix);
    }

    private static bool EqualsAsciiIgnoreCase(
        ReadOnlySpan<char> left,
        ReadOnlySpan<char> right)
    {
        if (left.Length != right.Length)
        {
            return false;
        }

        for (int index = 0; index < left.Length; index++)
        {
            char leftValue = left[index];
            char rightValue = right[index];
            if (leftValue is >= 'a' and <= 'z')
            {
                leftValue = (char)(leftValue - ('a' - 'A'));
            }

            if (rightValue is >= 'a' and <= 'z')
            {
                rightValue = (char)(rightValue - ('a' - 'A'));
            }

            if (leftValue != rightValue)
            {
                return false;
            }
        }

        return true;
    }
}
