namespace DocxGuard.Core.Building;

public enum DocxPublicationStatus { Published, Failed, Cancelled }

public sealed record DocxPublicationFinding(string Code, string Message, string? Path = null);

public sealed class DocxPublicationResult
{
    internal DocxPublicationResult(DocxPublicationStatus status, string? directory,
        string? reportSha256, IEnumerable<DocxPublicationFinding> findings, IEnumerable<string> residualPaths)
    {
        Status = status;
        Directory = directory;
        ReportSha256 = reportSha256;
        Findings = Array.AsReadOnly(findings.ToArray());
        ResidualPaths = Array.AsReadOnly(residualPaths.Distinct(StringComparer.Ordinal).ToArray());
    }

    public DocxPublicationStatus Status { get; }
    public string? Directory { get; }
    public string? DocxPath => Directory is null ? null : System.IO.Path.Combine(Directory, "output.docx");
    public string? ReportPath => Directory is null ? null : System.IO.Path.Combine(Directory, "build-report.json");
    public string? ReportSha256 { get; }
    public IReadOnlyList<DocxPublicationFinding> Findings { get; }
    public IReadOnlyList<string> ResidualPaths { get; }
}
