using System.Xml;
using DocxGuard.Core.Packaging;

namespace DocxGuard.Core.Wordprocessing;

internal static class WordMainDocumentParser
{
    internal const string TransitionalNamespace =
        "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    internal const string StrictNamespace =
        "http://purl.oclc.org/ooxml/wordprocessingml/main";
    private const string MarkupCompatibilityNamespace =
        "http://schemas.openxmlformats.org/markup-compatibility/2006";

    public static WordMainDocumentParseResult Parse(
        ReadOnlyMemory<byte> xml,
        OpcPartName partName,
        WordprocessingConformance conformance,
        WordprocessingInspectionLimits limits,
        CancellationToken cancellationToken)
    {
        if (!OpcXmlSecurity.HasSupportedEncodingSignature(xml.Span))
        {
            return Failure(
                partName,
                WordprocessingFindingCodes.UnsupportedXmlEncoding,
                "The main document must use UTF-8 or UTF-16 encoding.");
        }

        string expectedNamespace = conformance == WordprocessingConformance.Strict
            ? StrictNamespace
            : TransitionalNamespace;
        XmlReaderSettings settings = OpcXmlSecurity.CreateReaderSettings(
            limits.MaxXmlCharacters);
        bool rootSeen = false;
        bool rootClosed = false;
        bool bodySeen = false;
        bool bodyOpen = false;
        bool backgroundSeen = false;
        bool sectionPropertiesSeen = false;
        int bodyChildElementCount = 0;
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
                        partName,
                        WordprocessingFindingCodes.ResourceLimitExceeded,
                        $"The main document exceeds the node limit of {limits.MaxXmlNodes}.");
                }

                if (reader.Depth > limits.MaxXmlDepth)
                {
                    return FailureAt(
                        reader,
                        partName,
                        WordprocessingFindingCodes.ResourceLimitExceeded,
                        $"The main document exceeds the depth limit of {limits.MaxXmlDepth}.");
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
                                partName,
                                WordprocessingFindingCodes.UnsupportedXmlEncoding,
                                "The main document declares an unsupported encoding.");
                        }

                        break;

                    case XmlNodeType.Element:
                        WordMainDocumentParseResult? attributeFailure =
                            ValidateAttributeLimits(reader, partName, limits);
                        if (attributeFailure is not null)
                        {
                            return attributeFailure;
                        }

                        if (!rootSeen)
                        {
                            if (reader.Depth != 0 || reader.LocalName != "document")
                            {
                                return FailureAt(
                                    reader,
                                    partName,
                                    WordprocessingFindingCodes.SchemaViolation,
                                    "The root must be a WordprocessingML document element.");
                            }

                            if (reader.NamespaceURI != expectedNamespace)
                            {
                                string code = IsWordprocessingNamespace(
                                    reader.NamespaceURI)
                                    ? WordprocessingFindingCodes.ConformanceMismatch
                                    : WordprocessingFindingCodes.SchemaViolation;
                                return FailureAt(
                                    reader,
                                    partName,
                                    code,
                                    "The document namespace does not match the package conformance family.");
                            }

                            rootSeen = true;
                            rootClosed = reader.IsEmptyElement;
                            break;
                        }

                        if (rootClosed)
                        {
                            return FailureAt(
                                reader,
                                partName,
                                WordprocessingFindingCodes.SchemaViolation,
                                "No element may follow the closed document root.");
                        }

                        if (reader.Depth == 1)
                        {
                            if (reader.LocalName == "body"
                                && reader.NamespaceURI == expectedNamespace)
                            {
                                if (bodySeen)
                                {
                                    return FailureAt(
                                        reader,
                                        partName,
                                        WordprocessingFindingCodes.SchemaViolation,
                                        "The document must contain exactly one body.");
                                }

                                bodySeen = true;
                                bodyOpen = !reader.IsEmptyElement;
                                break;
                            }

                            if (!bodySeen
                                && !backgroundSeen
                                && reader.LocalName == "background"
                                && reader.NamespaceURI == expectedNamespace)
                            {
                                backgroundSeen = true;
                                break;
                            }

                            return FailureAt(
                                reader,
                                partName,
                                WordprocessingFindingCodes.SchemaViolation,
                                "Only an optional background followed by one body is allowed under document.");
                        }

                        if (bodyOpen && reader.Depth == 2)
                        {
                            bodyChildElementCount++;
                            if (bodyChildElementCount > limits.MaxBodyChildElements)
                            {
                                return FailureAt(
                                    reader,
                                    partName,
                                    WordprocessingFindingCodes.ResourceLimitExceeded,
                                    $"The body exceeds the child-element limit of {limits.MaxBodyChildElements}.");
                            }

                            bool isSectionProperties =
                                reader.LocalName == "sectPr"
                                && reader.NamespaceURI == expectedNamespace;
                            if (sectionPropertiesSeen)
                            {
                                return FailureAt(
                                    reader,
                                    partName,
                                    WordprocessingFindingCodes.SchemaViolation,
                                    "Section properties must be the final direct body child.");
                            }

                            if (!isSectionProperties
                                && reader.NamespaceURI != expectedNamespace
                                && (reader.LocalName != "AlternateContent"
                                    || reader.NamespaceURI
                                        != MarkupCompatibilityNamespace))
                            {
                                return FailureAt(
                                    reader,
                                    partName,
                                    WordprocessingFindingCodes.SchemaViolation,
                                    "A direct body child is outside the controlled WordprocessingML profile.");
                            }

                            sectionPropertiesSeen = isSectionProperties;
                        }

                        break;

                    case XmlNodeType.EndElement:
                        if (reader.Depth == 1
                            && reader.LocalName == "body"
                            && reader.NamespaceURI == expectedNamespace)
                        {
                            bodyOpen = false;
                        }
                        else if (reader.Depth == 0)
                        {
                            rootClosed = true;
                        }

                        break;

                    case XmlNodeType.Text:
                    case XmlNodeType.CDATA:
                    case XmlNodeType.SignificantWhitespace:
                        if (!string.IsNullOrWhiteSpace(reader.Value)
                            && ((!bodyOpen && reader.Depth == 1)
                                || (bodyOpen && reader.Depth == 2)))
                        {
                            return FailureAt(
                                reader,
                                partName,
                                WordprocessingFindingCodes.SchemaViolation,
                                "The document or body cannot contain direct text.");
                        }

                        break;

                    case XmlNodeType.EntityReference:
                        return FailureAt(
                            reader,
                            partName,
                            WordprocessingFindingCodes.SchemaViolation,
                            "Entity-reference content is not permitted.");

                    case XmlNodeType.Comment:
                    case XmlNodeType.ProcessingInstruction:
                    case XmlNodeType.Whitespace:
                        break;

                    default:
                        return FailureAt(
                            reader,
                            partName,
                            WordprocessingFindingCodes.SchemaViolation,
                            $"XML node type '{reader.NodeType}' is not permitted.");
                }
            }
        }
        catch (XmlException exception)
        {
            return Failure(
                partName,
                WordprocessingFindingCodes.XmlReaderRejected,
                "The main document was rejected by the bounded XML reader.",
                exception.LineNumber,
                exception.LinePosition);
        }

        if (!rootSeen || !rootClosed || !bodySeen || bodyOpen)
        {
            return Failure(
                partName,
                WordprocessingFindingCodes.SchemaViolation,
                "The main document must contain one complete document root and body.");
        }

        return new WordMainDocumentParseResult(
            new WordMainDocumentStructure(
                bodyChildElementCount,
                sectionPropertiesSeen),
            []);
    }

    private static WordMainDocumentParseResult? ValidateAttributeLimits(
        XmlReader reader,
        OpcPartName partName,
        WordprocessingInspectionLimits limits)
    {
        int attributeCount = 0;
        if (reader.MoveToFirstAttribute())
        {
            do
            {
                attributeCount++;
                if (attributeCount > limits.MaxAttributesPerElement)
                {
                    reader.MoveToElement();
                    return FailureAt(
                        reader,
                        partName,
                        WordprocessingFindingCodes.ResourceLimitExceeded,
                        $"An element exceeds the attribute limit of {limits.MaxAttributesPerElement}.");
                }

                if (reader.Value.Length > limits.MaxAttributeValueCharacters)
                {
                    reader.MoveToElement();
                    return FailureAt(
                        reader,
                        partName,
                        WordprocessingFindingCodes.ResourceLimitExceeded,
                        $"An attribute exceeds the value limit of {limits.MaxAttributeValueCharacters} characters.");
                }
            }
            while (reader.MoveToNextAttribute());

            reader.MoveToElement();
        }

        return null;
    }

    private static bool IsWordprocessingNamespace(string value) =>
        value == TransitionalNamespace || value == StrictNamespace;

    private static WordMainDocumentParseResult Failure(
        OpcPartName partName,
        string code,
        string message,
        int? lineNumber = null,
        int? linePosition = null) =>
        new(
            null,
            [
                new WordprocessingFinding(
                    code,
                    FindingSeverity.Error,
                    message,
                    partName.Value,
                    LineNumber: NormalizeLineNumber(lineNumber),
                    LinePosition: NormalizeLineNumber(linePosition)),
            ]);

    private static WordMainDocumentParseResult FailureAt(
        XmlReader reader,
        OpcPartName partName,
        string code,
        string message)
    {
        IXmlLineInfo lineInfo = (IXmlLineInfo)reader;
        return Failure(
            partName,
            code,
            message,
            lineInfo.HasLineInfo() ? lineInfo.LineNumber : null,
            lineInfo.HasLineInfo() ? lineInfo.LinePosition : null);
    }

    private static int? NormalizeLineNumber(int? value) => value > 0 ? value : null;
}

internal sealed record WordMainDocumentParseResult(
    WordMainDocumentStructure? Structure,
    IReadOnlyList<WordprocessingFinding> Findings);

internal sealed record WordMainDocumentStructure(
    int BodyChildElementCount,
    bool HasSectionProperties);
