using System.Text;
using System.Xml.Linq;
using DocxGuard.Core.Execution;
using DocxGuard.Core.Indexing;
using DocxGuard.Core.Packaging;
using DocxGuard.Core.Planning;

namespace DocxGuard.Tests;

public sealed class InMemoryTextPatchExecutorTests
{
    private static readonly XNamespace XmlNamespace =
        "http://www.w3.org/XML/1998/namespace";

    private readonly InMemoryTextPatchExecutor executor = new();

    [Fact]
    public void Execute_SingleRunReplacement_PreservesRunProperties()
    {
        DocxIndexInspectionResult source = CreateSource("""
            <w:p><w:r>
              <w:rPr><w:b/></w:rPr>
              <w:t>before target after</w:t>
            </w:r></w:p>
            """);
        PatchPlan plan = CreatePlan(
            source,
            "target",
            "replacement");

        TextPatchExecutionResult result = executor.Execute(source, plan);

        Assert.Equal(TextPatchExecutionStatus.Passed, result.Status);
        Assert.Equal(
            PatchPreconditionValidationStatus.Passed,
            result.PreconditionValidation!.Status);
        InMemoryMainDocumentPatch artifact = Assert.IsType<InMemoryMainDocumentPatch>(
            result.Artifact);
        Assert.Equal(plan.Id, artifact.PlanId);
        Assert.Equal("/word/document.xml", artifact.MainDocumentPartName);
        Assert.NotEqual(artifact.OriginalXmlSha256, artifact.PatchedXmlSha256);

        XNamespace word = WordNamespace(artifact);
        XElement run = Assert.Single(ReadXml(artifact).Descendants(word + "r"));
        Assert.NotNull(run.Element(word + "rPr")?.Element(word + "b"));
        Assert.Equal(
            "before replacement after",
            string.Concat(run.Elements(word + "t").Select(text => text.Value)));
        Assert.Equal("before target after", source.Index!.PlainText);
    }

    [Fact]
    public void Execute_CrossRunReplacement_PreservesEachRunFormatting()
    {
        DocxIndexInspectionResult source = CreateSource("""
            <w:p>
              <w:r><w:rPr><w:b/></w:rPr><w:t>pre tar</w:t></w:r>
              <w:r><w:rPr><w:i/></w:rPr><w:t>get post</w:t></w:r>
            </w:p>
            """);
        PatchPlan plan = CreatePlan(source, "target", "replacement");

        InMemoryMainDocumentPatch artifact = executor.Execute(source, plan).Artifact!;

        XNamespace word = WordNamespace(artifact);
        XElement[] runs = ReadXml(artifact).Descendants(word + "r").ToArray();
        Assert.NotNull(runs[0].Element(word + "rPr")?.Element(word + "b"));
        Assert.NotNull(runs[1].Element(word + "rPr")?.Element(word + "i"));
        Assert.Equal(
            "pre replacement",
            string.Concat(
                runs[0].Elements(word + "t").Select(text => text.Value)));
        XElement suffix = Assert.Single(runs[1].Elements(word + "t"));
        Assert.Equal(" post", suffix.Value);
        Assert.Equal("preserve", suffix.Attribute(XmlNamespace + "space")?.Value);
    }

    [Fact]
    public void Execute_ReplacementTabsBreaksAndSpaces_UseWordTokens()
    {
        DocxIndexInspectionResult source = CreateSource(
            "<w:p><w:r><w:t>target</w:t></w:r></w:p>");
        PatchPlan plan = CreatePlan(
            source,
            "target",
            " new\tline\nnext ");

        InMemoryMainDocumentPatch artifact = executor.Execute(source, plan).Artifact!;

        XNamespace word = WordNamespace(artifact);
        XElement run = Assert.Single(ReadXml(artifact).Descendants(word + "r"));
        Assert.Equal(
            ["t", "tab", "t", "br", "t"],
            run.Elements().Select(element => element.Name.LocalName));
        XElement[] text = run.Elements(word + "t").ToArray();
        Assert.Equal([" new", "line", "next "],
            text.Select(element => element.Value));
        Assert.Equal("preserve", text[0].Attribute(XmlNamespace + "space")?.Value);
        Assert.Equal("preserve", text[2].Attribute(XmlNamespace + "space")?.Value);
    }

