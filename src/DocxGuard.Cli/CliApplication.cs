namespace DocxGuard.Cli;

public sealed class CliApplication
{
    private readonly string _version;

    public CliApplication(string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        _version = version;
    }

    public int Run(
        IReadOnlyList<string> arguments,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        if (arguments.Count == 0 || IsSingleArgument(arguments, "--help", "-h"))
        {
            WriteHelp(output);
            return 0;
        }

        if (IsSingleArgument(arguments, "--version"))
        {
            output.WriteLine(_version);
            return 0;
        }

        if (arguments.Count > 0 && arguments[0] == "inspect")
        {
            return InspectCommand.Run(arguments, _version, output, error, cancellationToken);
        }

        if (arguments.Count > 0 && arguments[0] == "multi-plan")
        {
            return MultiPlanningCommand.Run(arguments, _version, output, error, cancellationToken);
        }

        if (arguments.Count > 0 && arguments[0] is "plan" or "dry-run" or "apply")
        {
            return PlanningCommand.Run(arguments, _version, output, error, cancellationToken);
        }

        error.WriteLine(
            $"DOCXGUARD_CLI_001: Unknown argument(s): {string.Join(" ", arguments)}");
        error.WriteLine("Run 'docxguard --help' for usage information.");
        return 2;
    }

    private static bool IsSingleArgument(
        IReadOnlyList<string> arguments,
        params string[] expectedValues)
    {
        return arguments.Count == 1
            && expectedValues.Contains(arguments[0], StringComparer.Ordinal);
    }

    private void WriteHelp(TextWriter writer)
    {
        writer.WriteLine($"DocxGuard {_version}");
        writer.WriteLine("Contract-driven, verifiable DOCX tooling.");
        writer.WriteLine();
        writer.WriteLine("Usage:");
        writer.WriteLine("  docxguard [--help | --version]");
        writer.WriteLine("  docxguard inspect [--json] [--offset N] [--limit N] [--] <file.docx>");
        writer.WriteLine("  docxguard plan <file.docx> --find <text> --replace <text> [--paragraph <id>] [--json]");
        writer.WriteLine("  docxguard multi-plan <file.docx> --requests <requests.json> [--json]");
        writer.WriteLine("  docxguard dry-run <file.docx> --plan <plan.json> [--json]");
        writer.WriteLine("  docxguard apply <file.docx> --plan <plan.json> --output-dir <new-directory> [--json]");
        writer.WriteLine("  docxguard mcp --workspace <directory> [--allow-apply]");
        writer.WriteLine();
        writer.WriteLine("Options:");
        writer.WriteLine("  -h, --help     Show this help text.");
        writer.WriteLine("      --version  Show the application version.");
        writer.WriteLine();
        writer.WriteLine(
            "Read-only DOCX inspection with paragraph previews. Run 'docxguard inspect --help' for details.");
    }
}

