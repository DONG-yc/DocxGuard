using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using DocxGuard.Core.Packaging;

namespace DocxGuard.Tests;

public sealed class OpcPackageInspectorTests
{
    private const string ContentTypesNamespace =
        "http://schemas.openxmlformats.org/package/2006/content-types";
    private const string WordMainContentType =
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml";
    private const string RelationshipsContentType =
        "application/vnd.openxmlformats-package.relationships+xml";
    private const string EmptyRelationships =
        "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"/>";

    private readonly OpcPackageInspector inspector = new();

    [Fact]
    public void Inspect_StandardManifest_ProducesResolvedPartInventory()
    {
        byte[] package = CreatePackage(
            StandardManifest(),
            new EntrySpec("word/document.xml", "<document/>"u8.ToArray()));
        byte[] hashBefore = SHA256.HashData(package);
        using MemoryStream stream = new(package, writable: false);
        stream.Position = 5;

        OpcInspectionResult result = inspector.Inspect(stream);

        Assert.Equal(PackageInspectionStatus.Passed, result.Status);
        Assert.NotNull(result.ContentTypes);
        Assert.Empty(result.ContentTypeFindings);
        Assert.Equal(2, result.Parts.Count);
        Assert.Contains(
            result.Parts,
            part => part.PartName.Value == "/word/document.xml"
                && part.ContentType.Value == WordMainContentType
                && part.ContentType.Source == ContentTypeSource.Override);
        Assert.Contains(
            result.Parts,
            part => part.PartName.Value == "/_rels/.rels"
                && part.ContentType.Value == RelationshipsContentType
                && part.ContentType.Source == ContentTypeSource.RelationshipFixed);
        Assert.Equal(5, stream.Position);
        Assert.True(stream.CanRead);
        Assert.Equal(hashBefore, SHA256.HashData(stream.ToArray()));
    }

    [Fact]
    public void Inspect_BindsPreflightAndXmlParsingToOneImmutableSnapshot()
    {
        byte[] firstPackage = CreatePackage(
            TypesWithRelationships("<Default Extension=\"xml\" ContentType=\"application/xml\" />"),
            new EntrySpec("custom/data.xml", "<data/>"u8.ToArray()));
        byte[] secondPackage = CreatePackage(
            TypesWithRelationships("<Default Extension=\"xml\" ContentType=\"application/zip\" />"),
            new EntrySpec("custom/data.xml", "<data/>"u8.ToArray()));
        Assert.Equal(firstPackage.Length, secondPackage.Length);
        using ResetSwitchingStream stream = new(
            firstPackage,
            secondPackage,
            switchOnZeroReset: 3,
            initialPosition: 7);

        OpcInspectionResult result = inspector.Inspect(stream);

        Assert.Equal(PackageInspectionStatus.Passed, result.Status);
        Assert.Equal(1, stream.ZeroResetCount);
        Assert.False(stream.Switched);
        Assert.Contains(
            result.Parts,
            part => part.PartName.Value == "/custom/data.xml"
                && part.ContentType.Value == "application/xml");
        Assert.Equal(7, stream.Position);
    }

    [Fact]
    public void Inspect_LengthChangeCannotBypassSnapshotLimit()
    {
        byte[] package = CreatePackage(StandardManifest());
        using ChangingLengthStream stream = new(package, firstLength: 1);

        Assert.Throws<IOException>(
            () => inspector.Inspect(
                stream,
                opcLimits: OpcInspectionLimits.Default with
                {
                    MaxPackageSnapshotBytes = 2,
                }));
        Assert.Equal(1, stream.LengthReadCount);
    }

    [Fact]
    public void Inspect_DocumentCreatedByOpenXmlSdk_Passes()
    {
        using MemoryStream generatedDocument = new();
        using (WordprocessingDocument document = WordprocessingDocument.Create(
            generatedDocument,
            DocumentFormat.OpenXml.WordprocessingDocumentType.Document,
            autoSave: true))
        {
            MainDocumentPart mainPart = document.AddMainDocumentPart();
            mainPart.Document = new Document(
                new Body(
                    new Paragraph(
                        new Run(
                            new Text("DocxGuard")))));
        }

        using MemoryStream stream = new(generatedDocument.ToArray(), writable: false);

        OpcInspectionResult result = inspector.Inspect(stream);

        Assert.Equal(PackageInspectionStatus.Passed, result.Status);
        Assert.Contains(
            result.Parts,
            part => part.PartName.Value == "/word/document.xml"
                && part.ContentType.Value == WordMainContentType);
    }

