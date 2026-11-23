using DocxGuard.Core;

namespace DocxGuard.Cli;

internal sealed class McpWorkspace
{
    private readonly string root;

    internal McpWorkspace(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        root = Path.GetFullPath(path);
        if (OperatingSystem.IsWindows() && Path.GetPathRoot(root)?.Length != 3)
            throw new ArgumentException("The workspace must be on a local drive.", nameof(path));

        CheckRoot();
    }

    internal string SourcePath(string relativePath)
    {
        if (!DocumentFormatPolicy.IsSupportedInput(relativePath))
            throw new ArgumentException("Source path must name a .docx file.", nameof(relativePath));

        return Resolve(relativePath, source: true);
    }

    internal string OutputDirectory(string relativePath) => Resolve(relativePath, source: false);

    private string Resolve(string relativePath, bool source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        string[] parts = relativePath.Split('/', '\\');
        if (parts.Any(part => part.Length == 0
            || part.EndsWith('.') || part.EndsWith(' ')
            || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || part.Contains(':')
            || (OperatingSystem.IsWindows() && IsReservedDevice(part))))
        {
            throw new ArgumentException("Use a workspace-relative path with ordinary file or directory names.", nameof(relativePath));
        }

        CheckRoot();
        string path = root;
        foreach (string part in parts[..^1])
        {
            path = Path.Combine(path, part);
            CheckEntry(path, directory: true);
        }

        path = Path.Combine(path, parts[^1]);
        if (source)
            CheckEntry(path, directory: false);

        return path;
    }

    private void CheckRoot()
    {
        for (DirectoryInfo? directory = new(root); directory is not null; directory = directory.Parent)
            CheckEntry(directory.FullName, directory: true);
    }

    private static void CheckEntry(string path, bool directory)
    {
        FileAttributes attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0
            || attributes.HasFlag(FileAttributes.Directory) != directory)
            throw new IOException("The path contains an unsupported filesystem entry.");
    }

    private static bool IsReservedDevice(string part)
    {
        string name = part.Split('.', 2)[0].TrimEnd(' ').ToUpperInvariant();
        return name is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$"
            || (name.Length == 4 && name[..3] is "COM" or "LPT" && "0123456789¹²³".Contains(name[3]));
    }
}
