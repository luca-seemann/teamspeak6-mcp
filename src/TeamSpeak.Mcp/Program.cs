using TeamSpeak.Mcp.Hosting;

var options = StartupOptions.Parse(args);

switch (options.Transport)
{
    case TransportMode.Http:
        Microsoft.AspNetCore.Builder.WebApplication app;
        try
        {
            app = McpHostFactory.CreateHttpApp(args, options.HttpUrl);
        }
        catch (InvalidOperationException ex)
        {
            // Settings that would leave the endpoint open; the message says what to change.
            await Console.Error.WriteLineAsync(ex.Message).ConfigureAwait(false);
            return 1;
        }

        await app.RunAsync().ConfigureAwait(false);
        break;

    case TransportMode.Stdio:
    default:
        await McpHostFactory.CreateStdioHost(args).RunAsync().ConfigureAwait(false);
        break;
}

return 0;