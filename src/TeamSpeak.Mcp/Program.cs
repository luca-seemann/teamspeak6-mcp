using TeamSpeak.Mcp.Hosting;

var options = StartupOptions.Parse(args);

switch (options.Transport)
{
    case TransportMode.Http:
        await McpHostFactory.CreateHttpApp(args, options.HttpUrl).RunAsync().ConfigureAwait(false);
        break;

    case TransportMode.Stdio:
    default:
        await McpHostFactory.CreateStdioHost(args).RunAsync().ConfigureAwait(false);
        break;
}