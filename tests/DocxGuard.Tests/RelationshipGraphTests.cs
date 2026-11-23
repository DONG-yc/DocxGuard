using System.IO.Compression;
using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using DocxGuard.Core.Packaging;

namespace DocxGuard.Tests;

public sealed class RelationshipGraphTests
{
    private const string ContentTypesNamespace =
        "http://schemas.openxmlformats.org/package/2006/content-types";
    private const string RelationshipsNamespace =
        "http://schemas.openxmlformats.org/package/2006/relationships";
    private const string OfficeDocumentType =
        "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument";
    private const string TestType = "urn:docxguard:test";
    private const string StandardContentTypes = $"""
        <Types xmlns="{ContentTypesNamespace}">
          <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml" />
          <Default Extension="xml" ContentType="application/xml" />
        </Types>
        """;

    private readonly OpcPackageInspector inspector = new();

    [Fact]
    public void Inspect_OpenXmlSdkDocument_ProducesPackageRelationshipGraph()
    {
        using MemoryStream stream = new();
        using (WordprocessingDocument document = WordprocessingDocument.Create(
            stream,
            DocumentFormat.OpenXml.WordprocessingDocumentType.Document,
            autoSave: true))
        {
            MainDocumentPart mainPart = document.AddMainDocumentPart();
            mainPart.Document = new Document(new Body(new Paragraph(new Run(new Text("hello")))));
        }

        stream.Position = 0;
        OpcInspectionResult result = inspector.Inspect(stream);

        Assert.Equal(PackageInspectionStatus.Passed, result.Status);
        RelationshipGraph graph = Assert.IsType<RelationshipGraph>(result.Relationships);
        OpcRelationshipSet packageRelationships =
            Assert.IsType<OpcRelationshipSet>(graph.PackageRelationships);
        OpcRelationship relationship = Assert.Single(packageRelationships.Relationships);
        Assert.Equal(OfficeDocumentType, relationship.Type);
        Assert.Equal("/word/document.xml", relationship.TargetPartName.Value);
        Assert.Equal(RelationshipTargetMode.Internal, relationship.TargetMode);
    }

    [Fact]
    public void Inspect_EmptyPackageRelationshipSet_Passes()
    {
        using MemoryStream stream = PackageStream(EmptyRelationships());

        OpcInspectionResult result = inspector.Inspect(stream);

        Assert.Equal(PackageInspectionStatus.Passed, result.Status);
        Assert.Empty(result.RelationshipFindings);
        Assert.Empty(Assert.IsType<RelationshipGraph>(result.Relationships)
            .PackageRelationships!.Relationships);
    }

    [Fact]
    public void Inspect_InternalTargetsResolveAgainstTheirSource()
    {
        string rootRelationships = Relationships(
            Relationship("rId1", "../word/document.xml"),
            Relationship("rId2", "/word/./document.xml"));
        string documentRelationships = Relationships(
            Relationship("media", "../media/item.xml"));
        using MemoryStream stream = PackageStream(
            rootRelationships,
            new EntrySpec("word/document.xml", "<document/>"),
            new EntrySpec("media/item.xml", "<item/>"),
            new EntrySpec("word/_rels/document.xml.rels", documentRelationships));

        OpcInspectionResult result = inspector.Inspect(stream);

        Assert.Equal(PackageInspectionStatus.Passed, result.Status);
        RelationshipGraph graph = Assert.IsType<RelationshipGraph>(result.Relationships);
        Assert.All(
            graph.PackageRelationships!.Relationships,
            relationship => Assert.Equal(
                "/word/document.xml",
                relationship.TargetPartName.Value));
        Assert.True(graph.TryGetRelationships(
            OpcPartName.Parse("/word/document.xml"),
            out OpcRelationshipSet? documentSet));
        Assert.Equal(
            "/media/item.xml",
            Assert.Single(documentSet!.Relationships).TargetPartName.Value);
    }

    [Fact]
    public void Inspect_MultipleIdsMayShareOneTarget()
    {
        using MemoryStream stream = PackageStream(
            Relationships(
                Relationship("first", "word/document.xml"),
                Relationship("second", "word/document.xml")),
            new EntrySpec("word/document.xml", "<document/>"));

        OpcInspectionResult result = inspector.Inspect(stream);

        Assert.Equal(PackageInspectionStatus.Passed, result.Status);
        Assert.Equal(
            2,
            result.Relationships!.PackageRelationships!.Relationships.Count);
    }

    [Theory]
    [InlineData("https://example.com", "External")]
    [InlineData("../outside", "External")]
    public void Inspect_ExternalRelationship_Fails(
        string target,
        string targetMode)
    {
        using MemoryStream stream = PackageStream(
            Relationships(Relationship("external", target, targetMode)));

        OpcInspectionResult result = inspector.Inspect(stream);

        AssertRelationshipFailure(
            result,
            RelationshipFindingCodes.ExternalRelationshipProhibited);
    }

