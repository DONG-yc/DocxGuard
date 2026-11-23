using System.Xml;

namespace DocxGuard.Core.Packaging;

internal static class RelationshipPartParser
{
    private const string RelationshipsNamespace =
        "http://schemas.openxmlformats.org/package/2006/relationships";
    private const string XmlnsNamespace = "http://www.w3.org/2000/xmlns/";

    public static RelationshipParseResult Parse(
        ReadOnlyMemory<byte> xml,
        OpcPartName relationshipsPartName,
        OpcPartName? sourcePartName,
        OpcInspectionLimits limits,
        CancellationToken cancellationToken)
    {
        if (!OpcXmlSecurity.HasSupportedEncodingSignature(xml.Span))
        {
            return Failure(
                relationshipsPartName,
                sourcePartName,
                RelationshipFindingCodes.UnsupportedXmlEncoding,
                "The Relationships stream must use UTF-8 or UTF-16 encoding.");
        }

        XmlReaderSettings settings = OpcXmlSecurity.CreateReaderSettings(limits);
        List<ParsedRelationship> relationships = [];
        HashSet<string> ids = new(StringComparer.Ordinal);
        bool rootSeen = false;
        bool rootClosed = false;
        bool relationshipOpen = false;
        int nodeCount = 0;

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
                        relationshipsPartName,
                        sourcePartName,
                        RelationshipFindingCodes.ResourceLimitExceeded,
                        $"The Relationships stream exceeds the configured node limit of {limits.MaxXmlNodes}.");
                }

                if (reader.Depth > limits.MaxXmlDepth)
                {
                    return FailureAt(
                        reader,
                        relationshipsPartName,
                        sourcePartName,
                        RelationshipFindingCodes.ResourceLimitExceeded,
                        $"The Relationships stream exceeds the configured depth limit of {limits.MaxXmlDepth}.");
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
                                relationshipsPartName,
                                sourcePartName,
                                RelationshipFindingCodes.UnsupportedXmlEncoding,
                                "The Relationships stream declares an unsupported encoding.");
                        }

                        break;

                    case XmlNodeType.Element:
                        if (!rootSeen)
                        {
                            if (reader.Depth != 0
                                || reader.LocalName != "Relationships"
                                || reader.NamespaceURI != RelationshipsNamespace)
                            {
                                return FailureAt(
                                    reader,
                                    relationshipsPartName,
                                    sourcePartName,
                                    RelationshipFindingCodes.SchemaViolation,
                                    "The root element must be the OPC Relationships element.");
                            }

                            if (!TryReadAttributes(
                                reader,
                                relationshipsPartName,
                                sourcePartName,
                                limits,
                                [],
                                out _,
                                out RelationshipFinding rootFinding))
                            {
                                return new RelationshipParseResult(null, [rootFinding]);
                            }

                            rootSeen = true;
                            rootClosed = reader.IsEmptyElement;
                            break;
                        }

                        if (rootClosed
                            || relationshipOpen
                            || reader.Depth != 1
                            || reader.LocalName != "Relationship"
                            || reader.NamespaceURI != RelationshipsNamespace)
                        {
                            return FailureAt(
                                reader,
                                relationshipsPartName,
                                sourcePartName,
                                RelationshipFindingCodes.SchemaViolation,
                                "Relationships may contain only direct Relationship children.");
                        }

                        if (relationships.Count >= limits.MaxRelationshipsPerPart)
                        {
                            return FailureAt(
                                reader,
                                relationshipsPartName,
                                sourcePartName,
                                RelationshipFindingCodes.ResourceLimitExceeded,
                                $"The configured limit of {limits.MaxRelationshipsPerPart} relationships per part was exceeded.");
                        }

                        if (!TryParseRelationship(
                            reader,
                            relationshipsPartName,
                            sourcePartName,
                            limits,
                            ids,
                            out ParsedRelationship parsed,
                            out RelationshipFinding relationshipFinding))
                        {
                            return new RelationshipParseResult(null, [relationshipFinding]);
                        }

                        relationships.Add(parsed);
                        relationshipOpen = !reader.IsEmptyElement;
                        break;

                    case XmlNodeType.EndElement:
                        if (reader.Depth == 1 && relationshipOpen)
                        {
                            relationshipOpen = false;
                        }
                        else if (reader.Depth == 0)
                        {
                            rootClosed = true;
                        }
                        else
                        {
                            return FailureAt(
                                reader,
                                relationshipsPartName,
                                sourcePartName,
                                RelationshipFindingCodes.SchemaViolation,
                                "The Relationships stream contains an unexpected closing element.");
                        }

                        break;

                    case XmlNodeType.Text:
                    case XmlNodeType.SignificantWhitespace:
                        if (relationshipOpen || !string.IsNullOrWhiteSpace(reader.Value))
                        {
                            return FailureAt(
                                reader,
                                relationshipsPartName,
                                sourcePartName,
                                RelationshipFindingCodes.SchemaViolation,
                                "Relationships elements cannot contain text content.");
                        }

                        break;

                    case XmlNodeType.CDATA:
                    case XmlNodeType.EntityReference:
                        return FailureAt(
                            reader,
                            relationshipsPartName,
                            sourcePartName,
                            RelationshipFindingCodes.SchemaViolation,
                            "Relationships elements cannot contain CDATA or entity references.");

                    case XmlNodeType.Comment:
                    case XmlNodeType.ProcessingInstruction:
                    case XmlNodeType.Whitespace:
                        break;

                    default:
                        return FailureAt(
                            reader,
                            relationshipsPartName,
                            sourcePartName,
                            RelationshipFindingCodes.SchemaViolation,
                            $"XML node type '{reader.NodeType}' is not permitted.");
                }
            }
        }
        catch (XmlException exception)
        {
            return Failure(
                relationshipsPartName,
                sourcePartName,
                RelationshipFindingCodes.XmlReaderRejected,
                "The Relationships stream was rejected by the bounded XML reader.",
                exception.LineNumber,
                exception.LinePosition);
        }

        if (!rootSeen || !rootClosed || relationshipOpen)
        {
            return Failure(
                relationshipsPartName,
                sourcePartName,
                RelationshipFindingCodes.SchemaViolation,
                "The stream does not contain one complete Relationships root element.");
        }

        return new RelationshipParseResult(relationships, []);
    }

    private static bool TryParseRelationship(
        XmlReader reader,
        OpcPartName relationshipsPartName,
        OpcPartName? sourcePartName,
        OpcInspectionLimits limits,
        ISet<string> ids,
        out ParsedRelationship relationship,
        out RelationshipFinding finding)
    {
        relationship = default!;
        if (!TryReadAttributes(
            reader,
            relationshipsPartName,
            sourcePartName,
            limits,
            ["Id", "Type", "Target", "TargetMode"],
            out IReadOnlyDictionary<string, string> attributes,
            out finding))
        {
            return false;
        }

        if (!attributes.TryGetValue("Id", out string? id)
            || !attributes.TryGetValue("Type", out string? type)
            || !attributes.TryGetValue("Target", out string? target))
        {
            finding = CreateFindingAt(
                reader,
                relationshipsPartName,
                sourcePartName,
                RelationshipFindingCodes.SchemaViolation,
                "A Relationship requires Id, Type, and Target attributes.");
            return false;
        }

        if (!IsControlledXmlId(id))
        {
            finding = CreateFindingAt(
                reader,
                relationshipsPartName,
                sourcePartName,
                RelationshipFindingCodes.InvalidId,
                "The Relationship Id is outside the controlled ASCII xsd:ID profile.",
                id,
                target);
            return false;
        }

        if (!ids.Add(id))
        {
            finding = CreateFindingAt(
                reader,
                relationshipsPartName,
                sourcePartName,
                RelationshipFindingCodes.DuplicateId,
                "Relationship Id values must be unique within one Relationships part.",
                id,
                target);
            return false;
        }

        if (!IsValidRelationshipType(type))
        {
            finding = CreateFindingAt(
                reader,
                relationshipsPartName,
                sourcePartName,
                RelationshipFindingCodes.InvalidType,
                "The Relationship Type must be a well-formed absolute URI.",
                id,
                target);
            return false;
        }

        RelationshipTargetMode targetMode = RelationshipTargetMode.Internal;
        if (attributes.TryGetValue("TargetMode", out string? mode))
        {
            if (mode == "External")
            {
                targetMode = RelationshipTargetMode.External;
            }
            else if (mode != "Internal")
            {
                finding = CreateFindingAt(
                    reader,
                    relationshipsPartName,
                    sourcePartName,
                    RelationshipFindingCodes.InvalidTargetMode,
                    "TargetMode must be exactly 'Internal' or 'External'.",
                    id,
                    target);
                return false;
            }
        }

        relationship = new ParsedRelationship(id, type, target, targetMode);
        finding = null!;
        return true;
    }

    private static bool TryReadAttributes(
        XmlReader reader,
        OpcPartName relationshipsPartName,
        OpcPartName? sourcePartName,
        OpcInspectionLimits limits,
        string[] allowedNames,
        out IReadOnlyDictionary<string, string> attributes,
        out RelationshipFinding finding)
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
                        relationshipsPartName,
                        sourcePartName,
                        RelationshipFindingCodes.ResourceLimitExceeded,
                        $"An attribute exceeds the configured limit of {limits.MaxAttributeValueCharacters} characters.");
                    reader.MoveToElement();
                    return false;
                }

                if (reader.NamespaceURI == XmlnsNamespace)
                {
                    continue;
                }

                if (reader.NamespaceURI.Length != 0
                    || !allowedNames.Contains(reader.LocalName)
                    || !values.TryAdd(reader.LocalName, reader.Value))
                {
                    attributes = values;
                    finding = CreateFindingAt(
                        reader,
                        relationshipsPartName,
                        sourcePartName,
                        RelationshipFindingCodes.SchemaViolation,
                        $"Attribute '{reader.Name}' is not permitted on this Relationships element.");
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

    private static bool IsControlledXmlId(string value)
    {
        if (value.Length == 0 || !IsIdStart(value[0]))
        {
            return false;
        }

        return value[1..].All(character => IsIdStart(character)
            || character is >= '0' and <= '9'
            || character is '.' or '-');
    }

    private static bool IsIdStart(char value) =>
        value is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or '_';

    private static bool IsValidRelationshipType(string value) =>
        value.Length != 0
        && !value.Any(character => char.IsWhiteSpace(character)
            || char.IsControl(character))
        && Uri.TryCreate(value, UriKind.Absolute, out Uri? type)
        && type.Scheme.Length != 0;

    private static RelationshipParseResult Failure(
        OpcPartName relationshipsPartName,
        OpcPartName? sourcePartName,
        string code,
        string message,
        int? lineNumber = null,
        int? linePosition = null) =>
        new(
            null,
            [
                new RelationshipFinding(
                    code,
                    FindingSeverity.Error,
                    message,
                    relationshipsPartName.Value,
                    sourcePartName?.Value,
                    LineNumber: NormalizeLineNumber(lineNumber),
                    LinePosition: NormalizeLineNumber(linePosition)),
            ]);

    private static RelationshipParseResult FailureAt(
        XmlReader reader,
        OpcPartName relationshipsPartName,
        OpcPartName? sourcePartName,
        string code,
        string message) =>
        new(
            null,
            [
                CreateFindingAt(
                    reader,
                    relationshipsPartName,
                    sourcePartName,
                    code,
                    message),
            ]);

    private static RelationshipFinding CreateFindingAt(
        XmlReader reader,
        OpcPartName relationshipsPartName,
        OpcPartName? sourcePartName,
        string code,
        string message,
        string? relationshipId = null,
        string? rawTarget = null)
    {
        IXmlLineInfo lineInfo = (IXmlLineInfo)reader;
        return new RelationshipFinding(
            code,
            FindingSeverity.Error,
            message,
            relationshipsPartName.Value,
            sourcePartName?.Value,
            relationshipId,
            rawTarget,
            lineInfo.HasLineInfo() ? lineInfo.LineNumber : null,
            lineInfo.HasLineInfo() ? lineInfo.LinePosition : null);
    }

    private static int? NormalizeLineNumber(int? value) => value > 0 ? value : null;
}

internal sealed record RelationshipParseResult(
    IReadOnlyList<ParsedRelationship>? Relationships,
    IReadOnlyList<RelationshipFinding> Findings);

internal sealed record ParsedRelationship(
    string Id,
    string Type,
    string Target,
    RelationshipTargetMode TargetMode);
