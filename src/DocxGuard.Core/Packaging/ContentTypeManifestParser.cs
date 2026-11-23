using System.Xml;

namespace DocxGuard.Core.Packaging;

internal static class ContentTypeManifestParser
{
    private const string ContentTypesEntryName = "[Content_Types].xml";
    private const string ContentTypesNamespace =
        "http://schemas.openxmlformats.org/package/2006/content-types";
    private const string XmlnsNamespace = "http://www.w3.org/2000/xmlns/";

    private static readonly HashSet<string> OpcMediaTypesWithoutParameters = new(
        AsciiCaseInsensitiveComparer.Instance)
    {
        ContentTypeManifest.RelationshipsContentType,
        "application/vnd.openxmlformats-package.core-properties+xml",
        "application/vnd.openxmlformats-package.digital-signature-origin",
        "application/vnd.openxmlformats-package.digital-signature-xmlsignature+xml",
        "application/vnd.openxmlformats-package.digital-signature-certificate",
    };

    public static ManifestParseResult Parse(
        ReadOnlyMemory<byte> xml,
        OpcInspectionLimits limits,
        CancellationToken cancellationToken)
    {
        if (!OpcXmlSecurity.HasSupportedEncodingSignature(xml.Span))
        {
            return Failure(
                ContentTypeFindingCodes.UnsupportedXmlEncoding,
                "The Content Types stream must use UTF-8 or UTF-16 encoding.");
        }

        XmlReaderSettings settings = OpcXmlSecurity.CreateReaderSettings(limits);

        List<DefaultContentType> defaults = [];
        List<OverrideContentType> overrides = [];
        HashSet<string> defaultKeys = new(AsciiCaseInsensitiveComparer.Instance);
        HashSet<OpcPartName> overrideKeys = [];
        bool rootSeen = false;
        bool rootClosed = false;
        int nodeCount = 0;
        int mappingCount = 0;

        try
        {
            using MemoryStream stream = new(xml.ToArray(), writable: false);
            using XmlReader reader = XmlReader.Create(stream, settings);

            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                nodeCount++;

                if (nodeCount > limits.MaxXmlNodes)
                {
                    return FailureAt(
                        reader,
                        ContentTypeFindingCodes.ResourceLimitExceeded,
                        $"The Content Types stream exceeds the configured node limit of {limits.MaxXmlNodes}.");
                }

                if (reader.Depth > limits.MaxXmlDepth)
                {
                    return FailureAt(
                        reader,
                        ContentTypeFindingCodes.ResourceLimitExceeded,
                        $"The Content Types stream exceeds the configured depth limit of {limits.MaxXmlDepth}.");
                }

                switch (reader.NodeType)
                {
                    case XmlNodeType.XmlDeclaration:
                        string? encoding = reader.GetAttribute("encoding");
                        if (encoding is not null
                            && !OpcXmlSecurity.IsSupportedEncodingName(encoding))
                        {
                            return FailureAt(
                                reader,
                                ContentTypeFindingCodes.UnsupportedXmlEncoding,
                                "The Content Types stream declares an encoding other than UTF-8 or UTF-16.");
                        }

                        break;

                    case XmlNodeType.Element:
                        if (!rootSeen)
                        {
                            if (reader.Depth != 0
                                || reader.LocalName != "Types"
                                || reader.NamespaceURI != ContentTypesNamespace)
                            {
                                return FailureAt(
                                    reader,
                                    ContentTypeFindingCodes.SchemaViolation,
                                    "The root element must be the OPC Content Types 'Types' element.");
                            }

                            if (!TryReadAttributes(
                                reader,
                                limits,
                                allowedAttributeNames: [],
                                out _,
                                out ContentTypeFinding rootAttributeFinding))
                            {
                                return new ManifestParseResult(null, [rootAttributeFinding]);
                            }

                            rootSeen = true;
                            rootClosed = reader.IsEmptyElement;
                            break;
                        }

                        if (rootClosed
                            || reader.Depth != 1
                            || reader.NamespaceURI != ContentTypesNamespace
                            || (reader.LocalName != "Default" && reader.LocalName != "Override"))
                        {
                            return FailureAt(
                                reader,
                                ContentTypeFindingCodes.SchemaViolation,
                                "The Types element may contain only direct Default and Override children.");
                        }

                        mappingCount++;
                        if (mappingCount > limits.MaxContentTypeMappings)
                        {
                            return FailureAt(
                                reader,
                                ContentTypeFindingCodes.ResourceLimitExceeded,
                                $"The Content Types stream exceeds the configured mapping limit of {limits.MaxContentTypeMappings}.");
                        }

                        if (reader.LocalName == "Default")
                        {
                            ManifestParseResult? defaultFailure = ParseDefault(
                                reader,
                                limits,
                                defaultKeys,
                                defaults);
                            if (defaultFailure is not null)
                            {
                                return defaultFailure;
                            }
                        }
                        else
                        {
                            ManifestParseResult? overrideFailure = ParseOverride(
                                reader,
                                limits,
                                overrideKeys,
                                overrides);
                            if (overrideFailure is not null)
                            {
                                return overrideFailure;
                            }
                        }

                        break;

                    case XmlNodeType.EndElement:
                        if (reader.Depth == 0)
                        {
                            rootClosed = true;
                        }

                        break;

                    case XmlNodeType.Text:
                        if (!string.IsNullOrWhiteSpace(reader.Value))
                        {
                            return FailureAt(
                                reader,
                                ContentTypeFindingCodes.SchemaViolation,
                                "Content Types elements cannot contain text content.");
                        }

                        break;

                    case XmlNodeType.CDATA:
                    case XmlNodeType.EntityReference:
                        return FailureAt(
                            reader,
                            ContentTypeFindingCodes.SchemaViolation,
                            "Content Types elements cannot contain CDATA or entity-reference content.");

                    case XmlNodeType.Comment:
                    case XmlNodeType.ProcessingInstruction:
                    case XmlNodeType.Whitespace:
                    case XmlNodeType.SignificantWhitespace:
                        break;

                    default:
                        return FailureAt(
                            reader,
                            ContentTypeFindingCodes.SchemaViolation,
                            $"XML node type '{reader.NodeType}' is not permitted in the Content Types stream.");
                }
            }
        }
        catch (XmlException exception)
        {
            return Failure(
                ContentTypeFindingCodes.XmlReaderRejected,
                "The Content Types stream was rejected by the bounded XML reader.",
                exception.LineNumber,
                exception.LinePosition);
        }