    [Theory]
    [InlineData("")]
    [InlineData("http://example.com/document.xml")]
    [InlineData("//host/document.xml")]
    [InlineData("word/document.xml?version=1")]
    [InlineData("word/document.xml#bookmark")]
    [InlineData("word\\document.xml")]
    [InlineData("word/%2E%2E/document.xml")]
    [InlineData("word/")]
    public void Inspect_InvalidInternalTarget_Fails(string target)
    {
        using MemoryStream stream = PackageStream(
            Relationships(Relationship("bad", target)));

        OpcInspectionResult result = inspector.Inspect(stream);

        AssertRelationshipFailure(
            result,
            RelationshipFindingCodes.InvalidInternalTarget);
    }

    [Fact]
    public void Inspect_MissingInternalTarget_FailsWithoutPublishingPartialGraph()
    {
        using MemoryStream stream = PackageStream(
            Relationships(Relationship("missing", "word/missing.xml")),
            new EntrySpec("word/document.xml", "<document/>"));

        OpcInspectionResult result = inspector.Inspect(stream);

        AssertRelationshipFailure(result, RelationshipFindingCodes.TargetPartMissing);
        Assert.NotNull(result.ContentTypes);
        Assert.NotEmpty(result.Parts);
        Assert.Null(result.Relationships);
    }

    [Fact]
    public void Inspect_DuplicateRelationshipId_Fails()
    {
        using MemoryStream stream = PackageStream(
            Relationships(
                Relationship("same", "one.xml"),
                Relationship("same", "two.xml")),
            new EntrySpec("one.xml", "<one/>"),
            new EntrySpec("two.xml", "<two/>"));

        OpcInspectionResult result = inspector.Inspect(stream);

        AssertRelationshipFailure(result, RelationshipFindingCodes.DuplicateId);
    }

    [Theory]
    [InlineData("1startsWithDigit", TestType, null, "REL_005")]
    [InlineData("valid", "relative/type", null, "REL_007")]
    [InlineData("valid", TestType, "external", "REL_008")]
    public void Inspect_InvalidRelationshipAttributes_Fail(
        string id,
        string type,
        string? targetMode,
        string expectedCode)
    {
        using MemoryStream stream = PackageStream(
            Relationships(Relationship(id, "target.xml", targetMode, type)),
            new EntrySpec("target.xml", "<target/>"));

        OpcInspectionResult result = inspector.Inspect(stream);

        AssertRelationshipFailure(result, expectedCode);
    }

    [Theory]
    [InlineData("<Relationships xmlns=\"urn:wrong\"/>")]
    [InlineData("<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\" xml:base=\"/\"></Relationships>")]
    [InlineData("<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Unexpected/></Relationships>")]
    public void Inspect_RelationshipSchemaViolation_Fails(string relationshipsXml)
    {
        using MemoryStream stream = PackageStream(relationshipsXml);

        OpcInspectionResult result = inspector.Inspect(stream);

        AssertRelationshipFailure(result, RelationshipFindingCodes.SchemaViolation);
    }

    [Fact]
    public void Inspect_DtdAndExternalEntity_AreRejected()
    {
        string relationshipsXml = $"""
            <!DOCTYPE Relationships [
              <!ENTITY xxe SYSTEM "file:///not-allowed">
            ]>
            <Relationships xmlns="{RelationshipsNamespace}">&xxe;</Relationships>
            """;
        using MemoryStream stream = PackageStream(relationshipsXml);

        OpcInspectionResult result = inspector.Inspect(stream);

        AssertRelationshipFailure(
            result,
            RelationshipFindingCodes.XmlReaderRejected);
    }

    [Fact]
    public void Inspect_OrphanRelationshipsPart_Fails()
    {
        using MemoryStream stream = PackageStream(
            EmptyRelationships(),
            new EntrySpec(
                "ghost/_rels/missing.xml.rels",
                EmptyRelationships()));

        OpcInspectionResult result = inspector.Inspect(stream);

        AssertRelationshipFailure(result, RelationshipFindingCodes.SourcePartMissing);
    }

    [Fact]
    public void Inspect_RelationshipCannotTargetRelationshipsPart()
    {
        using MemoryStream stream = PackageStream(
            Relationships(Relationship("loop", "_rels/.rels")));

        OpcInspectionResult result = inspector.Inspect(stream);

        AssertRelationshipFailure(
            result,
            RelationshipFindingCodes.RelationshipPartReferenceProhibited);
    }

    [Fact]
    public void Inspect_RelationshipPartByteLimit_IsEnforcedBeforeParsing()
    {
        using MemoryStream stream = PackageStream(EmptyRelationships());
        OpcInspectionLimits limits = OpcInspectionLimits.Default with
        {
            MaxRelationshipPartXmlBytes = 16,
        };

        OpcInspectionResult result = inspector.Inspect(
            stream,
            opcLimits: limits);

        AssertRelationshipFailure(
            result,
            RelationshipFindingCodes.ResourceLimitExceeded);
    }

