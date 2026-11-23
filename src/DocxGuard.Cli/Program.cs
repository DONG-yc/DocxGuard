using System.Reflection;
using System.Text;
using DocxGuard.Cli;

Assembly assembly = Assembly.GetExecutingAssembly();
string version = assembly
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
    .InformationalVersion
    ?? assembly.GetName().Version?.ToString()
    ?? "unknown";

Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
if (args is ["mcp", .. var mcpArgs])
    return await McpHost.RunAsync(mcpArgs, version);

using CancellationTokenSource cancellation = new();
ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};
Console.CancelKeyPress += cancelHandler;
try
{
    return new CliApplication(version).Run(args, Console.Out, Console.Error, cancellation.Token);
}
finally
{
    Console.CancelKeyPress -= cancelHandler;
}

