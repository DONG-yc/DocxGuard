using System.IO.Compression;
using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using DocxGuard.Core.Packaging;
using DocxGuard.Core.Wordprocessing;

namespace DocxGuard.Tests;

public sealed class DocxDocumentInspectorTests
{
    private const string ContentTypesNamespace =
        "http://schemas.openxmlformats.org/package/2006/content-types";
    private const string RelationshipsNamespace =
        "http://schemas.openxmlformats.org/package/2006/relationships";
    private const string TransitionalWordNamespace =
        "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private const string StrictWordNamespace =
        "http://purl.oclc.org/ooxml/wordprocessingml/main";
    private const string RelationshipsContentType =
        "application/vnd.openxmlformats-package.relationships+xml";

    private readonly DocxDocumentInspector inspector = new();

    [Fact]
    public void Inspect_OpenXmlSdkDocument_PublishesMainDocument()
    {
        using MemoryStream stream = new();
        using (WordprocessingDocument document = WordprocessingDocument.Create(
            stream,
            DocumentFormat.OpenXml.WordprocessingDocumentType.Document,
            autoSave: true))
        {
            MainDocumentPart mainPart = document.AddMainDocumentPart();
            mainPart.Document = new Document(
                new Body(
                    new Paragraph(
                        new Run(
                            new Text("hello")))));
        }

        stream.Position = 0;
        DocxInspectionResult result = inspector.Inspect(stream);

        Assert.Equal(PackageInspectionStatus.Passed, result.Status);
        WordMainDocumentInfo main = Assert.IsType<WordMainDocumentInfo>(
            result.MainDocument);
        Assert.Equal("/word/document.xml", main.Part.PartName.Value);
        Assert.Equal(
            WordprocessingConformance.Transitional,
            main.Conformance);
        Assert.Equal(1, main.BodyChildElementCount);
        Assert.Empty(result.WordprocessingFindings);
    }

    [Fact]
    public void Inspect_MainPartLocationComesFromRelationship_NotHardCodedPath()
    {
        using MemoryStream stream = WordPackageStream(
            TransitionalDocument("<w:p/>"),
            mainEntryName: "custom/main.xml");

        DocxInspectionResult result = inspector.Inspect(stream);

        Assert.Equal(PackageInspectionStatus.Passed, result.Status);
        Assert.Equal(
            "/custom/main.xml",
            result.MainDocument!.Part.PartName.Value);
    }

    [Fact]
    public void Inspect_StrictPackageAndNamespace_PassTogether()
    {
        using MemoryStream stream = WordPackageStream(
            StrictDocument("<w:p/><w:sectPr/>"),
            relationshipType:
                DocxDocumentInspector.StrictOfficeDocumentRelationshipType);

        DocxInspectionResult result = inspector.Inspect(stream);

        Assert.Equal(PackageInspectionStatus.Passed, result.Status);
        Assert.Equal(
            WordprocessingConformance.Strict,
            result.MainDocument!.Conformance);
        Assert.True(result.MainDocument.HasSectionProperties);
        Assert.Equal(2, result.MainDocument.BodyChildElementCount);
    }

    [Fact]
    public void Inspect_MissingOfficeDocumentRelationship_Fails()
    {
        using MemoryStream stream = PackageStream(
            ContentTypes(("word/document.xml", DocxDocumentInspector.MainDocumentContentType)),
            EmptyRelationships(),
            new EntrySpec("word/document.xml", TransitionalDocument()));

        DocxInspectionResult result = inspector.Inspect(stream);

        AssertWordFailure(
            result,
            WordprocessingFindingCodes.MainDocumentRelationshipMissing);
    }