    [Fact]
    public void Inspect_RelationshipCountPerPartLimit_IsEnforced()
    {
        using MemoryStream stream = PackageStream(
            Relationships(
                Relationship("one", "one.xml"),
                Relationship("two", "two.xml")),
            new EntrySpec("one.xml", "<one/>"),
            new EntrySpec("two.xml", "<two/>"));
        OpcInspectionLimits limits = OpcInspectionLimits.Default with
        {
            MaxRelationshipsPerPart = 1,
        };

        OpcInspectionResult result = inspector.Inspect(
            stream,
            opcLimits: limits);

        AssertRelationshipFailure(
            result,
            RelationshipFindingCodes.ResourceLimitExceeded);
    }

    [Fact]
    public void Inspect_TotalRelationshipCountLimit_IsEnforcedAcrossParts()
    {
        using MemoryStream stream = PackageStream(
            Relationships(Relationship("root", "word/document.xml")),
            new EntrySpec("word/document.xml", "<document/>"),
            new EntrySpec(
                "word/_rels/document.xml.rels",
                Relationships(Relationship("child", "../item.xml"))),
            new EntrySpec("item.xml", "<item/>"));
        OpcInspectionLimits limits = OpcInspectionLimits.Default with
        {
            MaxTotalRelationships = 1,
        };

        OpcInspectionResult result = inspector.Inspect(
            stream,
            opcLimits: limits);

        AssertRelationshipFailure(
            result,
            RelationshipFindingCodes.ResourceLimitExceeded);
    }

    [Fact]
    public void Inspect_ExpandedEmptyRelationshipElement_IsAccepted()
    {
        string relationshipsXml = $"""
            <Relationships xmlns="{RelationshipsNamespace}">
              <Relationship Id="rId1" Type="{TestType}" Target="target.xml"></Relationship>
            </Relationships>
            """;
        using MemoryStream stream = PackageStream(
            relationshipsXml,
            new EntrySpec("target.xml", "<target/>"));

        OpcInspectionResult result = inspector.Inspect(stream);

        Assert.Equal(PackageInspectionStatus.Passed, result.Status);
        Assert.Single(
            result.Relationships!.PackageRelationships!.Relationships);
    }

    [Fact]
    public void Inspect_MalformedRelationshipsXml_Fails()
    {
        using MemoryStream stream = PackageStream(
            $"<Relationships xmlns=\"{RelationshipsNamespace}\">");

        OpcInspectionResult result = inspector.Inspect(stream);

        AssertRelationshipFailure(
            result,
            RelationshipFindingCodes.XmlReaderRejected);
    }

    private static string EmptyRelationships() =>
        $"<Relationships xmlns=\"{RelationshipsNamespace}\"/>";

    private static string Relationships(params string[] relationships) => $"""
        <Relationships xmlns="{RelationshipsNamespace}">
          {string.Join(Environment.NewLine, relationships)}
        </Relationships>
        """;

    private static string Relationship(
        string id,
        string target,
        string? targetMode = null,
        string type = TestType)
    {
        string targetModeAttribute = targetMode is null
            ? string.Empty
            : $" TargetMode=\"{targetMode}\"";
        return $"<Relationship Id=\"{id}\" Type=\"{type}\" Target=\"{target}\"{targetModeAttribute}/>";
    }

    private static MemoryStream PackageStream(
        string rootRelationships,
        params EntrySpec[] entries)
    {
        MemoryStream stream = new();
        using (ZipArchive archive = new(
            stream,
            ZipArchiveMode.Create,
            leaveOpen: true))
        {
            WriteEntry(archive, "[Content_Types].xml", StandardContentTypes);
            WriteEntry(archive, "_rels/.rels", rootRelationships);
            foreach (EntrySpec entry in entries)
            {
                WriteEntry(archive, entry.Name, entry.Content);
            }
        }

        stream.Position = 0;
        return stream;
    }

    private static void WriteEntry(
        ZipArchive archive,
        string name,
        string content)
    {
        ZipArchiveEntry entry = archive.CreateEntry(
            name,
            CompressionLevel.NoCompression);
        using Stream entryStream = entry.Open();
        byte[] bytes = Encoding.UTF8.GetBytes(content);
        entryStream.Write(bytes);
    }

    private static void AssertRelationshipFailure(
        OpcInspectionResult result,
        string expectedCode)
    {
        Assert.Equal(PackageInspectionStatus.Failed, result.Status);
        Assert.Contains(
            result.RelationshipFindings,
            finding => finding.Code == expectedCode
                && finding.Severity == FindingSeverity.Error);
        Assert.Null(result.Relationships);
    }

    private sealed record EntrySpec(string Name, string Content);
}