    [Fact]
    public void Execute_EmptyReplacement_ClearsTextButKeepsRuns()
    {
        DocxIndexInspectionResult source = CreateSource("""
            <w:p>
              <w:r><w:rPr><w:b/></w:rPr><w:t>tar</w:t></w:r>
              <w:r><w:rPr><w:i/></w:rPr><w:t>get</w:t></w:r>
            </w:p>
            """);
        PatchPlan plan = CreatePlan(source, "target", "");

        InMemoryMainDocumentPatch artifact = executor.Execute(source, plan).Artifact!;

        XNamespace word = WordNamespace(artifact);
        XElement[] runs = ReadXml(artifact).Descendants(word + "r").ToArray();
        Assert.Equal(2, runs.Length);
        Assert.All(runs, run => Assert.Empty(run.Elements(word + "t")));
        Assert.NotNull(runs[0].Element(word + "rPr")?.Element(word + "b"));
        Assert.NotNull(runs[1].Element(word + "rPr")?.Element(word + "i"));
    }

    [Fact]
    public void Execute_StrictMainDocument_PassesAndRetainsConformance()
    {
        DocxIndexInspectionResult source = CreateSource(
            "<w:p><w:r><w:t>target</w:t></w:r></w:p>",
            strict: true);
        PatchPlan plan = CreatePlan(source, "target", "replacement");

        TextPatchExecutionResult result = executor.Execute(source, plan);

        Assert.Equal(TextPatchExecutionStatus.Passed, result.Status);
        Assert.Equal(
            DocxGuard.Core.Wordprocessing.WordprocessingConformance.Strict,
            result.Artifact!.Conformance);
    }

    [Fact]
    public void Execute_TableCellText_PreservesTableOwnership()
    {
        DocxIndexInspectionResult source = CreateSource("""
            <w:tbl><w:tr><w:tc>
              <w:p><w:r><w:t>target</w:t></w:r></w:p>
            </w:tc></w:tr></w:tbl>
            """);
        PatchPlan plan = CreatePlan(source, "target", "replacement");

        TextPatchExecutionResult result = executor.Execute(source, plan);

        Assert.Equal(TextPatchExecutionStatus.Passed, result.Status);
        XNamespace word = WordNamespace(result.Artifact!);
        Assert.Equal(
            "replacement",
            Assert.Single(
                ReadXml(result.Artifact!).Descendants(word + "t")).Value);
        Assert.Equal(
            "t000001/r0001/c0001",
            source.Index!.Paragraphs[0].TableCellId);
    }

    [Fact]
    public void Execute_StalePlan_FailsBeforeMutation()
    {
        DocxIndexInspectionResult planned = CreateSource(
            "<w:p><w:r><w:t>target suffix</w:t></w:r></w:p>");
        DocxIndexInspectionResult current = CreateSource(
            "<w:p><w:r><w:t>target changed</w:t></w:r></w:p>");
        PatchPlan plan = CreatePlan(planned, "target", "replacement");

        TextPatchExecutionResult result = executor.Execute(current, plan);

        AssertFailure(
            result,
            TextPatchExecutionFindingCodes.PreconditionsFailed);
        Assert.Equal(
            PatchPreconditionValidationStatus.Failed,
            result.PreconditionValidation!.Status);
    }

    [Theory]
    [InlineData("<w:p><w:r><w:fldChar/><w:t>target</w:t></w:r></w:p>")]
    [InlineData("<w:p><w:ins><w:r><w:t>target</w:t></w:r></w:ins></w:p>")]
    public void Execute_ComplexOrRevisedRun_IsRejected(string bodyContent)
    {
        DocxIndexInspectionResult source = CreateSource(bodyContent);
        PatchPlan plan = CreatePlan(source, "target", "replacement");

        TextPatchExecutionResult result = executor.Execute(source, plan);

        AssertFailure(
            result,
            TextPatchExecutionFindingCodes.UnsupportedRunContent);
    }

    [Fact]
    public void Execute_AffectedRunLimit_IsEnforced()
    {
        DocxIndexInspectionResult source = CreateSource("""
            <w:p><w:r><w:t>tar</w:t></w:r><w:r><w:t>get</w:t></w:r></w:p>
            """);
        PatchPlan plan = CreatePlan(source, "target", "replacement");

        TextPatchExecutionResult result = executor.Execute(
            source,
            plan,
            TextPatchExecutionLimits.Default with { MaxAffectedRuns = 1 });

        AssertFailure(
            result,
            TextPatchExecutionFindingCodes.ResourceLimitExceeded);
    }

