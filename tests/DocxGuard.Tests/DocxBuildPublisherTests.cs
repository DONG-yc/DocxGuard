using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxGuard.Core.Building;
using DocxGuard.Core.Indexing;
using DocxGuard.Core.Planning;

namespace DocxGuard.Tests;

public sealed class DocxBuildPublisherTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "docxguard-publish-" + Guid.NewGuid().ToString("N"));
    public DocxBuildPublisherTests() => Directory.CreateDirectory(directory);

    [Fact]
    public void Publish_PassedBuild_SavesExactDocxAndReportTogether()
    {
        DocxBuildResult build = Build();
        string destination = Path.Combine(directory, "new result");
        var result = new DocxBuildPublisher().Publish(build, destination);
        Assert.Equal(DocxPublicationStatus.Published, result.Status);
        Assert.Empty(result.Findings);
        Assert.Empty(result.ResidualPaths);
        Assert.Equal(Path.GetFullPath(destination), result.Directory);
        Assert.Equal(build.Artifact!.Bytes.ToArray(), File.ReadAllBytes(result.DocxPath!));
        Assert.Equal(Encoding.UTF8.GetBytes(build.Report.ToJson()), File.ReadAllBytes(result.ReportPath!));
        Assert.Equal(Hash(File.ReadAllBytes(result.ReportPath!)), result.ReportSha256);
        using var report = JsonDocument.Parse(File.ReadAllBytes(result.ReportPath!));
        Assert.Equal(Hash(File.ReadAllBytes(result.DocxPath!)), report.RootElement.GetProperty("outputPackageSha256").GetString());
        Assert.Equal(2, Directory.GetFiles(destination).Length);
        AssertNoStaging();
    }

    [Theory]
    [InlineData("directory")]
    [InlineData("file")]
    [InlineData("missingParent")]
    [InlineData("root")]
    public void Publish_InvalidDestination_DoesNotOverwriteOrCreateParents(string kind)
    {
        string target = Path.Combine(directory, "destination");
        if (kind == "directory") Directory.CreateDirectory(target);
        if (kind == "file") File.WriteAllText(target, "original");
        if (kind == "missingParent") target = Path.Combine(directory, "missing", "result");
        if (kind == "root") target = Path.GetPathRoot(directory)!;
        var result = new DocxBuildPublisher().Publish(Build(), target);
        AssertFailure(result, "PUBLISH_001");
        if (kind == "file") Assert.Equal("original", File.ReadAllText(target));
        if (kind == "missingParent") Assert.False(Directory.Exists(Path.Combine(directory, "missing")));
        AssertNoStaging();
    }

    [Theory]
    [InlineData("open")]
    [InlineData("write")]
    [InlineData("flush")]
    [InlineData("move")]
    public void Publish_IoFailure_CleansOwnedTemporaryFiles(string failure)
    {
        var fs = new FaultFileSystem();
        if (failure == "open") fs.FailSecondOpen = true;
        if (failure == "write") fs.FailWrite = true;
        if (failure == "flush") fs.AfterFlush = _ => throw new IOException("Injected flush failure.");
        if (failure == "move") fs.BeforeMove = (_, _) => throw new IOException("Injected move failure.");
        string target = Path.Combine(directory, "result");
        var result = new DocxBuildPublisher(fs).Publish(Build(), target);
        AssertFailure(result, "PUBLISH_002");
        Assert.Empty(result.ResidualPaths);
        Assert.False(Directory.Exists(target));
        AssertNoStaging();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Publish_DestinationCreatedAtCommit_DoesNotTouchCompetitorsFiles(bool empty)
    {
        string target = Path.Combine(directory, "result");
        var fs = new FaultFileSystem
        {
            BeforeMove = (_, destination) =>
            {
                Directory.CreateDirectory(destination);
                if (!empty) File.WriteAllText(Path.Combine(destination, "keep.txt"), "competitor");
            },
        };
        var result = new DocxBuildPublisher(fs).Publish(Build(), target);
        AssertFailure(result, "PUBLISH_002");
        if (!empty) Assert.Equal("competitor", File.ReadAllText(Path.Combine(target, "keep.txt")));
        Assert.Equal(empty ? 0 : 1, Directory.GetFiles(target).Length);
        AssertNoStaging();
    }

    [Fact]
    public void Publish_StagedBytesCorrupted_RejectsBeforeCommit()
    {
        var fs = new FaultFileSystem
        {
            AfterFlush = file =>
            {
                file.Position = 0;
                file.WriteByte(0);
                file.Flush();
            },
        };
        string target = Path.Combine(directory, "result");
        var result = new DocxBuildPublisher(fs).Publish(Build(), target);
        AssertFailure(result, "PUBLISH_003");
        Assert.False(Directory.Exists(target));
        AssertNoStaging();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Publish_CancelledBeforeCommit_CleansTemporaryFiles(bool duringWrite)
    {
        using CancellationTokenSource cancellation = new();
        var fs = new FaultFileSystem();
        if (duringWrite) fs.AfterFlush = _ => cancellation.Cancel();
        else cancellation.Cancel();
        var result = new DocxBuildPublisher(fs).Publish(Build(), Path.Combine(directory, "result"), cancellation.Token);
        Assert.Equal(DocxPublicationStatus.Cancelled, result.Status);
        Assert.Contains(result.Findings, f => f.Code == "PUBLISH_005");
        Assert.Null(result.Directory);
        Assert.Empty(result.ResidualPaths);
        AssertNoStaging();
    }

    [Fact]
    public void Publish_CancelledAfterCommit_ReportsPublishedFiles()
    {
        using CancellationTokenSource cancellation = new();
        var fs = new FaultFileSystem { AfterMove = () => cancellation.Cancel() };
        var result = new DocxBuildPublisher(fs).Publish(Build(), Path.Combine(directory, "result"), cancellation.Token);
        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(DocxPublicationStatus.Published, result.Status);
        Assert.True(File.Exists(result.DocxPath));
        Assert.True(File.Exists(result.ReportPath));
        AssertNoStaging();
    }

    [Fact]
    public void Publish_CleanupFailure_ReportsRetainedPaths()
    {
        var fs = new FaultFileSystem { FailSecondOpen = true, FailDelete = true };
        var result = new DocxBuildPublisher(fs).Publish(Build(), Path.Combine(directory, "result"));
        AssertFailure(result, "PUBLISH_002");
        Assert.Contains(result.Findings, f => f.Code == "PUBLISH_004");
        Assert.NotEmpty(result.ResidualPaths);
        Assert.All(result.ResidualPaths, path => Assert.StartsWith(directory + Path.DirectorySeparatorChar, path));
        Assert.All(result.ResidualPaths, path => Assert.True(File.Exists(path) || Directory.Exists(path)));
        Assert.Null(result.Directory);
    }

    [Fact]
    public void Publish_UnownedFileInStaging_IsNotDeletedDuringCleanup()
    {
        string? foreign = null;
        var fs = new FaultFileSystem
        {
            BeforeMove = (staging, _) =>
            {
                foreign = Path.Combine(staging, "foreign.txt");
                File.WriteAllText(foreign, "keep");
                throw new IOException("Injected failure.");
            },
        };
        var result = new DocxBuildPublisher(fs).Publish(Build(), Path.Combine(directory, "result"));
        Assert.Equal("keep", File.ReadAllText(foreign!));
        Assert.Contains(result.Findings, f => f.Code == "PUBLISH_004");
        Assert.Single(result.ResidualPaths);
    }

    [Fact]
    public void Publish_UnexpectedStagingEntry_RejectsAndPreservesUnownedEntry()
    {
        string? foreign = null;
        var fs = new FaultFileSystem
        {
            AfterFlush = stream =>
            {
                string path = ((FileStream)stream).Name;
                foreign = Path.Combine(Path.GetDirectoryName(path)!, "unexpected.txt");
                File.WriteAllText(foreign, "unowned");
            },
        };
        var result = new DocxBuildPublisher(fs).Publish(Build(), Path.Combine(directory, "result"));
        AssertFailure(result, "PUBLISH_003");
        Assert.Equal("unowned", File.ReadAllText(foreign!));
        Assert.Single(result.ResidualPaths);
    }

    [Fact]
    public void Publish_FailedBuild_ThrowsBeforeFilesystemWork()
    {
        using MemoryStream sample = RebuildTestPackage.Create();
        var index = new DocxIndexInspector().Inspect(sample);
        var plan = BoundPatchPlan.Create(index, new("target", "new")).Plan!;
        using MemoryStream invalid = new(new byte[] { 1 });
        var failed = new DocxBuildPipeline().BuildBound(invalid, plan);
        Assert.Throws<ArgumentException>(() => new DocxBuildPublisher().Publish(failed, Path.Combine(directory, "result")));
        Assert.Empty(Directory.GetFileSystemEntries(directory));
    }

    private static DocxBuildResult Build()
    {
        using MemoryStream sample = RebuildTestPackage.Create();
        var inspection = new DocxIndexInspector().Inspect(sample);
        var bound = BoundPatchPlan.Create(inspection, new("target", "new")).Plan!;
        return new DocxBuildPipeline().BuildBound(sample, bound);
    }

    private void AssertNoStaging() => Assert.Empty(Directory.GetDirectories(directory, ".docxguard-*.tmp"));
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static void AssertFailure(DocxPublicationResult result, string code)
    {
        Assert.Equal(DocxPublicationStatus.Failed, result.Status);
        Assert.Null(result.Directory);
        Assert.Null(result.DocxPath);
        Assert.Null(result.ReportPath);
        Assert.Contains(result.Findings, f => f.Code == code);
    }

    private sealed class FaultFileSystem : IPublicationFileSystem
    {
        private readonly PublicationFileSystem inner = new();
        private int opens;
        internal bool FailSecondOpen { get; set; }
        internal bool FailWrite { get; set; }
        internal bool FailDelete { get; init; }
        internal Action<Stream>? AfterFlush { get; set; }
        internal Action<string, string>? BeforeMove { get; set; }
        internal Action? AfterMove { get; init; }
        public Stream CreateNew(string path)
        {
            if (++opens == 2 && FailSecondOpen) throw new IOException("Injected second-open failure.");
            Stream stream = inner.CreateNew(path);
            return FailWrite ? new PartialWriteFailure(stream) : stream;
        }
        public void Flush(Stream stream) { inner.Flush(stream); AfterFlush?.Invoke(stream); }
        public void MoveDirectory(string source, string destination)
        {
            BeforeMove?.Invoke(source, destination);
            inner.MoveDirectory(source, destination);
            AfterMove?.Invoke();
        }
        public void DeleteFile(string path)
        {
            if (FailDelete) throw new IOException("Injected cleanup failure.");
            inner.DeleteFile(path);
        }
        public void DeleteDirectory(string path) => inner.DeleteDirectory(path);
    }

    private sealed class PartialWriteFailure(Stream inner) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override void Write(byte[] buffer, int offset, int count)
        {
            inner.Write(buffer, offset, Math.Min(1, count));
            throw new IOException("Injected partial write failure.");
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }

    public void Dispose()
    {
        Assert.StartsWith(Path.Combine(Path.GetTempPath(), "docxguard-publish-"), Path.GetFullPath(directory));
        Directory.Delete(directory, recursive: true);
    }
}