    [Fact]
    public void Inspect_PrefixedNamespaceCommentsAndProcessingInstruction_Passes()
    {
        string xml = $$"""
            <?xml version="1.0" encoding="utf-8"?>
            <?docxguard inspect?>
            <ct:Types xmlns:ct="{{ContentTypesNamespace}}">
              <!-- mappings can be interleaved -->
              <ct:Default Extension="rels" ContentType="{{RelationshipsContentType}}" />
              <ct:Override PartName="/word/document.xml" ContentType="{{WordMainContentType}}"></ct:Override>
              <ct:Default Extension="xml" ContentType="application/xml" />
            </ct:Types>
            """;
        using MemoryStream stream = PackageStream(
            Encoding.UTF8.GetBytes(xml),
            new EntrySpec("word/document.xml", "<document/>"u8.ToArray()));

        OpcInspectionResult result = inspector.Inspect(stream);

        Assert.Equal(PackageInspectionStatus.Passed, result.Status);
    }

    [Fact]
    public void Inspect_Utf16ContentTypes_Passes()
    {
        Encoding utf16 = new UnicodeEncoding(
            bigEndian: false,
            byteOrderMark: true,
            throwOnInvalidBytes: true);
        string xml = StandardManifest(encoding: "utf-16");
        byte[] content = [.. utf16.GetPreamble(), .. utf16.GetBytes(xml)];
        using MemoryStream stream = PackageStream(
            content,
            new EntrySpec("word/document.xml", "<document/>"u8.ToArray()));

        OpcInspectionResult result = inspector.Inspect(stream);

        Assert.Equal(PackageInspectionStatus.Passed, result.Status);
    }

    [Fact]
    public void Inspect_OverrideWinsAndMatchesAsciiCaseInsensitively()
    {
        string xml = TypesWithRelationships(
            "<Default Extension=\"xml\" ContentType=\"application/xml\" />",
            $"<Override PartName=\"/word/document.xml\" ContentType=\"{WordMainContentType}\" />");
        using MemoryStream stream = PackageStream(
            Encoding.UTF8.GetBytes(xml),
            new EntrySpec("WORD/DOCUMENT.XML", "<document/>"u8.ToArray()));

        OpcInspectionResult result = inspector.Inspect(stream);

        OpcPartInfo documentPart = Assert.Single(
            result.Parts,
            part => part.PackageEntryName == "WORD/DOCUMENT.XML");
        Assert.Equal(ContentTypeSource.Override, documentPart.ContentType.Source);
        Assert.Equal(WordMainContentType, documentPart.ContentType.Value);
    }

    [Fact]
    public void Inspect_DefaultMatchesExtensionUsingAsciiCaseOnly()
    {
        string xml = TypesWithRelationships(
            "<Default Extension=\"xml\" ContentType=\"application/xml\" />");
        using MemoryStream stream = PackageStream(
            Encoding.UTF8.GetBytes(xml),
            new EntrySpec("custom/archive.data.XML", "<data/>"u8.ToArray()));

        OpcInspectionResult result = inspector.Inspect(stream);

        OpcPartInfo part = Assert.Single(
            result.Parts,
            candidate => candidate.PartName.Value == "/custom/archive.data.XML");
        Assert.Equal(ContentTypeSource.Default, part.ContentType.Source);
        Assert.Equal("application/xml", part.ContentType.Value);
    }

    [Fact]
    public void Inspect_ZipPercentEncodedNonAsciiMapsToLiteralPartName()
    {
        string xml = TypesWithRelationships(
            "<Override PartName=\"/word/é.xml\" ContentType=\"application/xml\" />");
        using MemoryStream stream = PackageStream(
            Encoding.UTF8.GetBytes(xml),
            new EntrySpec("word/%c3%A9.xml", "<data/>"u8.ToArray()));

        OpcInspectionResult result = inspector.Inspect(stream);

        Assert.Equal(PackageInspectionStatus.Passed, result.Status);
        OpcPartInfo part = Assert.Single(
            result.Parts,
            candidate => candidate.PartName.Value == "/word/é.xml");
        Assert.Equal(ContentTypeSource.Override, part.ContentType.Source);
    }

