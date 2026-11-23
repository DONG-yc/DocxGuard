using System.Text;
using System.Xml;
using System.Xml.Linq;
using DocxGuard.Core.Execution;
using DocxGuard.Core.Indexing;
using DocxGuard.Core.Packaging;
using DocxGuard.Core.Planning;
using DocxGuard.Core.Wordprocessing;

namespace DocxGuard.Core.Verification;

/// <summary>
/// Independent allowlist check: validate expected text tokens, then compare
/// the entire XML with only those validated run text children excluded.
/// </summary>
internal static class MainDocumentStructuralComparer
{
    public static StructuralDiffFinding? Compare(
        DocxIndexInspectionResult source,
        DocxIndexInspectionResult output,
        InMemoryMainDocumentPatch patch,
        WordprocessingInspectionLimits limits,
        CancellationToken token)
    {
        string entryName = source.DocumentInspection.MainDocument!.Part.PackageEntryName;
        StructuralDiffFinding Failure(string code, string message, string? path = null) =>
            new(code, FindingSeverity.Error, message, entryName, path);

        if (new PatchPreconditionValidator().Validate(source.Index!, patch.Plan, token)
                .Status != PatchPreconditionValidationStatus.Passed)
        {
            return Failure(StructuralDiffFindingCodes.SourceMismatch,
                "All patch operations must be valid on the source index and have disjoint targets.");
        }

        try
        {
            XNamespace word = patch.Conformance == WordprocessingConformance.Strict
                ? WordMainDocumentParser.StrictNamespace
                : WordMainDocumentParser.TransitionalNamespace;
            XDocument before = Load(source.MainDocumentXml!.Value, limits, token);
            XDocument after = Load(output.MainDocumentXml!.Value, limits, token);
            // Encoding changes to UTF-8 are allowed; version/standalone and
            // declaration presence can affect interpretation and are preserved.
            if ((before.Declaration is null) != (after.Declaration is null)
                || before.Declaration?.Version != after.Declaration?.Version
                || before.Declaration?.Standalone != after.Declaration?.Standalone)
            {
                return Failure(StructuralDiffFindingCodes.UnauthorizedXmlChange,
                    "The XML declaration changed beyond output encoding.", "/");
            }

            var sourceIndex = source.Index!;
            XElement[] beforeParagraphs = before.Root!.Element(word + "body")!
                .Descendants(word + "p").ToArray();
            XElement[] afterParagraphs = after.Root!.Element(word + "body")!
                .Descendants(word + "p").ToArray();
            if (beforeParagraphs.Length != sourceIndex.Paragraphs.Count
                || afterParagraphs.Length != beforeParagraphs.Length)
            {
                return Failure(StructuralDiffFindingCodes.UnauthorizedXmlChange,
                    "Paragraph ownership or count changed.", "/document/body");
            }

            HashSet<XElement> allowedBefore = [];
            HashSet<XElement> allowedAfter = [];
            foreach (var (paragraphId, runEdits) in PatchTextProjection.RunEditsByParagraph(patch.Plan))
            {
                sourceIndex.TryGetParagraph(paragraphId, out IndexedParagraph? paragraph);
                int ordinal = paragraph!.Ordinal;
                XElement[] beforeRuns = beforeParagraphs[ordinal].Descendants(word + "r").ToArray();
                XElement[] afterRuns = afterParagraphs[ordinal].Descendants(word + "r").ToArray();
                if (beforeRuns.Length != paragraph.Runs.Count || afterRuns.Length != beforeRuns.Length)
                {
                    return Failure(StructuralDiffFindingCodes.UnauthorizedXmlChange,
                        "Run ownership or count changed.", Path(afterParagraphs[ordinal]));
                }

                foreach (var (runOrdinal, edits) in runEdits)
                {
                    token.ThrowIfCancellationRequested();
                    XElement original = beforeRuns[runOrdinal];
                    XElement candidate = afterRuns[runOrdinal];
                    if (!HasPlainShape(original, word, output: false)
                        || !HasPlainShape(candidate, word, output: true))
                    {
                        return Failure(StructuralDiffFindingCodes.UnauthorizedXmlChange,
                            "An affected run contains content outside the text allowlist.", Path(candidate));
                    }

                    string originalText = string.Concat(original.Elements(word + "t").Select(e => e.Value));
                    if (originalText != paragraph.Runs[runOrdinal].Text)
                    {
                        return Failure(StructuralDiffFindingCodes.SourceMismatch,
                            "The source XML run does not match the approved index.", Path(original));
                    }

                    List<ContentToken> expected = [];
                    int cursor = 0;
                    foreach (var edit in edits)
                    {
                        AppendText(expected, originalText[cursor..edit.Start]);
                        AppendReplacement(expected, edit.Replacement);
                        cursor = edit.End;
                    }

                    AppendText(expected, originalText[cursor..]);
                    if (!expected.SequenceEqual(ReadContent(candidate, word)))
                    {
                        return Failure(StructuralDiffFindingCodes.ExpectedTextMismatch,
                            "An affected run does not contain the planned text/tab/break sequence.", Path(candidate));
                    }

                    allowedBefore.Add(original);
                    allowedAfter.Add(candidate);
                }
            }

            using IEnumerator<XmlToken> left = Tokens(before, allowedBefore, word, token).GetEnumerator();
            using IEnumerator<XmlToken> right = Tokens(after, allowedAfter, word, token).GetEnumerator();
            while (true)
            {
                token.ThrowIfCancellationRequested();
                bool hasLeft = left.MoveNext();
                bool hasRight = right.MoveNext();
                if (!hasLeft && !hasRight)
                {
                    return null;
                }

                if (hasLeft != hasRight
                    || left.Current.Kind != right.Current.Kind
                    || left.Current.Name != right.Current.Name
                    || left.Current.Value != right.Current.Value)
                {
                    XObject location = hasRight ? right.Current.Location : left.Current.Location;
                    return Failure(StructuralDiffFindingCodes.UnauthorizedXmlChange,
                        "XML outside the approved run text changed (element, attribute, namespace, text, comment, or instruction).",
                        Path(location));
                }
            }
        }
        catch (XmlException exception)
        {
            return Failure(StructuralDiffFindingCodes.UnauthorizedXmlChange,
                $"The XML could not be compared safely: {exception.Message}");
        }
    }