    [Fact]
    public void Inspect_MultipleOfficeDocumentRelationships_Fail()
    {
        string relationships = Relationships(
            Relationship(
                "first",
                "word/document.xml",
                DocxDocumentInspector.TransitionalOfficeDocumentRelationshipType),
            Relationship(
                "second",
                "word/document.xml",
                DocxDocumentInspector.TransitionalOfficeDocumentRelationshipType));
        using MemoryStream stream = PackageStream(
            ContentTypes(("word/document.xml", DocxDocumentInspector.MainDocumentContentType)),
            relationships,
            new EntrySpec("word/document.xml", TransitionalDocument()));

        DocxInspectionResult result = inspector.Inspect(stream);

        AssertWordFailure(
            result,
            WordprocessingFindingCodes.MultipleMainDocumentRelationships);
    }

    [Theory]
    [InlineData("application/xml")]
    [InlineData("application/vnd.ms-word.document.macroEnabled.main+xml")]
    [InlineData("application/vnd.openxmlformats-officedocument.wordprocessingml.template.main+xml")]
    public void Inspect_UnsupportedMainContentType_Fails(string contentType)
    {
        using MemoryStream stream = WordPackageStream(
            TransitionalDocument(),
            mainContentType: contentType);

        DocxInspectionResult result = inspector.Inspect(stream);

        AssertWordFailure(
            result,
            WordprocessingFindingCodes.UnsupportedMainDocumentContentType);
    }

    [Fact]
    public void Inspect_RelationshipAndNamespaceConformanceMustMatch()
    {
        using MemoryStream stream = WordPackageStream(
            StrictDocument(),
            relationshipType:
                DocxDocumentInspector.TransitionalOfficeDocumentRelationshipType);

        DocxInspectionResult result = inspector.Inspect(stream);

        AssertWordFailure(
            result,
            WordprocessingFindingCodes.ConformanceMismatch);
    }

