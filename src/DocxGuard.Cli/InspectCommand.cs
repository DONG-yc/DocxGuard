using System.Globalization;
using DocxGuard.Core;
using DocxGuard.Core.Indexing;

namespace DocxGuard.Cli;

internal static class InspectCommand
{
    internal static int Run(IReadOnlyList<string> arguments, string version,
        TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        if (arguments.Count == 2 && arguments[1] is "--help" or "-h")
        {
            output.WriteLine("Usage: docxguard inspect [--json] [--offset N] [--limit N] [--] <file.docx>");
            output.WriteLine("Read-only inspection; default offset 0, limit 20 (1-1000). Text previews: up to 200 UTF-16 units.");
            output.WriteLine("Exit codes: 0 passed; 2 usage/format; 3 input I/O; 4 rejected document; 130 cancelled.");
            return 0;
        }

        bool json = arguments.Skip(1).TakeWhile(a => a != "--").Contains("--json", StringComparer.Ordinal);
        string? path = null;
        int offset = 0;
        int limit = 20;
        bool positionalOnly = false;
        HashSet<string> seen = new(StringComparer.Ordinal);
        string? usageError = null;
        for (int i = 1; i < arguments.Count; i++)
        {
            string value = arguments[i];
            if (!positionalOnly && value == "--")
            {
                positionalOnly = true;
                continue;
            }

            if (!positionalOnly && value.StartsWith("-", StringComparison.Ordinal))
            {
                if (!seen.Add(value)) { usageError = "Duplicate option."; break; }
                if (value == "--json") continue;
                if (value is not ("--offset" or "--limit")) { usageError = "Unknown inspect option."; break; }
                if (++i >= arguments.Count || !int.TryParse(arguments[i], NumberStyles.None,
                    CultureInfo.InvariantCulture, out int number)
                    || (value == "--limit" && (number < 1 || number > 1000)))
                {
                    usageError = "--offset requires a nonnegative integer; --limit requires an integer from 1 to 1000.";
                    break;
                }

                if (value == "--offset") offset = number; else limit = number;
                continue;
            }

            if (path is not null) { usageError = "Inspect accepts exactly one input path."; break; }
            path = value;
        }

        InspectReport report;
        if (usageError is not null || string.IsNullOrWhiteSpace(path))
        {
            report = InspectReport.Failure(version, 2, "DOCXGUARD_CLI_001",
                usageError ?? "Inspect requires one DOCX path. Run 'docxguard inspect --help'.");
        }
        else
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!DocumentFormatPolicy.IsSupportedInput(path))
                    report = InspectReport.Failure(version, 2, "DOCXGUARD_CLI_002", "Only .docx input files are supported.");
                else
                    report = InspectReport.Create(version,
                        new DocxIndexInspector().Inspect(path, cancellationToken: cancellationToken), offset, limit);
            }
            catch (OperationCanceledException)
            {
                report = InspectReport.Failure(version, 130, "DOCXGUARD_CLI_004", "Inspection was cancelled.");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                or ArgumentException or NotSupportedException or System.Security.SecurityException)
            {
                report = InspectReport.Failure(version, 3, "DOCXGUARD_CLI_003", exception.Message);
            }
        }

        report.Write(output, error, json);
        return report.ExitCode;
    }
}