    [Fact]
    public void Inspect_ExtensionlessPartCanUseOverride()
    {
        string xml = TypesWithRelationships(
            "<Override PartName=\"/custom/data\" ContentType=\"application/octet-stream\" />");
        using MemoryStream stream = PackageStream(
            Encoding.UTF8.GetBytes(xml),
            new EntrySpec("custom/data", [1, 2, 3]));

        OpcInspectionResult result = inspector.Inspect(stream);

        Assert.Equal(PackageInspectionStatus.Passed, result.Status);
        Assert.Contains(result.Parts, part => part.PartName.Value == "/custom/data");
    }

    [Fact]
    public void Inspect_ParameterizedMediaTypeOnOrdinaryPart_Passes()
    {
        string xml = TypesWithRelationships(
            "<Default Extension=\"txt\" ContentType=\"text/plain; charset=utf-8\" />");
        using MemoryStream stream = PackageStream(
            Encoding.UTF8.GetBytes(xml),
            new EntrySpec("custom/readme.txt", "hello"u8.ToArray()));

        OpcInspectionResult result = inspector.Inspect(stream);

        Assert.Equal(PackageInspectionStatus.Passed, result.Status);
        Assert.Contains(
            result.Parts,
            part => part.ContentType.Value == "text/plain; charset=utf-8");
    }

    [Fact]
    public void Inspect_RelationshipPartDoesNotRequireManifestMapping()
    {
        using MemoryStream stream = PackageStream(
            Encoding.UTF8.GetBytes(Types(
                "<Default Extension=\"xml\" ContentType=\"application/xml\" />")));

        OpcInspectionResult result = inspector.Inspect(stream);

        Assert.Equal(PackageInspectionStatus.Passed, result.Status);
        OpcPartInfo relationshipPart = Assert.Single(result.Parts);
        Assert.Equal("/_rels/.rels", relationshipPart.PartName.Value);
        Assert.Equal(RelationshipsContentType, relationshipPart.ContentType.Value);
        Assert.Equal(
            ContentTypeSource.RelationshipFixed,
            relationshipPart.ContentType.Source);
    }

    [Fact]
    public void Inspect_ManifestRequiresAtLeastOneMapping()
    {
        using MemoryStream stream = PackageStream(Encoding.UTF8.GetBytes(Types()));

        OpcInspectionResult result = inspector.Inspect(stream);

        AssertContentTypeFailure(result, ContentTypeFindingCodes.SchemaViolation);
    }

    [Fact]
    public void Inspect_UnusedDefaultIsAllowedButUnusedOverrideFails()
    {
        string xml = TypesWithRelationships(
            "<Default Extension=\"unused\" ContentType=\"application/octet-stream\" />",
            "<Override PartName=\"/missing.xml\" ContentType=\"application/xml\" />");
        using MemoryStream stream = PackageStream(Encoding.UTF8.GetBytes(xml));

        OpcInspectionResult result = inspector.Inspect(stream);

        AssertContentTypeFailure(result, ContentTypeFindingCodes.UnusedOverride);
    }

    [Fact]
    public void OpcPartName_NonAsciiCaseIsNotFolded()
    {
        OpcPartName lower = OpcPartName.Parse("/word/é.xml");
        OpcPartName upper = OpcPartName.Parse("/word/É.xml");

        Assert.NotEqual(lower, upper);
    }

    [Theory]
    [InlineData("/word/\uE000.xml")]
    [InlineData("/word/\uFDD0.xml")]
    [InlineData("/word/\U0001FFFE.xml")]
    public void OpcPartName_RejectsScalarsOutsideRfc3987UcsChar(string value)
    {
        Assert.False(OpcPartName.TryParse(value, out _));
    }

    [Fact]
    public void OpcPartName_AllowsRfc3987ColonInLogicalSegment()
    {
        Assert.True(OpcPartName.TryParse("/word/a:b.xml", out OpcPartName? partName));
        Assert.Equal("/word/a:b.xml", partName.Value);
    }