    private static XDocument Load(ReadOnlyMemory<byte> bytes,
        WordprocessingInspectionLimits limits, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using MemoryStream stream = new(bytes.ToArray(), writable: false);
        using XmlReader reader = XmlReader.Create(stream,
            OpcXmlSecurity.CreateReaderSettings(limits.MaxXmlCharacters));
        return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
    }

    private static bool HasPlainShape(XElement run, XNamespace word, bool output)
    {
        if (run.Ancestors().Any(e => e.Name.Namespace == word
            && e.Name.LocalName is "ins" or "del" or "moveFrom" or "moveTo"))
        {
            return false;
        }

        XElement[] elements = run.Elements().ToArray();
        if (elements.Count(e => e.Name == word + "rPr") > 1
            || (elements.Any(e => e.Name == word + "rPr") && elements[0].Name != word + "rPr")
            || run.Nodes().Any(n => n is not XElement
                && (n is not XText t || !string.IsNullOrWhiteSpace(t.Value))))
        {
            return false;
        }

        foreach (XElement element in elements)
        {
            if (element.Name == word + "rPr")
            {
                continue;
            }

            if (output && (element.Name == word + "tab" || element.Name == word + "br"))
            {
                if (element.HasAttributes || element.Nodes().Any())
                {
                    return false;
                }

                continue;
            }

            if (element.Name != word + "t"
                || element.Nodes().Any(n => n is not XText)
                || element.Attributes().Any(a => a.Name != XNamespace.Xml + "space"))
            {
                return false;
            }

            if (output)
            {
                string value = element.Value;
                string? expectedSpace = value.Length > 0
                    && (char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1]))
                    ? "preserve" : null;
                if (value.Length == 0
                    || (string?)element.Attribute(XNamespace.Xml + "space") != expectedSpace)
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static List<ContentToken> ReadContent(XElement run, XNamespace word)
    {
        List<ContentToken> content = [];
        StringBuilder plain = new();
        foreach (XElement child in run.Elements())
        {
            if (child.Name == word + "t")
            {
                plain.Append(child.Value);
            }
            else if (child.Name == word + "tab" || child.Name == word + "br")
            {
                AppendText(content, plain.ToString());
                plain.Clear();
                content.Add(new ContentToken(child.Name.LocalName, string.Empty));
            }
        }

        AppendText(content, plain.ToString());
        return content;
    }

    private static void AppendReplacement(List<ContentToken> tokens, string value)
    {
        int start = 0;
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] is not ('\t' or '\n'))
            {
                continue;
            }

