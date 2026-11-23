using System.Security.Cryptography;
using System.Text;

namespace DocxGuard.Core.Building;

/// <summary>Publishes a verified build as a new directory containing both DOCX and report.</summary>
public sealed class DocxBuildPublisher
{
    private readonly IPublicationFileSystem fileSystem;
    public DocxBuildPublisher() : this(new PublicationFileSystem()) { }
    internal DocxBuildPublisher(IPublicationFileSystem fileSystem) => this.fileSystem = fileSystem;

    public DocxPublicationResult Publish(DocxBuildResult build, string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(build);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        if (build.Status != DocxBuildStatus.Passed || build.Artifact is null)
            throw new ArgumentException("Only a passed build can be published.", nameof(build));

        string? destination = null;
        string? staging = null;
        string? reportHash = null;
        List<string> ownedFiles = [];
        List<string> residualPaths = [];
        List<DocxPublicationFinding> findings = [];
        DocxPublicationStatus status = DocxPublicationStatus.Failed;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            destination = ValidateDestination(outputDirectory);
            string parent = Path.GetDirectoryName(destination)!;
            string candidate = Path.Combine(parent, ".docxguard-" + Guid.NewGuid().ToString("N") + ".tmp");
            if (Exists(candidate)) throw new IOException("The staging directory already exists.");
            Directory.CreateDirectory(candidate);
            staging = candidate;
            byte[] docx = build.Artifact.Bytes.ToArray();
            byte[] report = Encoding.UTF8.GetBytes(build.Report.ToJson());
            reportHash = Hash(report);
            string stagedDocx = Path.Combine(staging, "output.docx");
            string stagedReport = Path.Combine(staging, "build-report.json");
            WriteFile(stagedDocx, docx, ownedFiles, cancellationToken);
            WriteFile(stagedReport, report, ownedFiles, cancellationToken);
            VerifyFile(stagedDocx, build.Artifact.OutputPackageSha256);
            VerifyFile(stagedReport, reportHash);
            if (Directory.GetFileSystemEntries(staging).Length != 2)
                throw new VerificationException("Staging contains unexpected entries.");
            cancellationToken.ThrowIfCancellationRequested();
            ValidateDestination(destination);
            fileSystem.MoveDirectory(staging, destination);
            // Commit won: subsequent cancellation must not describe published files as cancelled.
            staging = null;
            status = DocxPublicationStatus.Published;
        }
        catch (DestinationException exception)
        {
            findings.Add(new("PUBLISH_001", exception.Message, outputDirectory));
        }
        catch (VerificationException exception)
        {
            findings.Add(new("PUBLISH_003", exception.Message));
        }
        catch (OperationCanceledException)
        {
            status = DocxPublicationStatus.Cancelled;
            findings.Add(new("PUBLISH_005", "Publication was cancelled before commit."));
        }
        catch (Exception exception) when (IsFileError(exception))
        {
            findings.Add(new("PUBLISH_002", exception.Message, destination ?? outputDirectory));
        }
        finally
        {
            if (staging is not null)
            {
                foreach (string file in ownedFiles)
                    Cleanup(file, () => fileSystem.DeleteFile(file), findings, residualPaths);
                Cleanup(staging, () => fileSystem.DeleteDirectory(staging), findings, residualPaths);
            }
        }

        return new(status, status == DocxPublicationStatus.Published ? destination : null,
            status == DocxPublicationStatus.Published ? reportHash : null, findings, residualPaths);
    }

    private void WriteFile(string path, byte[] bytes, List<string> ownedFiles, CancellationToken token)
    {
        using Stream file = fileSystem.CreateNew(path);
        ownedFiles.Add(path);
        for (int offset = 0; offset < bytes.Length;)
        {
            token.ThrowIfCancellationRequested();
            int count = Math.Min(8192, bytes.Length - offset);
            file.Write(bytes, offset, count);
            offset += count;
        }

        token.ThrowIfCancellationRequested();
        fileSystem.Flush(file);
    }

    private static void VerifyFile(string path, string expected)
    {
        using FileStream file = File.OpenRead(path);
        string actual = Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();
        if (actual != expected) throw new VerificationException("Staged bytes differ from the verified build or report.");
    }

    private static string ValidateDestination(string path)
    {
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        string root = Path.GetPathRoot(full)!;
        if (full == root || Exists(full))
            throw new DestinationException("Output must be a new directory; existing files/directories are never overwritten.");
        string? parent = Path.GetDirectoryName(full);
        if (parent is null || !Directory.Exists(parent))
            throw new DestinationException("The output parent directory must already exist.");
        if (OperatingSystem.IsWindows())
        {
            foreach (string component in full[root.Length..].Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                if (component.EndsWith(' ') || component.EndsWith('.') || component.Contains(':'))
                    throw new DestinationException("Output path components cannot contain alternate streams or end in a dot/space.");
        }

        for (DirectoryInfo? ancestor = new(parent); ancestor is not null; ancestor = ancestor.Parent)
            if ((ancestor.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new DestinationException("Output parent paths cannot traverse symbolic links or reparse points.");
        return full;
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path)
        || new FileInfo(path).LinkTarget is not null || new DirectoryInfo(path).LinkTarget is not null;

    private static void Cleanup(string path, Action delete,
        List<DocxPublicationFinding> findings, List<string> residualPaths)
    {
        try { delete(); }
        catch (Exception exception) when (IsFileError(exception))
        {
            findings.Add(new("PUBLISH_004", "Temporary cleanup failed: " + exception.Message, path));
            residualPaths.Add(path);
        }
    }

    private static bool IsFileError(Exception exception) => exception is IOException
        or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException;
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private sealed class DestinationException(string message) : Exception(message);
    private sealed class VerificationException(string message) : Exception(message);
}