        if (!rootSeen || !rootClosed)
        {
            return Failure(
                ContentTypeFindingCodes.SchemaViolation,
                "The Content Types stream does not contain one complete Types root element.");
        }

        if (mappingCount == 0)
        {
            return Failure(
                ContentTypeFindingCodes.SchemaViolation,
                "The Types element must contain at least one Default or Override mapping.");
        }

        return new ManifestParseResult(
            new ContentTypeManifest(defaults, overrides),
            []);
    }

    private static ManifestParseResult? ParseDefault(
        XmlReader reader,
        OpcInspectionLimits limits,
        ISet<string> defaultKeys,
        ICollection<DefaultContentType> defaults)
    {
        if (!TryReadAttributes(
            reader,
            limits,
            allowedAttributeNames: ["Extension", "ContentType"],
            out IReadOnlyDictionary<string, string> attributes,
            out ContentTypeFinding finding))
        {
            return new ManifestParseResult(null, [finding]);
        }

        if (!attributes.TryGetValue("Extension", out string? extension)
            || !attributes.TryGetValue("ContentType", out string? contentType))
        {
            return FailureAt(
                reader,
                ContentTypeFindingCodes.SchemaViolation,
                "A Default element requires Extension and ContentType attributes.");
        }

        if (!IsValidDefaultExtension(extension))
        {
            return FailureAt(
                reader,
                ContentTypeFindingCodes.InvalidDefaultExtension,
                "The Default Extension value does not conform to the OPC extension grammar.");
        }

        ManifestParseResult? contentTypeFailure = ValidateContentType(reader, contentType);
        if (contentTypeFailure is not null)
        {
            return contentTypeFailure;
        }

        if (!defaultKeys.Add(extension))
        {
            return FailureAt(
                reader,
                ContentTypeFindingCodes.DuplicateDefault,
                $"The extension '{extension}' has more than one ASCII-case-insensitive Default mapping.");
        }

        defaults.Add(new DefaultContentType(extension, contentType));
        return null;
    }

    private static ManifestParseResult? ParseOverride(
        XmlReader reader,
        OpcInspectionLimits limits,
        ISet<OpcPartName> overrideKeys,
        ICollection<OverrideContentType> overrides)
    {
        if (!TryReadAttributes(
            reader,
            limits,
            allowedAttributeNames: ["PartName", "ContentType"],
            out IReadOnlyDictionary<string, string> attributes,
            out ContentTypeFinding finding))
        {
            return new ManifestParseResult(null, [finding]);
        }

        if (!attributes.TryGetValue("PartName", out string? partNameValue)
            || !attributes.TryGetValue("ContentType", out string? contentType))
        {
            return FailureAt(
                reader,
                ContentTypeFindingCodes.SchemaViolation,
                "An Override element requires PartName and ContentType attributes.");
        }

        if (!OpcPartName.TryParse(partNameValue, out OpcPartName? partName))
        {
            return FailureAt(
                reader,
                ContentTypeFindingCodes.InvalidOverridePartName,
                "The Override PartName value is not a valid canonical OPC part name.",
                partName: partNameValue);
        }

        ManifestParseResult? contentTypeFailure = ValidateContentType(reader, contentType);
        if (contentTypeFailure is not null)
        {
            return contentTypeFailure;
        }

        if (!overrideKeys.Add(partName))
        {
            return FailureAt(
                reader,
                ContentTypeFindingCodes.DuplicateOverride,
                $"The part '{partName.Value}' has more than one ASCII-case-insensitive Override mapping.",
                partName: partName.Value);
        }

        overrides.Add(new OverrideContentType(partName, contentType));
        return null;
    }

    private static ManifestParseResult? ValidateContentType(
        XmlReader reader,
        string contentType)
    {
        if (!MediaTypeParser.TryParse(contentType, out ParsedMediaType parsed))
        {
            return FailureAt(
                reader,
                ContentTypeFindingCodes.InvalidMediaType,
                "The ContentType value is not a valid RFC media type.");
        }

        if (parsed.HasParameters
            && OpcMediaTypesWithoutParameters.Contains(parsed.TypeAndSubtype))
        {
            return FailureAt(
                reader,
                ContentTypeFindingCodes.OpcMediaTypeParametersProhibited,
                "Parameters are prohibited on this OPC-defined content type.");
        }

        return null;
    }

    private static bool TryReadAttributes(
        XmlReader reader,
        OpcInspectionLimits limits,
        string[] allowedAttributeNames,
        out IReadOnlyDictionary<string, string> attributes,
        out ContentTypeFinding finding)
    {
        Dictionary<string, string> values = new(StringComparer.Ordinal);

        if (reader.MoveToFirstAttribute())
        {
            do
            {
                if (reader.Value.Length > limits.MaxAttributeValueCharacters)
                {
                    attributes = values;
                    finding = CreateFindingAt(
                        reader,
                        ContentTypeFindingCodes.ResourceLimitExceeded,
                        $"An attribute value exceeds the configured limit of {limits.MaxAttributeValueCharacters} characters.");
                    reader.MoveToElement();
                    return false;
                }

                if (reader.NamespaceURI == XmlnsNamespace)
                {
                    continue;
                }

                if (reader.NamespaceURI.Length != 0
                    || !allowedAttributeNames.Contains(reader.LocalName)
                    || !values.TryAdd(reader.LocalName, reader.Value))
                {
                    attributes = values;
                    finding = CreateFindingAt(
                        reader,
                        ContentTypeFindingCodes.SchemaViolation,
                        $"Attribute '{reader.Name}' is not permitted on this Content Types element.");
                    reader.MoveToElement();
                    return false;
                }

            }
            while (reader.MoveToNextAttribute());

            reader.MoveToElement();
        }

        attributes = values;
        finding = null!;
        return true;
    }

    private static bool IsValidDefaultExtension(string value)
    {
        if (value.Length == 0)
        {
            return false;
        }

        for (int index = 0; index < value.Length; index++)
        {
            char character = value[index];
            if (IsExtensionCharacter(character))
            {
                continue;
            }

            if (character != '%'
                || index + 2 >= value.Length
                || !IsHexadecimal(value[index + 1])
                || !IsHexadecimal(value[index + 2]))
            {
                return false;
            }

            index += 2;
        }

        return true;
    }

    private static bool IsExtensionCharacter(char value) =>
        value is >= 'A' and <= 'Z'
        or >= 'a' and <= 'z'
        or >= '0' and <= '9'
        or '!' or '$' or '&' or '\'' or '(' or ')' or '*' or '+' or ','
        or ':' or '=' or '@' or '-' or '_' or '~';

    private static bool IsHexadecimal(char value) => value is >= '0' and <= '9'
        or >= 'A' and <= 'F'
        or >= 'a' and <= 'f';

    private static ManifestParseResult Failure(
        string code,
        string message,
        int? lineNumber = null,
        int? linePosition = null) =>
        new(
            null,
            [
                new ContentTypeFinding(
                    code,
                    FindingSeverity.Error,
                    message,
                    ContentTypesEntryName,
                    LineNumber: NormalizeLineNumber(lineNumber),
                    LinePosition: NormalizeLineNumber(linePosition)),
            ]);

    private static ManifestParseResult FailureAt(
        XmlReader reader,
        string code,
        string message,
        string? partName = null) =>
        new(null, [CreateFindingAt(reader, code, message, partName)]);

    private static ContentTypeFinding CreateFindingAt(
        XmlReader reader,
        string code,
        string message,
        string? partName = null)
    {
        IXmlLineInfo lineInfo = (IXmlLineInfo)reader;
        return new ContentTypeFinding(
            code,
            FindingSeverity.Error,
            message,
            ContentTypesEntryName,
            partName,
            lineInfo.HasLineInfo() ? lineInfo.LineNumber : null,
            lineInfo.HasLineInfo() ? lineInfo.LinePosition : null);
    }

    private static int? NormalizeLineNumber(int? value) => value > 0 ? value : null;
}