    [Fact]
    public void Execute_GeneratedNodeAndOutputByteLimits_AreEnforced()
    {
        DocxIndexInspectionResult source = CreateSource(
            "<w:p><w:r><w:t>target</w:t></w:r></w:p>");
        PatchPlan tokenPlan = CreatePlan(source, "target", "a\tb");
        PatchPlan bytePlan = CreatePlan(source, "target", "replacement");

        TextPatchExecutionResult nodes = executor.Execute(
            source,
            tokenPlan,
            TextPatchExecutionLimits.Default with
            {
                MaxGeneratedTextNodes = 1,
            });
        TextPatchExecutionResult bytes = executor.Execute(
            source,
            bytePlan,
            TextPatchExecutionLimits.Default with
            {
                MaxOutputMainDocumentXmlBytes = 1,
            });

        AssertFailure(nodes, TextPatchExecutionFindingCodes.ResourceLimitExceeded);
        AssertFailure(bytes, TextPatchExecutionFindingCodes.ResourceLimitExceeded);
    }

    [Fact]
    public void Execute_FailedSourceInspection_IsRejected()
    {
        using MemoryStream stream = IndexTestPackage.WordStream(
            "<w:r><w:t>outside paragraph</w:t></w:r>");
        DocxIndexInspectionResult failedSource =
            new DocxIndexInspector().Inspect(stream);
        DocxIndexInspectionResult valid = CreateSource(
            "<w:p><w:r><w:t>target</w:t></w:r></w:p>");
        PatchPlan plan = CreatePlan(valid, "target", "replacement");

        TextPatchExecutionResult result = executor.Execute(failedSource, plan);

        AssertFailure(
            result,
            TextPatchExecutionFindingCodes.SourceInspectionNotPassed);
        Assert.Null(result.PreconditionValidation);
    }

    [Fact]
    public void Execute_InvalidLimitsNullArgumentsAndCancellation_Throw()
    {
        DocxIndexInspectionResult source = CreateSource(
            "<w:p><w:r><w:t>target</w:t></w:r></w:p>");
        PatchPlan plan = CreatePlan(source, "target", "replacement");
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        Assert.Throws<ArgumentNullException>(() => executor.Execute(null!, plan));
        Assert.Throws<ArgumentNullException>(() => executor.Execute(source, null!));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => executor.Execute(
                source,
                plan,
                TextPatchExecutionLimits.Default with
                {
                    MaxGeneratedTextNodes = 0,
                }));
        Assert.Throws<OperationCanceledException>(
            () => executor.Execute(
                source,
                plan,
                cancellationToken: cancellation.Token));
    }

    private static DocxIndexInspectionResult CreateSource(
        string bodyContent,
        bool strict = false)
    {
        using MemoryStream stream = IndexTestPackage.WordStream(
            bodyContent,
            strict);
        DocxIndexInspectionResult result = new DocxIndexInspector().Inspect(stream);
        Assert.Equal(PackageInspectionStatus.Passed, result.Status);
        return result;
    }

    private static PatchPlan CreatePlan(
        DocxIndexInspectionResult source,
        string expected,
        string replacement) =>
        new TextPatchPlanner().CreatePlan(
            source.Index!,
            new TextReplacementRequest(expected, replacement)).Plan!;

    private static XDocument ReadXml(InMemoryMainDocumentPatch artifact) =>
        XDocument.Parse(
            Encoding.UTF8.GetString(artifact.Xml.Span),
            LoadOptions.PreserveWhitespace);

    private static XNamespace WordNamespace(
        InMemoryMainDocumentPatch artifact) =>
        artifact.Conformance
            == DocxGuard.Core.Wordprocessing.WordprocessingConformance.Strict
            ? IndexTestPackage.StrictWordNamespace
            : IndexTestPackage.TransitionalWordNamespace;

    private static void AssertFailure(
        TextPatchExecutionResult result,
        string expectedCode)
    {
        Assert.Equal(TextPatchExecutionStatus.Failed, result.Status);
        Assert.Null(result.Artifact);
        Assert.Contains(result.Findings, finding => finding.Code == expectedCode);
    }
}
