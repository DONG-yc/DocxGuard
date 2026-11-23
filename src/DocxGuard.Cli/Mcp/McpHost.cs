using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Server;

namespace DocxGuard.Cli;

internal static class McpHost
{
    internal static async Task<int> RunAsync(string[] arguments, string version)
    {
        const string usage = "Usage: docxguard mcp --workspace <directory> [--allow-apply]";
        if (arguments is ["--help"] or ["-h"])
        {
            Console.WriteLine(usage);
            return 0;
        }

        if (arguments is not ["--workspace", _] and not ["--workspace", _, "--allow-apply"])
        {
            Console.Error.WriteLine(usage);
            return 2;
        }

        McpWorkspace workspace;
        try
        {
            workspace = new McpWorkspace(arguments[1]);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("The MCP workspace must be an existing local directory without symbolic links or junctions.");
            return 2;
        }

        HostApplicationBuilder builder = new(new HostApplicationBuilderSettings { DisableDefaults = true });
        DocxMcpTools tools = new(workspace, version);
        var server = builder.Services
            .AddMcpServer(options => options.ServerInfo = new() { Name = "DocxGuard", Version = version })
            .WithStdioServerTransport()
            .WithTools(tools, JsonSerializerOptions.Web);
        if (arguments.Length == 3)
            server.WithTools(new DocxMcpApplyTool(tools), JsonSerializerOptions.Web);

        using IHost host = builder.Build();
        await host.RunAsync();
        return 0;
    }
}