    [Theory]
    [InlineData("<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"/>")]
    [InlineData("<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body/><w:body/></w:document>")]
    [InlineData("<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:p/><w:body/></w:document>")]
    [InlineData("<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body><w:sectPr/><w:p/></w:body></w:document>")]
    [InlineData("<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body><foreign xmlns=\"urn:foreign\"/></w:body></w:document>")]
    public void Inspect_InvalidMinimumDocumentStructure_Fails(string documentXml)
    {
        using MemoryStream stream = WordPackageStream(documentXml);

        DocxInspectionResult result = inspector.Inspect(stream);

        AssertWordFailure(result, WordprocessingFindingCodes.SchemaViolation);
    }

    [Fact]
    public void Inspect_DtdAndExternalEntityInMainDocument_AreRejected()
    {
        string documentXml = $"""
            <!DOCTYPE w:document [
              <!ENTITY xxe SYSTEM "file:///not-allowed">
            ]>
            <w:document xmlns:w="{TransitionalWordNamespace}">
              <w:body>&xxe;</w:body>
            </w:document>
            """;
        using MemoryStream stream = WordPackageStream(documentXml);

        DocxInspectionResult result = inspector.Inspect(stream);

        AssertWordFailure(result, WordprocessingFindingCodes.XmlReaderRejected);
    }

    [Fact]
    public void Inspect_MalformedMainDocumentXml_Fails()
    {
        using MemoryStream stream = WordPackageStream(
            $"<w:document xmlns:w=\"{TransitionalWordNamespace}\"><w:body>");

        DocxInspectionResult result = inspector.Inspect(stream);

        AssertWordFailure(result, WordprocessingFindingCodes.XmlReaderRejected);
    }

    [Fact]
    public void Inspect_UnsupportedDeclaredEncoding_Fails()
    {
        string documentXml = $"""
            <?xml version="1.0" encoding="iso-8859-1"?>
            <w:document xmlns:w="{TransitionalWordNamespace}"><w:body/></w:document>
            """;
        using MemoryStream stream = WordPackageStream(documentXml);

        DocxInspectionResult result = inspector.Inspect(stream);

        AssertWordFailure(
            result,
            WordprocessingFindingCodes.UnsupportedXmlEncoding);
    }

    [Fact]
    public void Inspect_OptionalBackgroundBeforeBody_Passes()
    {
        string documentXml = $"""
            <w:document xmlns:w="{TransitionalWordNamespace}">
              <w:background w:color="FFFFFF"/>
              <w:body/>
            </w:document>
            """;
        using MemoryStream stream = WordPackageStream(documentXml);

        DocxInspectionResult result = inspector.Inspect(stream);

        Assert.Equal(PackageInspectionStatus.Passed, result.Status);
        Assert.Equal(0, result.MainDocument!.BodyChildElementCount);
    }

    [Fact]
    public void Inspect_MainDocumentByteLimit_IsEnforcedBeforeXmlParsing()
    {
        using MemoryStream stream = WordPackageStream(TransitionalDocument());
        WordprocessingInspectionLimits limits =
            WordprocessingInspectionLimits.Default with
            {
                MaxMainDocumentXmlBytes = 16,
            };

        DocxInspectionResult result = inspector.Inspect(
            stream,
            wordprocessingLimits: limits);

        AssertWordFailure(
            result,
            WordprocessingFindingCodes.ResourceLimitExceeded);
    }

    [Fact]
    public void Inspect_DepthAndNodeLimits_AreEnforced()
    {
        using MemoryStream depthStream = WordPackageStream(
            TransitionalDocument("<w:p><w:r><w:t>x</w:t></w:r></w:p>"));
        DocxInspectionResult depthResult = inspector.Inspect(
            depthStream,
            wordprocessingLimits: WordprocessingInspectionLimits.Default with
            {
                MaxXmlDepth = 2,
            });

        AssertWordFailure(
            depthResult,
            WordprocessingFindingCodes.ResourceLimitExceeded);

        using MemoryStream nodeStream = WordPackageStream(
            TransitionalDocument("<w:p/>"));
        DocxInspectionResult nodeResult = inspector.Inspect(
            nodeStream,
            wordprocessingLimits: WordprocessingInspectionLimits.Default with
            {
                MaxXmlNodes = 2,
            });

        AssertWordFailure(
            nodeResult,
            WordprocessingFindingCodes.ResourceLimitExceeded);
    }

    [Fact]
    public void Inspect_BodyChildAndAttributeLimits_AreEnforced()
    {
        using MemoryStream bodyStream = WordPackageStream(
            TransitionalDocument("<w:p/><w:p/>"));
        DocxInspectionResult bodyResult = inspector.Inspect(
            bodyStream,
            wordprocessingLimits: WordprocessingInspectionLimits.Default with
            {
                MaxBodyChildElements = 1,
            });

        AssertWordFailure(
            bodyResult,
            WordprocessingFindingCodes.ResourceLimitExceeded);

        using MemoryStream attributeStream = WordPackageStream(
            TransitionalDocument("<w:p a=\"1\" b=\"2\"/>"));
        DocxInspectionResult attributeResult = inspector.Inspect(
            attributeStream,
            wordprocessingLimits: WordprocessingInspectionLimits.Default with
            {
                MaxAttributesPerElement = 1,
            });

        AssertWordFailure(
            attributeResult,
            WordprocessingFindingCodes.ResourceLimitExceeded);
    }

    [Fact]
    public void Inspect_WordFailurePreservesVerifiedOpcButPublishesNoPartialModel()
    {
        using MemoryStream stream = WordPackageStream(
            TransitionalDocument("<w:sectPr/><w:p/>"));

        DocxInspectionResult result = inspector.Inspect(stream);

        Assert.Equal(PackageInspectionStatus.Passed, result.OpcInspection.Status);
        Assert.NotNull(result.OpcInspection.ContentTypes);
        Assert.NotNull(result.OpcInspection.Relationships);
        Assert.NotEmpty(result.OpcInspection.Parts);
        Assert.Null(result.MainDocument);
    }

    [Fact]
    public void Inspect_OpcFailureStopsBeforeWordprocessingLayer()
    {
        using MemoryStream stream = new("not-a-package"u8.ToArray());

        DocxInspectionResult result = inspector.Inspect(stream);

        Assert.Equal(PackageInspectionStatus.Failed, result.Status);
        Assert.Equal(
            PackageInspectionStatus.Failed,
            result.OpcInspection.Status);
        Assert.Empty(result.WordprocessingFindings);
        Assert.Null(result.MainDocument);
    }

    [Fact]
    public void Inspect_RestoresCallerStreamPosition()
    {
        using MemoryStream stream = WordPackageStream(TransitionalDocument());
        stream.Position = 7;

        DocxInspectionResult result = inspector.Inspect(stream);

        Assert.Equal(PackageInspectionStatus.Passed, result.Status);
        Assert.Equal(7, stream.Position);
    }

    [Fact]
    public void Inspect_CancellationAndInvalidLimits_Throw()
    {
        using MemoryStream stream = WordPackageStream(TransitionalDocument());
        using CancellationTokenSource source = new();
        source.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => inspector.Inspect(stream, cancellationToken: source.Token));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => inspector.Inspect(
                stream,
                wordprocessingLimits:
                    WordprocessingInspectionLimits.Default with
                    {
                        MaxXmlDepth = 0,
                    }));
    }

