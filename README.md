# teamspeak6-mcp

A [Model Context Protocol](https://modelcontextprotocol.io) server for administering
**TeamSpeak 6** servers, so an AI assistant can do the tedious parts of server administration:
tidy up channel trees, explain why a user lacks a permission, work through bans and complaints,
manage groups, and watch what is happening on the server.

> **Status: early development.** The scaffolding is in place; the query client and the tools
> are being built out phase by phase. Not yet usable.

## Why this exists

TeamSpeak's permission system is powerful and genuinely hard to reason about: server groups,
channel groups, client permissions and channel-client permissions all overlap, each with skip
and negate flags. Answering "why can't this user upload a file here?" means cross-referencing
several query commands by hand. That is exactly the kind of work a model with the right tools
does well.

## How it talks to TeamSpeak

TeamSpeak 6 removed the unencrypted raw TCP query that TeamSpeak 3 had. What remains is:

| Interface | Default port | Enable with | Events |
|---|---|---|---|
| SSH query | 10022 | `--query-ssh-enable` / `TSSERVER_QUERY_SSH_ENABLED` | yes |
| HTTP WebQuery | 10080 | `--query-http-enable` / `TSSERVER_QUERY_HTTP_ENABLED` | no |
| HTTPS WebQuery | 10443 | `--query-https-enable` / `TSSERVER_QUERY_HTTPS_ENABLED` | no |

Both speak the same command set, so this project models them as two implementations of one
`IQueryTransport` and keeps everything above that line transport-agnostic. The one asymmetry is
events: `servernotifyregister` needs a persistent session and works over SSH only.

## Safety

Administration tools can do real damage, so every tool is classified as **read**, **write** or
**destructive**, and a server runs read-only unless you opt in. Tools also carry the MCP
`readOnlyHint` / `destructiveHint` annotations so your client can prompt appropriately.

## Requirements

- [.NET SDK 10.0.400](https://dotnet.microsoft.com/download) or newer
- A TeamSpeak 6 server with the SSH and/or HTTP query interface enabled

## Building

```bash
dotnet build
dotnet test
```

## Running

```bash
# Local client over stdin/stdout (the default)
dotnet run --project src/TeamSpeak.Mcp

# Remote clients over Streamable HTTP
dotnet run --project src/TeamSpeak.Mcp -- --transport http --url http://127.0.0.1:7801
```

To register it with Claude Code:

```bash
claude mcp add teamspeak -- dotnet run --project /path/to/teamspeak6-mcp/src/TeamSpeak.Mcp
```

## Repository layout

```
src/TeamSpeak.Query    Transport-agnostic ServerQuery client library
src/TeamSpeak.Mcp      The MCP server itself (stdio and Streamable HTTP)
tests/                 Unit tests, an in-memory fake server, and a live-server integration suite
docker/                Container image and a compose file that brings up TeamSpeak alongside it
```

The integration suite skips itself unless `TSMCP_TEST_HOST` points at a live server, so
`dotnet test` stays green without one.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md). Bug reports and pull requests are welcome.

## License

[MIT](LICENSE)

## Disclaimer

Not affiliated with or endorsed by TeamSpeak Systems GmbH.
