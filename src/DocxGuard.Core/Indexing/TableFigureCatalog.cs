using System.Text;
using System.Xml.Linq;
using DocxGuard.Core.Wordprocessing;

namespace DocxGuard.Core.Indexing;

public sealed record TableFigureTarget(string Kind, int? Table, string? ImageRelationship,
    string Before, string After, IReadOnlyList<string>? Header);

public sealed record TableFigureCatalog(string SourceSha256, int Total, int Offset,
    bool HasMore, IReadOnlyList<TableFigureTarget> Items)
{
    public static TableFigureCatalog Read(DocxIndexInspectionResult inspection, int offset, int limit)
    {
        if (offset < 0 || limit is < 1 or > 100)
            throw new ArgumentException("Expected a nonnegative offset and a limit of 1–100.");
        if (inspection.MainDocumentXml is not { } xml ||
            inspection.DocumentInspection.MainDocument?.Conformance != WordprocessingConformance.Transitional)
            throw new InvalidDataException("Table and figure discovery requires a verified Transitional DOCX.");
        using var stream = new MemoryStream(xml.ToArray(), writable: false);
        XElement root = XElement.Load(stream);
        XNamespace w = root.Name.Namespace;
        XNamespace a = "http://schemas.openxmlformats.org/drawingml/2006/main";
        XNamespace r = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        var blocks = root.Element(w + "body")!.Elements().ToArray();
        int ordinal = 0, total = 0;
        List<TableFigureTarget> items = [];
        for (int index = 0; index < blocks.Length; index++)
        {
            var block = blocks[index];
            if (block.Name == w + "tbl")
            {
                ++ordinal;
                if (OnPage())
                {
                    var headers = block.Element(w + "tr")?.Elements(w + "tc").Take(32)
                        .Select(cell => Snippet(cell, w)).ToArray();
                    Add("table", ordinal, null, headers);
                }
            }
            foreach (var blip in block.Descendants(a + "blip"))
            {
                if (blip.Attribute(r + "embed") is { } id && OnPage())
                    Add("image", null, id.Value, null);
            }

            bool OnPage() => total++ >= offset && items.Count < limit;
            void Add(string kind, int? table, string? id, string[]? header) =>
                items.Add(new(kind, table, id, Snippet(index > 0 ? blocks[index - 1] : null, w),
                    Snippet(index + 1 < blocks.Length ? blocks[index + 1] : null, w), header));
        }
        return new(inspection.SourcePackageSha256!, total, offset, total - offset > items.Count, items);
    }

    private static string Snippet(XElement? element, XNamespace w)
    {
        if (element == null) return "";
        var preview = new StringBuilder();
        int count = 0;
        foreach (var node in element.Descendants(w + "t"))
            foreach (var rune in node.Value.EnumerateRunes())
            {
                if (++count > 160) return preview.ToString();
                preview.Append(rune);
            }
        return preview.ToString();
    }
}
