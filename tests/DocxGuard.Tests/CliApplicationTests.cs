using DocxGuard.Cli;

namespace DocxGuard.Tests;

public sealed class CliApplicationTests
{
    private const string TestVersion = "9.8.7-test";

    [Fact]
    public void Run_WithNoArguments_WritesHelpAndSucceeds()
    {
        CliRunResult result = RunCli();

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("DocxGuard 9.8.7-test", result.StandardOutput);
        Assert.Contains("docxguard [--help | --version]", result.StandardOutput);
        Assert.Contains("docxguard inspect", result.StandardOutput);
        Assert.Empty(result.StandardError);
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    public void Run_WithHelpArgument_WritesHelpAndSucceeds(string argument)
    {
        CliRunResult result = RunCli(argument);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Usage:", result.StandardOutput);
        Assert.Empty(result.StandardError);
    }

    [Fact]
    public void Run_WithVersionArgument_WritesOnlyVersionAndSucceeds()
    {
        CliRunResult result = RunCli("--version");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal($"{TestVersion}{Environment.NewLine}", result.StandardOutput);
        Assert.Empty(result.StandardError);
    }

    [Fact]
    public void Run_WithUnknownArgument_WritesStructuredErrorAndReturnsUsageCode()
    {
        CliRunResult result = RunCli("--unknown-option");

        Assert.Equal(2, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Contains("DOCXGUARD_CLI_001", result.StandardError);
        Assert.Contains("--unknown-option", result.StandardError);
    }

    [Fact]
    public void Run_WithMultipleOptions_ReturnsUsageCode()
    {
        CliRunResult result = RunCli("--help", "--version");

        Assert.Equal(2, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Contains("--help --version", result.StandardError);
    }

    private static CliRunResult RunCli(params string[] arguments)
    {
        using StringWriter output = new();
        using StringWriter error = new();
        CliApplication application = new(TestVersion);

        int exitCode = application.Run(arguments, output, error);

        return new CliRunResult(exitCode, output.ToString(), error.ToString());
    }

    private sealed record CliRunResult(
        int ExitCode,
        string StandardOutput,
        string StandardError);
}