            AppendText(tokens, value[start..i]);
            tokens.Add(new ContentToken(value[i] == '\t' ? "tab" : "br", string.Empty));
            start = i + 1;
        }

        AppendText(tokens, value[start..]);
    }

    private static void AppendText(List<ContentToken> tokens, string text)
    {
        if (text.Length == 0)
        {
            return;
        }

        if (tokens.Count > 0 && tokens[^1].Kind == "text")
        {
            tokens[^1] = new ContentToken("text", tokens[^1].Value + text);
        }
        else
        {
            tokens.Add(new ContentToken("text", text));
        }
    }

    private static IEnumerable<XmlToken> Tokens(XDocument document,
        HashSet<XElement> allowedRuns, XNamespace word, CancellationToken token)
    {
        Stack<(IEnumerator<XNode> Nodes, XElement? Element)> stack = new();
        stack.Push((ComparableNodes(document, false, word).GetEnumerator(), null));
        try
        {
            while (stack.TryPeek(out var frame))
            {
                token.ThrowIfCancellationRequested();
                if (!frame.Nodes.MoveNext())
                {
                    frame.Nodes.Dispose();
                    stack.Pop();
                    if (frame.Element is not null)
                    {
                        yield return new XmlToken("end", frame.Element.Name.ToString(), "", frame.Element);
                    }

                    continue;
                }

                XNode node = frame.Nodes.Current;
                if (node is XElement element)
                {
                    yield return new XmlToken("start", element.Name.ToString(), "", element);
                    foreach (XAttribute attribute in element.Attributes()
                        .OrderBy(a => a.Name.NamespaceName, StringComparer.Ordinal)
                        .ThenBy(a => a.Name.LocalName, StringComparer.Ordinal))
                    {
                        yield return new XmlToken("attribute", attribute.Name.ToString(), attribute.Value, attribute);
                    }

                    stack.Push((ComparableNodes(element, allowedRuns.Contains(element), word).GetEnumerator(), element));
                }
                else if (node is XText text)
                {
                    yield return new XmlToken("text", "", text.Value, frame.Element ?? (XObject)document);
                }
                else if (node is XComment comment)
                {
                    yield return new XmlToken("comment", "", comment.Value, comment);
                }
                else if (node is XProcessingInstruction instruction)
                {
                    yield return new XmlToken("instruction", instruction.Target, instruction.Data, instruction);
                }
                else
                {
                    throw new XmlException("Unsupported XML node in structural comparison.");
                }
            }
        }
        finally
        {
            foreach (var frame in stack)
            {
                frame.Nodes.Dispose();
            }
        }
    }

    private static IEnumerable<XNode> ComparableNodes(XContainer parent, bool allowedRun, XNamespace word)
    {
        StringBuilder? text = null;
        foreach (XNode node in parent.Nodes())
        {
            if (allowedRun && node is XElement child
                && (child.Name == word + "t" || child.Name == word + "tab" || child.Name == word + "br"))
            {
                continue;
            }

            if (node is XText value)
            {
                (text ??= new StringBuilder()).Append(value.Value);
                continue;
            }

            if (text is not null)
            {
                yield return new XText(text.ToString());
                text = null;
            }

            yield return node;
        }

        if (text is not null)
        {
            yield return new XText(text.ToString());
        }
    }

    private static string Path(XObject node)
    {
        XElement? element = node as XElement ?? node.Parent;
        Stack<string> parts = new();
        while (element is not null)
        {
            int ordinal = element.ElementsBeforeSelf(element.Name).Count() + 1;
            parts.Push($"{element.Name}[{ordinal}]");
            element = element.Parent;
        }

        string path = "/" + string.Join("/", parts);
        return node is XAttribute attribute ? path + "/@" + attribute.Name : path;
    }

    private sealed record ContentToken(string Kind, string Value);

    private sealed record XmlToken(string Kind, string Name, string Value, XObject Location);
}