    private static MemoryStream WordPackageStream(
        string documentXml,
        string mainEntryName = "word/document.xml",
        string mainContentType = DocxDocumentInspector.MainDocumentContentType,
        string relationshipType =
            DocxDocumentInspector.TransitionalOfficeDocumentRelationshipType) =>
        PackageStream(
            ContentTypes((mainEntryName, mainContentType)),
            Relationships(Relationship("rId1", mainEntryName, relationshipType)),
            new EntrySpec(mainEntryName, documentXml));

    private static MemoryStream PackageStream(
        string contentTypes,
        string packageRelationships,
        params EntrySpec[] entries)
    {
        MemoryStream stream = new();
        using (ZipArchive archive = new(
            stream,
            ZipArchiveMode.Create,
            leaveOpen: true))
        {
            WriteEntry(archive, "[Content_Types].xml", contentTypes);
            WriteEntry(archive, "_rels/.rels", packageRelationships);
            foreach (EntrySpec entry in entries)
            {
                WriteEntry(archive, entry.Name, entry.Content);
            }
        }

        stream.Position = 0;
        return stream;
    }

    private static string ContentTypes(
        params (string PartName, string ContentType)[] overrides) => $"""
        <Types xmlns="{ContentTypesNamespace}">
          <Default Extension="rels" ContentType="{RelationshipsContentType}" />
          {string.Join(
              Environment.NewLine,
              overrides.Select(item =>
                  $"<Override PartName=\"/{item.PartName}\" ContentType=\"{item.ContentType}\" />"))}
        </Types>
        """;

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
        string type) =>
        $"<Relationship Id=\"{id}\" Type=\"{type}\" Target=\"{target}\"/>";

    private static string TransitionalDocument(string bodyContent = "") => $"""
        <w:document xmlns:w="{TransitionalWordNamespace}">
          <w:body>{bodyContent}</w:body>
        </w:document>
        """;

    private static string StrictDocument(string bodyContent = "") => $"""
        <w:document xmlns:w="{StrictWordNamespace}">
          <w:body>{bodyContent}</w:body>
        </w:document>
        """;

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

    private static void AssertWordFailure(
        DocxInspectionResult result,
        string expectedCode)
    {
        Assert.Equal(PackageInspectionStatus.Passed, result.OpcInspection.Status);
        Assert.Equal(PackageInspectionStatus.Failed, result.Status);
        Assert.Contains(
            result.WordprocessingFindings,
            finding => finding.Code == expectedCode
                && finding.Severity == FindingSeverity.Error);
        Assert.Null(result.MainDocument);
    }

    private sealed record EntrySpec(string Name, string Content);
}
