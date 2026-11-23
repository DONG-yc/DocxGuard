using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using DocxGuard.Core;
using DocxGuard.Core.Indexing;
using DocxGuard.Core.Packaging;

namespace DocxGuard.Cli;

internal static class TableFigureCommand
{
    internal static int Run(IReadOnlyList<string> args, TextWriter output, TextWriter error, CancellationToken token)
    {
        bool list = args.Count >= 2 && args[1] == "list";
        bool apply = args.Count == 7 && args[1] == "apply";
        int offset = 0, limit = 50;
        if (list && args.Count != 3 && !(args.Count == 5 && int.TryParse(args[3], out offset) && int.TryParse(args[4], out limit)))
        {
            error.WriteLine("Use table-figure list <docx> [offset limit].");
            return 2;
        }
        if (!list && !apply && !(args.Count == 5 && args[1] == "preview"))
        {
            error.WriteLine("Use table-figure preview <docx> <json> <png> or table-figure apply <docx> <json> <png> <preview-sha256> <new-directory>.");
            return 2;
        }
        try
        {
            if (list)
            {
                if (!DocumentFormatPolicy.IsSupportedInput(args[2])) throw new ArgumentException("Expected a DOCX file.");
                var inspection = new DocxIndexInspector().Inspect(args[2], cancellationToken: token);
                output.WriteLine(JsonSerializer.Serialize(TableFigureCatalog.Read(inspection, offset, limit), InspectReport.JsonOptions));
                return 0;
            }
            var result = Build(args[2], args[3], args[4], apply ? args[5] : null, apply ? args[6] : null, token);
            output.WriteLine(result.GetRawText());
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException)
        {
            error.WriteLine(ex.Message);
            return 1;
        }
    }

    internal static JsonElement Build(string source, string request, string figure, string? expected,
        string? destination, CancellationToken token)
    {
        if (!DocumentFormatPolicy.IsSupportedInput(source) ||
            !Path.GetExtension(request).Equals(".json", StringComparison.OrdinalIgnoreCase) ||
            !Path.GetExtension(figure).Equals(".png", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Expected DOCX, JSON and PNG inputs.");
        if (destination != null && (expected?.Length != 64 || !expected.All(Uri.IsHexDigit)))
            throw new ArgumentException("Apply requires the SHA-256 from preview.");
        string? python = Environment.GetEnvironmentVariable("DOCXGUARD_PYTHON");
        if (string.IsNullOrWhiteSpace(python) || !Path.IsPathFullyQualified(python) || !File.Exists(python))
            throw new ArgumentException("Set DOCXGUARD_PYTHON to the absolute path of Python with Pillow and lxml.");
        using var sourceLock = OpenBounded(source, PackageLimits.Default.MaxArchiveBytes);
        using var requestLock = OpenBounded(request, 4 * 1024 * 1024);
        using var imageLock = OpenBounded(figure, 32_000_000);
        string parent = Path.GetTempPath();
        if (destination != null)
        {
            destination = Path.GetFullPath(destination);
            parent = Path.GetDirectoryName(destination) ?? throw new ArgumentException("Expected a new directory.");
            if (!Directory.Exists(parent) || Path.Exists(destination))
                throw new IOException("Choose a new output directory under an existing parent.");
        }
        string stage = Path.Combine(parent, ".docxguard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        bool published = false;
        try
        {
            string candidate = Path.Combine(stage, "output.docx");
            var start = new ProcessStartInfo(python) { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string arg in new[] { "-I", "-X", "utf8", Path.Combine(AppContext.BaseDirectory, "workers", "table-figure.py"),
                Path.GetFullPath(source), Path.GetFullPath(request), Path.GetFullPath(figure), candidate })
                start.ArgumentList.Add(arg);
            string host = Environment.ProcessPath!;
            string[] inspect = Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
                ? [host, typeof(CliApplication).Assembly.Location] : [host];
            start.Environment["DOCXGUARD_INSPECT_COMMAND"] = JsonSerializer.Serialize(inspect);
            token.ThrowIfCancellationRequested();
            using Process worker = Process.Start(start) ?? throw new IOException("Unable to launch the Python worker.");
            worker.StandardInput.Close();
            var stdout = worker.StandardOutput.ReadToEndAsync();
            var stderr = worker.StandardError.ReadToEndAsync();
            using var registration = token.Register(() =>
            {
                try { if (!worker.HasExited) worker.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
            });
            worker.WaitForExit();
            token.ThrowIfCancellationRequested();
            if (worker.ExitCode != 0)
            {
                string detail = stderr.GetAwaiter().GetResult().TrimEnd().Split('\n').Last();
                throw new InvalidDataException("Table/figure edit rejected. " + detail);
            }
            using JsonDocument report = JsonDocument.Parse(stdout.GetAwaiter().GetResult());
            using (var bytes = File.OpenRead(candidate))
            {
                string digest = Convert.ToHexStringLower(SHA256.HashData(bytes));
                if (digest != report.RootElement.GetProperty("outputSha256").GetString() ||
                    (destination != null && !digest.Equals(expected, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException("Output differs from preview. Preview the current inputs again.");
            }
            var result = JsonSerializer.SerializeToElement(new { status = destination == null ? "previewed" : "published",
                buildReport = report.RootElement });
            if (destination != null)
            {
                File.WriteAllText(Path.Combine(stage, "build-report.json"), report.RootElement.GetRawText());
                token.ThrowIfCancellationRequested();
                Directory.Move(stage, destination);
                published = true;
            }
            return result;
        }
        catch (Win32Exception ex) { throw new IOException("Unable to run the configured Python interpreter.", ex); }
        finally { if (!published) Directory.Delete(stage, recursive: true); }
    }

    private static FileStream OpenBounded(string path, long limit)
    {
        var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length <= limit) return stream;
        stream.Dispose();
        throw new ArgumentException("Input exceeds its configured size limit.");
    }
}
