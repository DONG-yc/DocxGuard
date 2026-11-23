using System.IO.Compression;
using System.Text;
using DocxGuard.Core.Wordprocessing;

namespace DocxGuard.Tests;

internal static class IndexTestPackage
{
    public const string TransitionalWordNamespace =
        "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    public const string StrictWordNamespace =
        "http://purl.oclc.org/ooxml/wordprocessingml/main";

    private const string ContentTypesNamespace =
        "http://schemas.openxmlformats.org/package/2006/content-types";
    private const string RelationshipsNamespace =
        "http://schemas.openxmlformats.org/package/2006/relationships";
    private const string RelationshipsContentType =
        "application/vnd.openxmlformats-package.relationships+xml";

    public static MemoryStream WordStream(
        string bodyContent,
        bool strict = false)
    {
        string wordNamespace = strict
            ? StrictWordNamespace
            : TransitionalWordNamespace;
        string relationshipType = strict
            ? DocxDocumentInspector.StrictOfficeDocumentRelationshipType
            : DocxDocumentInspector.TransitionalOfficeDocumentRelationshipType;
        string documentXml = $"""
            <w:document xmlns:w="{wordNamespace}">
              <w:body>{bodyContent}</w:body>
            </w:document>
            """;
        string contentTypes = $"""
            <Types xmlns="{ContentTypesNamespace}">
              <Default Extension="rels" ContentType="{RelationshipsContentType}" />
              <Override PartName="/word/document.xml" ContentType="{DocxDocumentInspector.MainDocumentContentType}" />
            </Types>
            """;
        string relationships = $"""
            <Relationships xmlns="{RelationshipsNamespace}">
              <Relationship Id="rId1" Type="{relationshipType}" Target="word/document.xml" />
            </Relationships>
            """;

        MemoryStream stream = new();
        using (ZipArchive archive = new(
            stream,
            ZipArchiveMode.Create,
            leaveOpen: true))
        {
            WriteEntry(archive, "[Content_Types].xml", contentTypes);
            WriteEntry(archive, "_rels/.rels", relationships);
            WriteEntry(archive, "word/document.xml", documentXml);
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
        entryStream.Write(Encoding.UTF8.GetBytes(content));
    }
}
