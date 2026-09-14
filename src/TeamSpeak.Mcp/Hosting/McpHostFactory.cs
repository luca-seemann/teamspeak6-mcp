using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using TeamSpeak.Mcp.Configuration;
using TeamSpeak.Mcp.Tools;
using TeamSpeak.Query.Client;

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
    /// The environment variable prefix configuration is also read from.
    /// </summary>
    /// <remarks>
    /// Secrets belong in the environment rather than in a file, so
    /// <c>TSMCP_TeamSpeak__Profiles__prod__Password</c> overrides whatever the file says. This is
    /// also the only practical way to configure the server from an MCP client's own JSON config or
    /// from a container.
    /// </remarks>
    public const string EnvironmentPrefix = "TSMCP_";

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

        builder.Configuration.AddEnvironmentVariables(EnvironmentPrefix);
        AddTeamSpeak(builder.Services, builder.Configuration);

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

        builder.Configuration.AddEnvironmentVariables(EnvironmentPrefix);
        AddTeamSpeak(builder.Services, builder.Configuration);

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

    /// <summary>
    /// Registers the TeamSpeak options, profiles, connections, safety policy and tool executor.
    /// </summary>
    /// <param name="services">The service collection to add to.</param>
    /// <param name="configuration">The configuration to bind from.</param>
    /// <returns>The service collection, for chaining.</returns>
    /// <remarks>
    /// <para>
    /// Profiles are validated while the registry is being built, so a mistyped host or a missing
    /// password fails at startup with a clear message rather than on the first tool call.
    /// </para>
    /// <para>
    /// Everything is a singleton. The connection manager in particular must outlive any MCP
    /// session: the Streamable HTTP transport is stateless, and a connection per session would
    /// reconnect on nearly every call.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddTeamSpeak(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<TeamSpeakMcpOptions>(configuration.GetSection(TeamSpeakMcpOptions.SectionName));

        services.AddSingleton(provider => Options(provider).BuildRegistry());
        services.AddSingleton(provider => Options(provider).BuildSafetyPolicy());
        services.AddSingleton(provider => new QueryConnectionManager(provider.GetRequiredService<ProfileRegistry>()));
        services.AddSingleton(provider => new QueryEventHub(provider.GetRequiredService<ProfileRegistry>(), Options(provider).EventBufferSize));
        services.AddSingleton(provider => Options(provider).FileTransfer);
        services.AddSingleton<QueryExecutor>();
        services.AddSingleton<PermissionNameCache>();
        return services;

        static TeamSpeakMcpOptions Options(IServiceProvider provider) =>
            provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<TeamSpeakMcpOptions>>().Value;
    }
}