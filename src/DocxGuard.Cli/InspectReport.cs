using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;
using DocxGuard.Core.Indexing;
using DocxGuard.Core.Packaging;

namespace DocxGuard.Cli;

internal sealed record InspectCheck(string Stage, string Status);
internal sealed record InspectFinding(string Code, string Severity, string Message,
    string Stage, string? EntryName = null, string? PartName = null,
    int? LineNumber = null, int? LinePosition = null, string? RelationshipId = null);
internal sealed record InspectSummary(string MainDocumentPartName, string Conformance,
    int EntryCount, int PartCount, int RelationshipCount, int ParagraphCount,
    int RunCount, int TableCount, int CellCount);
internal sealed record ParagraphPreview(string Id, int Ordinal, string? TableCellId,
    int RunCount, int TextLength, string TextPreview, bool Truncated);
internal sealed record ParagraphPage(int Offset, int Limit, int Total, bool HasMore,
    IReadOnlyList<ParagraphPreview> Items);

internal sealed record InspectReport(string EngineVersion, string Status, int ExitCode,
    string? SourcePackageSha256, InspectSummary? Summary, ParagraphPage? Paragraphs,
    IReadOnlyList<InspectCheck> Checks, IReadOnlyList<InspectFinding> Findings)
{
    public string SchemaVersion => "1.0";
    public string Command => "inspect";
    public string Profile => "docxguard.inspect.v1";

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    internal static InspectReport Failure(string version, int exitCode, string code, string message) =>
        new(version, "failed", exitCode, null, null, null,
            Stages.Select(stage => new InspectCheck(stage, "notRun")).ToArray(),
            [new(code, "error", message, "cli")]);

    private static readonly string[] Stages = ["package", "contentTypes", "relationships", "word", "index"];

    internal static InspectReport Create(string version, DocxIndexInspectionResult result, int offset, int limit)
    {
        var word = result.DocumentInspection;
        var opc = word.OpcInspection;
        var package = opc.PackageInspection;
        List<InspectFinding> findings = [];
        foreach (var f in package.Findings)
            findings.Add(new(f.Code, Severity(f.Severity), f.Message, "package", f.EntryName));
        foreach (var f in opc.ContentTypeFindings)
            findings.Add(new(f.Code, Severity(f.Severity), f.Message, "contentTypes",
                f.EntryName, f.PartName, f.LineNumber, f.LinePosition));
        foreach (var f in opc.RelationshipFindings)
            findings.Add(new(f.Code, Severity(f.Severity), f.Message, "relationships",
                PartName: f.RelationshipsPartName, LineNumber: f.LineNumber,
                LinePosition: f.LinePosition, RelationshipId: f.RelationshipId));
        foreach (var f in word.WordprocessingFindings)
            findings.Add(new(f.Code, Severity(f.Severity), f.Message, "word",
                PartName: f.MainDocumentPartName, LineNumber: f.LineNumber,
                LinePosition: f.LinePosition, RelationshipId: f.RelationshipId));
        foreach (var f in result.IndexFindings)
            findings.Add(new(f.Code, Severity(f.Severity), f.Message, "index",
                PartName: f.MainDocumentPartName, LineNumber: f.LineNumber, LinePosition: f.LinePosition));

        bool packagePassed = package.Status == PackageInspectionStatus.Passed;
        bool contentTypesPassed = packagePassed && opc.ContentTypes is not null;
        bool relationshipsPassed = contentTypesPassed && opc.Status == PackageInspectionStatus.Passed;
        bool wordPassed = relationshipsPassed && word.Status == PackageInspectionStatus.Passed;
        bool passed = result.Status == PackageInspectionStatus.Passed;
        InspectCheck[] checks =
        [
            new("package", Check(true, packagePassed)),
            new("contentTypes", Check(packagePassed, contentTypesPassed)),
            new("relationships", Check(contentTypesPassed, relationshipsPassed)),
            new("word", Check(relationshipsPassed, wordPassed)),
            new("index", Check(wordPassed, passed)),
        ];
        InspectSummary? summary = null;
        ParagraphPage? page = null;
        if (passed)
        {
            var index = result.Index!;
            summary = new(word.MainDocument!.Part.PartName.Value, word.MainDocument.Conformance.ToString(),
                package.Entries.Count, opc.Parts.Count,
                opc.Relationships!.RelationshipSets.Sum(set => set.Relationships.Count),
                index.Paragraphs.Count, index.Paragraphs.Sum(p => p.Runs.Count),
                index.Tables.Count, index.Tables.Sum(t => t.Cells.Count));
            var items = index.Paragraphs.Skip(offset).Take(limit).Select(p =>
            {
                int length = Math.Min(200, p.Text.Length);
                if (length < p.Text.Length && length > 0 && char.IsHighSurrogate(p.Text[length - 1]))
                    length--;
                return new ParagraphPreview(p.Id, p.Ordinal, p.TableCellId, p.Runs.Count,
                    p.Text.Length, p.Text[..length], length < p.Text.Length);
            }).ToArray();
            page = new(offset, limit, index.Paragraphs.Count,
                (long)offset + items.Length < index.Paragraphs.Count, items);
        }

        return new(version, passed ? "passed" : "failed", passed ? 0 : 4,
            result.SourcePackageSha256, summary, page, checks, findings);
    }

    private static string Check(bool ran, bool passed) => !ran ? "notRun" : passed ? "passed" : "failed";
    private static string Severity(FindingSeverity severity) => severity == FindingSeverity.Error ? "error" : "warning";

    internal void Write(TextWriter output, TextWriter error, bool json)
    {
        if (json)
        {
            output.WriteLine(JsonSerializer.Serialize(this, JsonOptions));
            return;
        }

        TextWriter writer = ExitCode == 0 ? output : error;
        writer.WriteLine($"Inspection: {Status}");
        if (Summary is { } summary)
        {
            writer.WriteLine($"Source SHA-256: {SourcePackageSha256}");
            writer.WriteLine($"Main Part: {Quote(summary.MainDocumentPartName)} ({summary.Conformance})");
            writer.WriteLine($"Entries: {summary.EntryCount}; Parts: {summary.PartCount}; Relationships: {summary.RelationshipCount}");
            writer.WriteLine($"Paragraphs: {summary.ParagraphCount}; Runs: {summary.RunCount}; Tables: {summary.TableCount}; Cells: {summary.CellCount}");
            writer.WriteLine($"Paragraph previews (offset {Paragraphs!.Offset}, limit {Paragraphs.Limit}):");
            foreach (var p in Paragraphs.Items)
                writer.WriteLine($"  {Quote(p.Id)} cell={Quote(p.TableCellId)} runs={p.RunCount} length={p.TextLength}: {Quote(p.TextPreview)}{(p.Truncated ? " [truncated]" : "")}");
            if (Paragraphs.HasMore)
                writer.WriteLine($"More paragraphs: use --offset {Paragraphs.Offset + Paragraphs.Items.Count} --limit {Paragraphs.Limit}.");
        }

        foreach (var finding in Findings)
            writer.WriteLine($"{finding.Code} [{finding.Stage}/{finding.Severity}]: {Quote(finding.Message)} entry={Quote(finding.EntryName)} part={Quote(finding.PartName)} line={finding.LineNumber}:{finding.LinePosition}");
    }

    private static string Quote(string? value) => JsonSerializer.Serialize(value, JsonOptions);
}
