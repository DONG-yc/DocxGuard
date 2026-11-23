using System.Xml;
using DocxGuard.Core.Packaging;
using DocxGuard.Core.Wordprocessing;

namespace DocxGuard.Core.Indexing;

internal static class WordDocumentIndexer
{
    public static DocumentIndexParseResult Parse(
        ReadOnlyMemory<byte> xml,
        OpcPartName partName,
        WordprocessingConformance conformance,
        WordprocessingInspectionLimits wordprocessingLimits,
        DocumentIndexLimits indexLimits,
        CancellationToken cancellationToken)
    {
        string wordNamespace = conformance == WordprocessingConformance.Strict
            ? WordMainDocumentParser.StrictNamespace
            : WordMainDocumentParser.TransitionalNamespace;
        XmlReaderSettings settings = OpcXmlSecurity.CreateReaderSettings(
            wordprocessingLimits.MaxXmlCharacters);
        DocumentIndexBuildState state = new(
            partName,
            wordNamespace,
            indexLimits);
        int nodeCount = 0;

        try
        {
            using MemoryStream stream = new(xml.ToArray(), writable: false);
            using XmlReader reader = XmlReader.Create(stream, settings);
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                nodeCount++;
                if (nodeCount > wordprocessingLimits.MaxXmlNodes)
                {
                    return state.FailureAt(
                        reader,
                        DocumentIndexFindingCodes.ResourceLimitExceeded,
                        $"The main document exceeds the node limit of {wordprocessingLimits.MaxXmlNodes}.");
                }

                if (reader.Depth > wordprocessingLimits.MaxXmlDepth)
                {
                    return state.FailureAt(
                        reader,
                        DocumentIndexFindingCodes.ResourceLimitExceeded,
                        $"The main document exceeds the depth limit of {wordprocessingLimits.MaxXmlDepth}.");
                }

                DocumentIndexParseResult? failure = reader.NodeType switch
                {
                    XmlNodeType.Element => state.HandleStart(reader),
                    XmlNodeType.Text
                        or XmlNodeType.CDATA
                        or XmlNodeType.SignificantWhitespace
                        or XmlNodeType.Whitespace =>
                        state.HandleText(reader),
                    XmlNodeType.EndElement => state.HandleEnd(reader),
                    _ => null,
                };
                if (failure is not null)
                {
                    return failure;
                }
            }
        }
        catch (XmlException exception)
        {
            return state.Failure(
                DocumentIndexFindingCodes.XmlReaderRejected,
                "The verified main document was rejected while building the index.",
                exception.LineNumber,
                exception.LinePosition);
        }

        return state.Build();
    }
}

internal sealed record DocumentIndexParseResult(
    WordDocumentIndex? Index,
    IReadOnlyList<DocumentIndexFinding> Findings);
