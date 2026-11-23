using System.Text;
using System.Xml;
using System.Xml.Linq;
using DocxGuard.Core.Indexing;
using DocxGuard.Core.Packaging;
using DocxGuard.Core.Planning;
using DocxGuard.Core.Wordprocessing;

namespace DocxGuard.Core.Execution;

internal static class MainDocumentTextMutator
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private static readonly XNamespace XmlNamespace =
        "http://www.w3.org/XML/1998/namespace";

    public static MainDocumentMutationResult Apply(
        ReadOnlyMemory<byte> sourceXml,
        WordprocessingConformance conformance,
        WordDocumentIndex sourceIndex,
        PatchPlan plan,
        WordprocessingInspectionLimits wordprocessingLimits,
        TextPatchExecutionLimits executionLimits,
        CancellationToken cancellationToken)
    {
        string namespaceValue = conformance == WordprocessingConformance.Strict
            ? WordMainDocumentParser.StrictNamespace
            : WordMainDocumentParser.TransitionalNamespace;
        XNamespace word = namespaceValue;

        TextPatchOperation operation = plan.Operations[0];
        try
        {
            var editsByParagraph = PatchTextProjection.RunEditsByParagraph(plan);
            if (editsByParagraph.Values.Sum(runs => runs.Count) > executionLimits.MaxAffectedRuns)
            {
                return Failure(TextPatchExecutionFindingCodes.ResourceLimitExceeded,
                    $"The plan affects more than {executionLimits.MaxAffectedRuns} runs.", operation);
            }

            XDocument document = Load(sourceXml, wordprocessingLimits, cancellationToken);
            XElement[] xmlParagraphs = document.Root?.Element(word + "body")?
                .Descendants(word + "p").ToArray() ?? [];
            int generatedNodeCount = 0;
            foreach (var (paragraphId, runEdits) in editsByParagraph)
            {
                cancellationToken.ThrowIfCancellationRequested();
                operation = runEdits.Values.First()[0].Operation;
                if (!sourceIndex.TryGetParagraph(paragraphId, out IndexedParagraph? indexedParagraph)
                    || indexedParagraph!.Ordinal >= xmlParagraphs.Length)
                {
                    return Failure(TextPatchExecutionFindingCodes.XmlMutationFailed,
                        "The validated paragraph could not be resolved for mutation.", operation);
                }

                XElement[] xmlRuns = xmlParagraphs[indexedParagraph.Ordinal].Descendants(word + "r").ToArray();
                if (xmlRuns.Length != indexedParagraph.Runs.Count)
                {
                    return Failure(TextPatchExecutionFindingCodes.XmlMutationFailed,
                        "The XML run count differs from the validated index.", operation);
                }

                foreach (var (runOrdinal, edits) in runEdits)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    operation = edits[0].Operation;
                    string original = indexedParagraph.Runs[runOrdinal].Text;
                    XElement xmlRun = xmlRuns[runOrdinal];
                    if (!TryReadPlainRun(xmlRun, word, out string? runText) || original != runText)
                    {
                        return Failure(TextPatchExecutionFindingCodes.UnsupportedRunContent,
                            "An affected run is revised, non-textual, or outside the plain w:t execution profile.", operation);
                    }

                    List<XElement> replacementNodes = [];
                    int cursor = 0;
                    foreach (var edit in edits)
                    {
                        operation = edit.Operation;
                        AddPlainText(replacementNodes, word, original[cursor..edit.Start]);
                        AddReplacementText(replacementNodes, word, edit.Replacement);
                        cursor = edit.End;
                        if (replacementNodes.Count > executionLimits.MaxGeneratedTextNodes - generatedNodeCount)
                            break;
                    }

                    AddPlainText(replacementNodes, word, original[cursor..]);
                    if (replacementNodes.Count > executionLimits.MaxGeneratedTextNodes - generatedNodeCount)
                    {
                        return Failure(TextPatchExecutionFindingCodes.ResourceLimitExceeded,
                            $"The plan generates more than {executionLimits.MaxGeneratedTextNodes} text nodes.", operation);
                    }

                    generatedNodeCount += replacementNodes.Count;
                    ReplaceRunText(xmlRun, word, replacementNodes);
                }
            }

            byte[] patchedXml = Save(document);
            if (patchedXml.Length > executionLimits.MaxOutputMainDocumentXmlBytes
                || patchedXml.Length
                    > wordprocessingLimits.MaxMainDocumentXmlBytes)
            {
                return Failure(
                    TextPatchExecutionFindingCodes.ResourceLimitExceeded,
                    "The patched main document exceeds an output byte limit.",
                    operation);
            }

            return new MainDocumentMutationResult(patchedXml, null);
        }
        catch (Exception exception) when (
            exception is XmlException
                or InvalidOperationException
                or ArgumentException)
        {
            return Failure(
                TextPatchExecutionFindingCodes.XmlMutationFailed,
                "The verified XML snapshot could not be mutated safely.",
                operation);
        }
    }

    private static XDocument Load(
        ReadOnlyMemory<byte> sourceXml,
        WordprocessingInspectionLimits limits,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using MemoryStream stream = new(sourceXml.ToArray(), writable: false);
        using XmlReader reader = XmlReader.Create(
            stream,
            OpcXmlSecurity.CreateReaderSettings(limits.MaxXmlCharacters));
        return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
    }

    private static bool TryReadPlainRun(
        XElement run,
        XNamespace word,
        out string? text)
    {
        bool revised = run.Ancestors()
            .Any(ancestor => ancestor.Name.Namespace == word
                && ancestor.Name.LocalName is "ins"
                    or "del"
                    or "moveFrom"
                    or "moveTo");
        XElement[] children = run.Elements().ToArray();
        int propertiesCount = children.Count(
            child => child.Name == word + "rPr");
        bool elementShapeValid = propertiesCount <= 1
            && (propertiesCount == 0 || children[0].Name == word + "rPr")
            && children.All(child => child.Name == word + "rPr"
                || child.Name == word + "t");
        bool nonElementContentValid = run.Nodes()
            .Where(node => node is not XElement)
            .All(node => node is XText value
                && string.IsNullOrWhiteSpace(value.Value));
        XElement[] textElements = children
            .Where(child => child.Name == word + "t")
            .ToArray();
        bool textShapeValid = textElements.All(element =>
            !element.Elements().Any()
            && element.Attributes().All(attribute =>
                attribute.Name == XmlNamespace + "space")
            && element.Nodes().All(node => node is XText));

        if (revised
            || !elementShapeValid
            || !nonElementContentValid
            || !textShapeValid)
        {
            text = null;
            return false;
        }

        text = string.Concat(textElements.Select(element => element.Value));
        return true;
    }


    private static void ReplaceRunText(
        XElement run,
        XNamespace word,
        IReadOnlyList<XElement> replacementNodes)
    {
        run.Elements(word + "t").Remove();
        XElement? properties = run.Element(word + "rPr");
        if (properties is null)
        {
            run.AddFirst(replacementNodes);
        }
        else
        {
            properties.AddAfterSelf(replacementNodes);
        }
    }

    private static void AddReplacementText(
        List<XElement> nodes,
        XNamespace word,
        string value)
    {
        int segmentStart = 0;
        for (int index = 0; index < value.Length; index++)
        {
            string? tokenName = value[index] switch
            {
                '\t' => "tab",
                '\n' => "br",
                _ => null,
            };
            if (tokenName is null)
            {
                continue;
            }

            AddPlainText(nodes, word, value[segmentStart..index]);
            nodes.Add(new XElement(word + tokenName));
            segmentStart = index + 1;
        }

        AddPlainText(nodes, word, value[segmentStart..]);
    }

    private static void AddPlainText(
        List<XElement> nodes,
        XNamespace word,
        string value)
    {
        if (value.Length == 0)
        {
            return;
        }

        XElement text = new(word + "t", value);
        if (char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1]))
        {
            text.SetAttributeValue(XmlNamespace + "space", "preserve");
        }

        nodes.Add(text);
    }

    private static byte[] Save(XDocument document)
    {
        using MemoryStream output = new();
        XmlWriterSettings settings = new()
        {
            CheckCharacters = true,
            CloseOutput = false,
            Encoding = StrictUtf8,
            Indent = false,
            NewLineHandling = NewLineHandling.None,
            OmitXmlDeclaration = document.Declaration is null,
        };
        using (XmlWriter writer = XmlWriter.Create(output, settings))
        {
            document.Save(writer);
        }

        return output.ToArray();
    }

    private static MainDocumentMutationResult Failure(
        string code,
        string message,
        TextPatchOperation operation) =>
        new(
            null,
            new TextPatchExecutionFinding(
                code,
                FindingSeverity.Error,
                message,
                operation.Id,
                operation.Precondition.ParagraphId));

}

internal sealed record MainDocumentMutationResult(
    byte[]? Xml,
    TextPatchExecutionFinding? Finding);