internal sealed record ManifestParseResult(
    ContentTypeManifest? Manifest,
    IReadOnlyList<ContentTypeFinding> Findings);

internal readonly record struct ParsedMediaType(
    string TypeAndSubtype,
    bool HasParameters);

internal static class MediaTypeParser
{
    public static bool TryParse(string value, out ParsedMediaType mediaType)
    {
        mediaType = default;
        if (value.Length == 0
            || value[0] == ' '
            || value[^1] == ' '
            || value.Any(character => character > 0x7E || char.IsControl(character)))
        {
            return false;
        }

        int position = 0;
        if (!ReadToken(value, ref position, out _)
            || position >= value.Length
            || value[position] != '/')
        {
            return false;
        }

        position++;
        if (!ReadToken(value, ref position, out _))
        {
            return false;
        }

        int typeAndSubtypeLength = position;
        bool hasParameters = false;

        while (position < value.Length)
        {
            SkipSpaces(value, ref position);
            if (position >= value.Length || value[position] != ';')
            {
                return false;
            }

            hasParameters = true;
            position++;
            SkipSpaces(value, ref position);

            if (!ReadToken(value, ref position, out _)
                || position >= value.Length
                || value[position] != '=')
            {
                return false;
            }

            position++;
            if (position >= value.Length)
            {
                return false;
            }

            if (value[position] == '"')
            {
                if (!ReadQuotedString(value, ref position))
                {
                    return false;
                }
            }
            else if (!ReadToken(value, ref position, out _))
            {
                return false;
            }
        }

        mediaType = new ParsedMediaType(
            value[..typeAndSubtypeLength],
            hasParameters);
        return true;
    }

    private static bool ReadToken(string value, ref int position, out int length)
    {
        int start = position;
        while (position < value.Length && IsTokenCharacter(value[position]))
        {
            position++;
        }

        length = position - start;
        return length > 0;
    }

    private static bool ReadQuotedString(string value, ref int position)
    {
        position++;
        while (position < value.Length)
        {
            char character = value[position++];
            if (character == '"')
            {
                return true;
            }

            if (character == '\\')
            {
                if (position >= value.Length
                    || value[position] < 0x20
                    || value[position] > 0x7E)
                {
                    return false;
                }

                position++;
                continue;
            }

            if (character < 0x20 || character > 0x7E)
            {
                return false;
            }
        }

        return false;
    }

    private static bool IsTokenCharacter(char value) => value is
        >= 'A' and <= 'Z'
        or >= 'a' and <= 'z'
        or >= '0' and <= '9'
        or '!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+' or '-'
        or '.' or '^' or '_' or '`' or '|' or '~';

    private static void SkipSpaces(string value, ref int position)
    {
        while (position < value.Length && value[position] == ' ')
        {
            position++;
        }
    }
}
