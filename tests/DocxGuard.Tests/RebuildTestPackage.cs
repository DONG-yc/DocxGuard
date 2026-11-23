using System.IO.Compression;
using System.Text;
using DocxGuard.Core.Execution;
using DocxGuard.Core.Indexing;
using DocxGuard.Core.Packaging;
using DocxGuard.Core.Planning;

namespace DocxGuard.Tests;

internal static class RebuildTestPackage
{
    public const string Main = "word/document.xml";
    public const string Media = "word/media/logo.bin";
    public const string Styles = "word/styles.xml";
    public const string MainRelationships = "word/_rels/document.xml.rels";

    public static MemoryStream Create(string? body = null, bool strict = false,
        string mainEntryName = Main, bool resources = true)
    {
        using MemoryStream minimal = IndexTestPackage.WordStream(body ?? """
            <w:p><w:pPr><w:jc w:val="left"/></w:pPr>
              <w:r><w:rPr><w:b/></w:rPr><w:t>pre tar</w:t></w:r>
              <w:r><w:rPr><w:i/></w:rPr><w:t>get post</w:t></w:r>
            </w:p>
            <w:p><w:r><w:t>untouched</w:t></w:r></w:p>
            <!-- preserved comment --><?docxguard preserved?>
            <w:sectPr><w:pgSz w:w="12240" w:h="15840"/></w:sectPr>
            """, strict);
        byte[] result = Rewrite(minimal.ToArray(), entries =>
        {
            if (resources)
            {
                entries[Media] = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
                entries[Styles] = Encoding.UTF8.GetBytes(
                    $"<w:styles xmlns:w=\"{(strict ? IndexTestPackage.StrictWordNamespace : IndexTestPackage.TransitionalWordNamespace)}\"/>");
                entries[MainRelationships] = Encoding.UTF8.GetBytes("""
                    <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                      <Relationship Id="rImage" Type="urn:docxguard:image" Target="media/logo.bin"/>
                      <Relationship Id="rStyle" Type="urn:docxguard:styles" Target="styles.xml"/>
                    </Relationships>
                    """);
                ReplaceText(entries, "[Content_Types].xml", "</Types>", """
                    <Default Extension="bin" ContentType="application/octet-stream"/>
                    <Override PartName="/word/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml"/>
                    </Types>
                    """);
            }

            if (mainEntryName != Main)
            {
                entries[mainEntryName] = entries[Main];
                entries.Remove(Main);
                string logicalName = Uri.UnescapeDataString(mainEntryName);
                ReplaceText(entries, "[Content_Types].xml", Main, logicalName);
                ReplaceText(entries, "_rels/.rels", Main, logicalName);
            }
        });
        return new MemoryStream(result, writable: false);
    }

    public static InMemoryMainDocumentPatch Patch(Stream source, string replacement = "replacement")
    {
        DocxIndexInspectionResult inspection = new DocxIndexInspector().Inspect(source);
        Assert.Equal(PackageInspectionStatus.Passed, inspection.Status);
        PatchPlan plan = new TextPatchPlanner().CreatePlan(inspection.Index!,
            new TextReplacementRequest("target", replacement)).Plan!;
        TextPatchExecutionResult execution = new InMemoryTextPatchExecutor().Execute(inspection, plan);
        Assert.Equal(TextPatchExecutionStatus.Passed, execution.Status);
        return execution.Artifact!;
    }

    public static byte[] Rewrite(byte[] package, Action<Dictionary<string, byte[]>> edit,
        CompressionLevel compression = CompressionLevel.NoCompression)
    {
        Dictionary<string, byte[]> entries = Read(package);
        edit(entries);
        using MemoryStream output = new();
        using (ZipArchive archive = new(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string name, byte[] bytes) in entries)
            {
                ZipArchiveEntry entry = archive.CreateEntry(name, compression);
                entry.LastWriteTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using Stream content = entry.Open();
                content.Write(bytes);
            }
        }

        return output.ToArray();
    }

    public static Dictionary<string, byte[]> Read(byte[] package)
    {
        using MemoryStream stream = new(package, writable: false);
        using ZipArchive archive = new(stream, ZipArchiveMode.Read);
        return archive.Entries.ToDictionary(entry => entry.FullName, entry =>
        {
            using Stream content = entry.Open();
            using MemoryStream copy = new();
            content.CopyTo(copy);
            return copy.ToArray();
        }, StringComparer.Ordinal);
    }

    public static void ReplaceText(Dictionary<string, byte[]> entries, string name,
        string expected, string replacement)
    {
        string original = Encoding.UTF8.GetString(entries[name]);
        Assert.Contains(expected, original);
        entries[name] = Encoding.UTF8.GetBytes(original.Replace(expected, replacement, StringComparison.Ordinal));
    }
}
