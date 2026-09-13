using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace TeamSpeak.Mcp.Hosting;

/// <summary>
/// Builds the host for either MCP transport.
/// </summary>
/// <remarks>
/// The two transports share their service registration so that tools, resources and prompts behave
/// identically no matter how a client reaches the server.
/// </remarks>
public static class McpHostFactory
{
    /// <summary>
    /// Builds a host that serves MCP over stdin/stdout.
    /// </summary>
    /// <param name="args">The process arguments, forwarded to the configuration system.</param>
    /// <returns>An unstarted host.</returns>
    public static IHost CreateStdioHost(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        // stdout carries the MCP protocol itself, so every log line has to go to stderr.
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithToolsFromAssembly()
            .WithPromptsFromAssembly()
            .WithResourcesFromAssembly();

        return builder.Build();
    }

    /// <summary>
    /// Builds a web application that serves MCP over Streamable HTTP.
    /// </summary>
    /// <param name="args">The process arguments, forwarded to the configuration system.</param>
    /// <param name="url">The URL to bind.</param>
    /// <returns>An unstarted web application with the MCP endpoint mapped.</returns>
    public static WebApplication CreateHttpApp(string[] args, string url)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.WebHost.UseUrls(url);

        builder.Services
            .AddMcpServer()
            .WithHttpTransport()
            .WithToolsFromAssembly()
            .WithPromptsFromAssembly()
            .WithResourcesFromAssembly();

        var app = builder.Build();
        app.MapMcp();
        return app;
    }
}