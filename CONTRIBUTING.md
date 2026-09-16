# Contributing

Thanks for considering a contribution.

## Getting set up

You need the [.NET SDK 10.0.400](https://dotnet.microsoft.com/download) or newer. Everything else
restores on first build.

```bash
git clone https://github.com/luca-seemann/teamspeak6-mcp.git
cd teamspeak6-mcp
dotnet build
dotnet test
```

`dotnet test` runs in Microsoft.Testing.Platform mode (opted into via `global.json`). Two quirks
are worth knowing before you lose an hour to either:

- Any unrecognised flag is forwarded to the test application, so `dotnet test --nologo` fails with
  exit code 5. Pass MSBuild-level options only.
- Set `TESTINGPLATFORM_TELEMETRY_OPTOUT=1`. Without it the test host finishes the tests and then
  sits for another minute or two flushing telemetry, which looks exactly like a hung test. CI sets
  it already.

## Testing against a real server

Most tests use `FakeQueryTransport` and need no TeamSpeak server. The integration suite skips
itself unless you point it at one. Use a disposable server: the suite connects for real and changes
things, removing what it creates.

```bash
export TESTINGPLATFORM_TELEMETRY_OPTOUT=1
export TSMCP_TEST_HOST=<host> TSMCP_TEST_PASSWORD=<query admin password>
export TSMCP_TEST_APIKEY=<from apikeyadd scope=manage lifetime=0 over SSH>   # the WebQuery tests
# export TSMCP_TEST_DISRUPTIVE=1   # also kicks a connected client off the server and leaves it an offline message
dotnet test
```

- Tests that act on a real person skip themselves unless a TeamSpeak client is connected.
- Never kill a test process. Its orphaned session holds a query slot until the server reaps it.
- Run one connecting process at a time. Connections, not commands, earn an IP-level block.

`docker/docker-compose.yml` brings up a TeamSpeak 6 server with the SSH and HTTP query interfaces
enabled, which is the easiest way to get one. [docs/teamspeak6-findings.md](docs/teamspeak6-findings.md)
records how the server really behaves, and [docs/known-gaps.md](docs/known-gaps.md) what the tests do
not cover.

## Changing the tool surface

The tool schemas are pinned by a snapshot test. When a change is intended, run the tests with
`TSMCP_UPDATE_SNAPSHOTS=1` and commit the diff of `tests/TeamSpeak.Mcp.Tests/Tools/Snapshots/tools.json`.

When the code and `reference/serverquery-6.0.0-beta12.1.txt` disagree, the reference wins.

## Where things are

| Component | What it does |
|---|---|
| `TeamSpeak.Query` | Transport-agnostic ServerQuery client. No MCP dependency; usable on its own. |
| ├ `QueryEscaping` | The `\s` `\p` `\/` escape table, both directions. |
| ├ `QueryResponseParser` | Line protocol → `QueryResponse`, framed on the trailing status line. |
| ├ `WebQueryResponseParser` | WebQuery JSON → the *same* `QueryResponse`. |
| ├ `QueryCommandSerializer` | `QueryCommand` → SSH line or WebQuery URL. Rejects names that could smuggle a second command. |
| ├ `QueryCommandScope` | Which commands address the instance rather than one virtual server; shared by both transports. |
| ├ `QueryRecord` | Typed field access, decoding TeamSpeak's `1`/`0` booleans and Unix-second times in one place. |
| ├ `SshQueryTransport` | One long-lived session, event routing, keepalive, reconnect with backoff. Sends `use` only when needed, under the same lock as the command, and runs exclusive sequences. Fails a waiting command as soon as the session is lost, and ends with `quit`. |
| ├ `HttpQueryTransport` | WebQuery over `x-api-key`, no connection pooling. |
| ├ `FloodGuard` | Paces commands; honours the wait the server asks for. |
| ├ `ConnectionThrottle` | Paces connections, which cost far more than commands. |
| ├ `ProfileRegistry` | Named servers, validated at startup. |
| ├ `QueryConnectionManager` | One lazily opened connection per profile, shared by every tool call; resolves `Auto`. |
| ├ `QueryEventHub` | Event subscriptions, each on an SSH session of its own that registers again whenever it is replaced. |
| ├ `EventBuffer` | A numbered ring buffer per profile, read with a cursor, so events work without an MCP session. |
| ├ `FileTransferTicket` | The key and port from `ftinitupload`/`ftinitdownload`; a refusal inside the record becomes an exception. |
| └ `FileTransferClient` | The raw byte transfer over the file transfer port, with a stall timeout and fallback addresses. |
| `TeamSpeak.Mcp` | MCP host on stdio and Streamable HTTP. |
| ├ `SafetyPolicy` | `ReadOnly` / `Write` / `Destructive`, globally and per profile, enforced before anything is sent. |
| ├ `CommandCatalog` | The level every reference command needs through `ts_query_raw`; session commands refused. |
| ├ `QueryExecutor` | The one path every tool takes: profile, safety, shared connection, errors a model can act on. |
| ├ `PermissionNameCache` | The permission catalog per profile, fetched once, so tools show names instead of ids. |
| ├ `PermissionTarget` | The five things permissions attach to, with the list, add and delete command and the flags each takes. |
| ├ `SessionSequence` | An uninterrupted run of commands on the session, for actions that depend on its state: channel messages, and the own-session check before a kick or ban. |
| ├ Tools | 83 tools: 44 reading (5 of them for events, 4 for files), 39 changing including `ts_query_raw`. Schemas and annotations pinned by tests; see the README for the lists. |
| ├ `FileTransferOptions` | The one local directory file tools may use (off unless set) and the inline size limit. |
| ├ Resources | `ts://profiles`, `ts://{profile}/permissions`, and `info`, `channels`, `clients`, `groups` per virtual server. |
| └ Prompts | `server-audit`, `explain-user-permissions`, `cleanup-channel-tree`, `onboard-new-member`: the tools to use in order, and what the model may change. |
| Packaging | `dotnet publish -r <rid>` gives one self-contained, ReadyToRun-compiled executable for win-x64, linux-x64 or linux-arm64. `dotnet pack` gives the `TeamSpeak6.Mcp` tool package, of type `McpServer`, for `dnx`. `docker/Dockerfile` puts the binary on `runtime-deps`. |
| `reference/` | The captured ServerQuery reference — 143 commands in its index, 141 with their own page — and the source of truth for this project. |

## House rules

- **Warnings are errors.** `Directory.Build.props` enables nullable reference types, code style
  enforcement and XML documentation. Public members in `src/` need `///` documentation.
- **Package versions live in `Directory.Packages.props`**, not in individual project files.
- **Keep `TeamSpeak.Query` free of MCP dependencies.** It is a general-purpose ServerQuery client
  and should stay usable on its own.
- **New commands belong behind a transport-agnostic abstraction.** If something works over SSH but
  not over WebQuery, that difference belongs in the transport layer, not in a tool.
- **Classify every tool** as read, write or destructive, and annotate it accordingly. When in
  doubt, classify upward.
- Run `dotnet format` before pushing; CI verifies formatting.

## Commit messages

Write them in English, in the imperative mood, explaining why rather than restating the diff.

## Reporting bugs

Include your TeamSpeak 6 server version, which query interface you used, and the exact command or
tool call. If the server returned an error, include its `error id` and message.
