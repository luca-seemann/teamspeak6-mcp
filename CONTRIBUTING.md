# Contributing

Thanks for considering a contribution.

## Getting set up

You need the [.NET SDK 10.0.400](https://dotnet.microsoft.com/download) or newer. Everything else
restores on first build.

```bash
git clone <repository-url>
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
itself unless you point it at one:

```bash
export TSMCP_TEST_HOST=127.0.0.1
dotnet test
```

`docker/docker-compose.yml` brings up a TeamSpeak 6 server with the SSH and HTTP query interfaces
enabled, which is the easiest way to get one.

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