    [Theory]
    [InlineData("<Types")]
    [InlineData("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"/><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"/>")]
    [InlineData("<!DOCTYPE Types SYSTEM \"file:///does-not-exist\"><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"/>")]
    [InlineData("<!DOCTYPE Types [<!ENTITY x \"boom\">]><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">&x;</Types>")]
    public void Inspect_XmlReaderRejectsMalformedOrDtdXml(string xml)
    {
        using MemoryStream stream = PackageStream(Encoding.UTF8.GetBytes(xml));

        OpcInspectionResult result = inspector.Inspect(stream);

        AssertContentTypeFailure(result, ContentTypeFindingCodes.XmlReaderRejected);
    }

    [Fact]
    public void Inspect_UnsupportedDeclaredEncoding_Fails()
    {
        string xml = "<?xml version=\"1.0\" encoding=\"iso-8859-1\"?>"
            + Types();
        using MemoryStream stream = PackageStream(Encoding.UTF8.GetBytes(xml));

        OpcInspectionResult result = inspector.Inspect(stream);

        AssertContentTypeFailure(result, ContentTypeFindingCodes.UnsupportedXmlEncoding);
    }

    [Fact]
    public void Inspect_Utf32Signature_FailsBeforeXmlParsing()
    {
        Encoding utf32 = new UTF32Encoding(
            bigEndian: false,
            byteOrderMark: true,
            throwOnInvalidCharacters: true);
        byte[] content = [.. utf32.GetPreamble(), .. utf32.GetBytes(Types())];
        using MemoryStream stream = PackageStream(content);

        OpcInspectionResult result = inspector.Inspect(stream);

        AssertContentTypeFailure(result, ContentTypeFindingCodes.UnsupportedXmlEncoding);
    }

    [Theory]
    [InlineData("<Types xmlns=\"https://schemas.openxmlformats.org/package/2006/content-types\" />")]
    [InlineData("<Wrong xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\" />")]
    [InlineData("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\" extra=\"x\" />")]
    [InlineData("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Unknown /></Types>")]
    [InlineData("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"xml\" ContentType=\"application/xml\">text</Default></Types>")]
    [InlineData("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default xmlns:e=\"urn:evil\" Extension=\"xml\" e:Extension=\"txt\" ContentType=\"application/xml\" /></Types>")]
    [InlineData("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"xml\" /></Types>")]
    [InlineData("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Override ContentType=\"application/xml\" /></Types>")]
    public void Inspect_InvalidContentTypesShape_Fails(string xml)
    {
        using MemoryStream stream = PackageStream(Encoding.UTF8.GetBytes(xml));

        OpcInspectionResult result = inspector.Inspect(stream);

        AssertContentTypeFailure(result, ContentTypeFindingCodes.SchemaViolation);
    }

    [Theory]
    [InlineData("")]
    [InlineData(".xml")]
    [InlineData("tar.gz")]
    [InlineData("bad value")]
    [InlineData("xml/evil")]
    [InlineData("xml%GG")]
    public void Inspect_InvalidDefaultExtension_Fails(string extension)
    {
        string xml = Types(
            $"<Default Extension=\"{extension}\" ContentType=\"application/xml\" />");
        using MemoryStream stream = PackageStream(Encoding.UTF8.GetBytes(xml));

        OpcInspectionResult result = inspector.Inspect(stream);

        AssertContentTypeFailure(result, ContentTypeFindingCodes.InvalidDefaultExtension);
    }

    [Theory]
    [InlineData("word/document.xml")]
    [InlineData("//word/document.xml")]
    [InlineData("/word/../document.xml")]
    [InlineData("/word/document.xml.")]
    [InlineData("/word/%2Fdocument.xml")]
    [InlineData("/word/%64ocument.xml")]
    [InlineData("/word/document.xml?query")]
    [InlineData("/word/document.xml#fragment")]
    public void Inspect_InvalidOverridePartName_Fails(string partName)
    {
        string xml = Types(
            $"<Override PartName=\"{partName}\" ContentType=\"application/xml\" />");
        using MemoryStream stream = PackageStream(Encoding.UTF8.GetBytes(xml));

        OpcInspectionResult result = inspector.Inspect(stream);

        AssertContentTypeFailure(result, ContentTypeFindingCodes.InvalidOverridePartName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("text")]
    [InlineData(" text/plain")]
    [InlineData("text/plain ")]
    [InlineData("text/plain (comment)")]
    [InlineData("text/plain; charset =utf-8")]
    public void Inspect_InvalidMediaType_Fails(string mediaType)
    {
        string xml = Types(
            $"<Default Extension=\"txt\" ContentType=\"{mediaType}\" />");
        using MemoryStream stream = PackageStream(Encoding.UTF8.GetBytes(xml));

        OpcInspectionResult result = inspector.Inspect(stream);

        AssertContentTypeFailure(result, ContentTypeFindingCodes.InvalidMediaType);
    }

    [Fact]
    public void Inspect_DuplicateDefaultUsingAsciiCase_Fails()
    {
        string xml = Types(
            "<Default Extension=\"xml\" ContentType=\"application/xml\" />",
            "<Default Extension=\"XML\" ContentType=\"text/xml\" />");
        using MemoryStream stream = PackageStream(Encoding.UTF8.GetBytes(xml));

        OpcInspectionResult result = inspector.Inspect(stream);

        AssertContentTypeFailure(result, ContentTypeFindingCodes.DuplicateDefault);
    }

    [Fact]
    public void Inspect_DuplicateOverrideUsingAsciiCase_Fails()
    {
        string xml = Types(
            "<Override PartName=\"/word/document.xml\" ContentType=\"application/xml\" />",
            "<Override PartName=\"/WORD/DOCUMENT.XML\" ContentType=\"application/xml\" />");
        using MemoryStream stream = PackageStream(Encoding.UTF8.GetBytes(xml));

        OpcInspectionResult result = inspector.Inspect(stream);

        AssertContentTypeFailure(result, ContentTypeFindingCodes.DuplicateOverride);
    }

    [Fact]
    public void Inspect_UnmappedOrdinaryPart_FailsClosed()
    {
        using MemoryStream stream = PackageStream(
            Encoding.UTF8.GetBytes(Types(
                "<Default Extension=\"txt\" ContentType=\"text/plain\" />")),
            new EntrySpec("word/document.xml", "<document/>"u8.ToArray()));

        OpcInspectionResult result = inspector.Inspect(stream);

        AssertContentTypeFailure(result, ContentTypeFindingCodes.UnmappedPart);
    }

    [Fact]
    public void Inspect_RelationshipContentTypeConflict_Fails()
    {
        string xml = Types(
            "<Default Extension=\"rels\" ContentType=\"application/xml\" />");
        using MemoryStream stream = PackageStream(Encoding.UTF8.GetBytes(xml));

        OpcInspectionResult result = inspector.Inspect(stream);

        AssertContentTypeFailure(
            result,
            ContentTypeFindingCodes.RelationshipContentTypeConflict);
    }

    [Fact]
    public void Inspect_RootPartRelationshipUsesFixedRelationshipContentType()
    {
        string xml = Types(
            "<Default Extension=\"rels\" ContentType=\"application/xml\" />",
            $"<Override PartName=\"/_rels/.rels\" ContentType=\"{RelationshipsContentType}\" />",
            "<Default Extension=\"xml\" ContentType=\"application/xml\" />");
        using MemoryStream stream = PackageStream(
            Encoding.UTF8.GetBytes(xml),
            new EntrySpec("foo.xml", "<data/>"u8.ToArray()),
            new EntrySpec("_rels/foo.xml.rels", Encoding.UTF8.GetBytes(EmptyRelationships)));

        OpcInspectionResult result = inspector.Inspect(stream);

        AssertContentTypeFailure(
            result,
            ContentTypeFindingCodes.RelationshipContentTypeConflict);
    }

    [Fact]
    public void Inspect_RootPartRelationshipWithFixedMapping_Passes()
    {
        string xml = TypesWithRelationships(
            "<Default Extension=\"xml\" ContentType=\"application/xml\" />");
        using MemoryStream stream = PackageStream(
            Encoding.UTF8.GetBytes(xml),
            new EntrySpec("foo.xml", "<data/>"u8.ToArray()),
            new EntrySpec("_rels/foo.xml.rels", Encoding.UTF8.GetBytes(EmptyRelationships)));

        OpcInspectionResult result = inspector.Inspect(stream);

        Assert.Equal(PackageInspectionStatus.Passed, result.Status);
        Assert.Contains(
            result.Parts,
            part => part.PartName.Value == "/_rels/foo.xml.rels"
                && part.ContentType.Value == RelationshipsContentType
                && part.ContentType.Source == ContentTypeSource.RelationshipFixed);
    }

    [Fact]
    public void Inspect_ParametersOnOpcDefinedMediaType_Fail()
    {
        string xml = Types(
            $"<Default Extension=\"rels\" ContentType=\"{RelationshipsContentType}; charset=utf-8\" />");
        using MemoryStream stream = PackageStream(Encoding.UTF8.GetBytes(xml));

        OpcInspectionResult result = inspector.Inspect(stream);

        AssertContentTypeFailure(
            result,
            ContentTypeFindingCodes.OpcMediaTypeParametersProhibited);
    }

    [Fact]
    public void Inspect_MappingLimitIsEnforced()
    {
        string xml = Types(
            "<Default Extension=\"xml\" ContentType=\"application/xml\" />",
            "<Default Extension=\"txt\" ContentType=\"text/plain\" />");
        using MemoryStream stream = PackageStream(Encoding.UTF8.GetBytes(xml));
        OpcInspectionLimits limits = OpcInspectionLimits.Default with
        {
            MaxContentTypeMappings = 1,
        };

        OpcInspectionResult result = inspector.Inspect(
            stream,
            opcLimits: limits);

        AssertContentTypeFailure(result, ContentTypeFindingCodes.ResourceLimitExceeded);
    }

    [Fact]
    public void Inspect_XmlByteLimitIsEnforced()
    {
        byte[] xml = Encoding.UTF8.GetBytes(Types());
        using MemoryStream stream = PackageStream(xml);
        OpcInspectionLimits limits = OpcInspectionLimits.Default with
        {
            MaxContentTypesXmlBytes = xml.Length - 1,
        };

        OpcInspectionResult result = inspector.Inspect(
            stream,
            opcLimits: limits);

        AssertContentTypeFailure(result, ContentTypeFindingCodes.ResourceLimitExceeded);
    }

    [Fact]
    public void Inspect_PackageSnapshotAndPartSegmentLimitsAreEnforced()
    {
        byte[] package = CreatePackage(
            Types("<Default Extension=\"xml\" ContentType=\"application/xml\" />"),
            new EntrySpec("a/b/c.xml", "<data/>"u8.ToArray()));

        using MemoryStream snapshotStream = new(package, writable: false);
        OpcInspectionResult snapshotResult = inspector.Inspect(
            snapshotStream,
            opcLimits: OpcInspectionLimits.Default with
            {
                MaxPackageSnapshotBytes = package.Length - 1,
            });

        AssertContentTypeFailure(
            snapshotResult,
            ContentTypeFindingCodes.ResourceLimitExceeded);

        using MemoryStream archiveLimitStream = new(package, writable: false);
        OpcInspectionResult archiveLimitResult = inspector.Inspect(
            archiveLimitStream,
            packageLimits: PackageLimits.Default with
            {
                MaxArchiveBytes = package.Length - 1,
            });

        Assert.Equal(PackageInspectionStatus.Failed, archiveLimitResult.Status);
        Assert.Contains(
            archiveLimitResult.PackageInspection.Findings,
            finding => finding.Code == PackageFindingCodes.ArchiveTooLarge);
        Assert.Empty(archiveLimitResult.ContentTypeFindings);

        using MemoryStream segmentStream = new(package, writable: false);
        OpcInspectionResult segmentResult = inspector.Inspect(
            segmentStream,
            opcLimits: OpcInspectionLimits.Default with
            {
                MaxPartNameSegments = 2,
            });

        AssertContentTypeFailure(
            segmentResult,
            ContentTypeFindingCodes.ResourceLimitExceeded);
    }

    [Fact]
    public void Inspect_MaximumEntryProfileAvoidsQuadraticPartPrefixAllocations()
    {
        string repeatedSegments = string.Join('/', Enumerable.Repeat("s", 120));
        EntrySpec[] entries = Enumerable.Range(0, 2_045)
            .Select(index => new EntrySpec(
                $"bulk/{index:D4}/{repeatedSegments}/item.xml",
                [1]))
            .ToArray();
        using MemoryStream stream = PackageStream(
            Encoding.UTF8.GetBytes(TypesWithRelationships(
                "<Default Extension=\"xml\" ContentType=\"application/xml\" />")),
            entries);

        long before = GC.GetAllocatedBytesForCurrentThread();
        OpcInspectionResult result = inspector.Inspect(stream);
        long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(PackageInspectionStatus.Passed, result.Status);
        // Keep wide runtime headroom while catching the former substring-based
        // prefix scan, which exceeded this bound for the same controlled input.
        Assert.True(
            allocatedBytes < 96L * 1024 * 1024,
            $"Inspection allocated {allocatedBytes:N0} bytes; the bounded profile regressed.");
    }

    [Fact]
    public void Inspect_NodeAndAttributeLimitsCannotBeBypassedByCommentsOrNamespaces()
    {
        string xml = $$"""
            <Types xmlns="{{ContentTypesNamespace}}" xmlns:a="urn:123456789">
              <!-- one -->
              <!-- two -->
            </Types>
            """;

        using MemoryStream attributeStream = PackageStream(Encoding.UTF8.GetBytes(xml));
        OpcInspectionResult attributeResult = inspector.Inspect(
            attributeStream,
            opcLimits: OpcInspectionLimits.Default with
            {
                MaxAttributeValueCharacters = 8,
            });

        AssertContentTypeFailure(
            attributeResult,
            ContentTypeFindingCodes.ResourceLimitExceeded);

        using MemoryStream nodeStream = PackageStream(Encoding.UTF8.GetBytes(xml));
        OpcInspectionResult nodeResult = inspector.Inspect(
            nodeStream,
            opcLimits: OpcInspectionLimits.Default with { MaxXmlNodes = 2 });

        AssertContentTypeFailure(
            nodeResult,
            ContentTypeFindingCodes.ResourceLimitExceeded);
    }

    [Fact]
    public void Inspect_XmlCharacterAndDepthLimitsAreEnforced()
    {
        string commentHeavyXml = $$"""
            <Types xmlns="{{ContentTypesNamespace}}"><!--{{new string('x', 200)}}--></Types>
            """;
        using MemoryStream characterStream = PackageStream(
            Encoding.UTF8.GetBytes(commentHeavyXml));
        OpcInspectionResult characterResult = inspector.Inspect(
            characterStream,
            opcLimits: OpcInspectionLimits.Default with { MaxXmlCharacters = 64 });

        AssertContentTypeFailure(
            characterResult,
            ContentTypeFindingCodes.XmlReaderRejected);

        string nestedXml = $$"""
            <Types xmlns="{{ContentTypesNamespace}}"><Default Extension="xml" ContentType="application/xml"><Nested /></Default></Types>
            """;
        using MemoryStream depthStream = PackageStream(Encoding.UTF8.GetBytes(nestedXml));
        OpcInspectionResult depthResult = inspector.Inspect(
            depthStream,
            opcLimits: OpcInspectionLimits.Default with { MaxXmlDepth = 1 });

        AssertContentTypeFailure(
            depthResult,
            ContentTypeFindingCodes.ResourceLimitExceeded);
    }

    [Theory]
    [InlineData("word/bad name.xml")]
    [InlineData("word/document.xml.")]
    [InlineData("word/é.xml")]
    public void Inspect_PhysicalItemThatIsNotAnOpcPartName_Fails(string entryName)
    {
        string xml = Types(
            "<Default Extension=\"xml\" ContentType=\"application/xml\" />");
        using MemoryStream stream = PackageStream(
            Encoding.UTF8.GetBytes(xml),
            new EntrySpec(entryName, "<data/>"u8.ToArray()));

        OpcInspectionResult result = inspector.Inspect(stream);

        AssertContentTypeFailure(result, ContentTypeFindingCodes.InvalidPhysicalPartName);
    }

    [Fact]
    public void Inspect_PartCannotAlsoBeDirectoryForAnotherPart()
    {
        string xml = Types(
            "<Default Extension=\"xml\" ContentType=\"application/xml\" />",
            "<Override PartName=\"/word\" ContentType=\"application/octet-stream\" />");
        using MemoryStream stream = PackageStream(
            Encoding.UTF8.GetBytes(xml),
            new EntrySpec("word", [1]),
            new EntrySpec("word-extra.xml", "<data/>"u8.ToArray()),
            new EntrySpec("word/document.xml", "<data/>"u8.ToArray()));

        OpcInspectionResult result = inspector.Inspect(stream);

        AssertContentTypeFailure(result, ContentTypeFindingCodes.InvalidPhysicalPartName);
    }

    [Fact]
    public void Inspect_InvalidPackageStopsBeforeXmlLayer()
    {
        using MemoryStream stream = new("not-a-package"u8.ToArray());

        OpcInspectionResult result = inspector.Inspect(stream);

        Assert.Equal(PackageInspectionStatus.Failed, result.Status);
        Assert.Contains(
            result.PackageInspection.Findings,
            finding => finding.Code == PackageFindingCodes.InvalidZip);
        Assert.Empty(result.ContentTypeFindings);
        Assert.Null(result.ContentTypes);
    }

    [Fact]
    public void Inspect_CancellationPropagatesAndInvalidLimitsThrow()
    {
        using MemoryStream stream = PackageStream(Encoding.UTF8.GetBytes(Types()));
        using CancellationTokenSource source = new();
        source.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => inspector.Inspect(stream, cancellationToken: source.Token));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => inspector.Inspect(
                stream,
                opcLimits: OpcInspectionLimits.Default with { MaxXmlDepth = 0 }));
    }

    private static string StandardManifest(string encoding = "utf-8") => $$"""
        <?xml version="1.0" encoding="{{encoding}}"?>
        <Types xmlns="{{ContentTypesNamespace}}">
          <Default Extension="rels" ContentType="{{RelationshipsContentType}}" />
          <Default Extension="xml" ContentType="application/xml" />
          <Override PartName="/word/document.xml" ContentType="{{WordMainContentType}}" />
        </Types>
        """;

    private static string Types(params string[] mappings) => $$"""
        <Types xmlns="{{ContentTypesNamespace}}">
        {{string.Join(Environment.NewLine, mappings)}}
        </Types>
        """;

    private static string TypesWithRelationships(params string[] mappings) =>
        Types(
            [
                $"<Default Extension=\"rels\" ContentType=\"{RelationshipsContentType}\" />",
                .. mappings,
            ]);

    private static MemoryStream PackageStream(
        byte[] contentTypes,
        params EntrySpec[] entries) =>
        new(CreatePackage(contentTypes, entries), writable: false);

    private static byte[] CreatePackage(string contentTypes, params EntrySpec[] entries) =>
        CreatePackage(Encoding.UTF8.GetBytes(contentTypes), entries);

    private static byte[] CreatePackage(byte[] contentTypes, params EntrySpec[] entries)
    {
        using MemoryStream stream = new();
        using (ZipArchive archive = new(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(archive, "[Content_Types].xml", contentTypes);
            WriteEntry(archive, "_rels/.rels", Encoding.UTF8.GetBytes(EmptyRelationships));
            foreach (EntrySpec entry in entries)
            {
                WriteEntry(archive, entry.Name, entry.Content);
            }
        }

        return stream.ToArray();
    }

    private static void WriteEntry(ZipArchive archive, string name, byte[] content)
    {
        ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
        using Stream entryStream = entry.Open();
        entryStream.Write(content);
    }

    private static void AssertContentTypeFailure(
        OpcInspectionResult result,
        string expectedCode)
    {
        Assert.Equal(PackageInspectionStatus.Passed, result.PackageInspection.Status);
        Assert.Equal(PackageInspectionStatus.Failed, result.Status);
        Assert.Contains(
            result.ContentTypeFindings,
            finding => finding.Code == expectedCode
                && finding.Severity == FindingSeverity.Error);
        Assert.Null(result.ContentTypes);
        Assert.Empty(result.Parts);
    }

    private sealed record EntrySpec(string Name, byte[] Content);

    private sealed class ChangingLengthStream : Stream
    {
        private readonly MemoryStream inner;
        private readonly long firstLength;

        public ChangingLengthStream(byte[] content, long firstLength)
        {
            inner = new MemoryStream(content, writable: false);
            this.firstLength = firstLength;
        }

        public int LengthReadCount { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length => ++LengthReadCount == 1
            ? firstLength
            : inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) =>
            inner.Read(buffer, offset, count);

        public override int Read(Span<byte> buffer) => inner.Read(buffer);

        public override long Seek(long offset, SeekOrigin origin) =>
            inner.Seek(offset, origin);

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class ResetSwitchingStream : Stream
    {
        private readonly byte[] first;
        private readonly byte[] second;
        private readonly int switchOnZeroReset;
        private long position;

        public ResetSwitchingStream(
            byte[] first,
            byte[] second,
            int switchOnZeroReset,
            long initialPosition)
        {
            this.first = first;
            this.second = second;
            this.switchOnZeroReset = switchOnZeroReset;
            position = initialPosition;
        }

        public int ZeroResetCount { get; private set; }

        public bool Switched => ZeroResetCount >= switchOnZeroReset;

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length => first.Length;

        public override long Position
        {
            get => position;
            set
            {
                ArgumentOutOfRangeException.ThrowIfNegative(value);
                if (value == 0)
                {
                    ZeroResetCount++;
                }

                position = value;
            }
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            byte[] active = Switched ? second : first;
            int available = (int)Math.Min(buffer.Length, active.Length - position);
            if (available <= 0)
            {
                return 0;
            }

            active.AsSpan((int)position, available).CopyTo(buffer);
            position += available;
            return available;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            long next = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => position + offset,
                SeekOrigin.End => Length + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin)),
            };

            Position = next;
            return position;
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}
