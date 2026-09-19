# teamspeak6-mcp

[![CI](https://github.com/luca-seemann/teamspeak6-mcp/actions/workflows/ci.yml/badge.svg)](https://github.com/luca-seemann/teamspeak6-mcp/actions/workflows/ci.yml)
[![License: AGPL v3](https://img.shields.io/badge/license-AGPL--3.0-blue.svg)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4.svg)](https://dotnet.microsoft.com/download)
[![MCP](https://img.shields.io/badge/MCP-server-orange.svg)](https://modelcontextprotocol.io)

A [Model Context Protocol](https://modelcontextprotocol.io) server for administering
**TeamSpeak 6** servers, so an AI assistant can do the tedious parts of server administration:
tidy up channel trees, explain why a user lacks a permission, work through bans and complaints,
manage groups, and watch what is happening on the server.

> **Status: pre-release, 0.1.0-beta.** Reading, changing, events, file transfer and prompts are
> complete and verified against a live TeamSpeak 6 server, which is itself still in beta. The server
> builds as a NuGet tool package and as self-contained binaries, but nothing is published on
> nuget.org and no release has been tagged yet, so today you build it yourself — see
> [Installing](docs/setup.md#installing).
> [docs/known-gaps.md](docs/known-gaps.md) lists honestly what is not verified, and
> [TODO.md](TODO.md) what waits for a trigger.

## Quick start

With the [.NET SDK 10](https://dotnet.microsoft.com/download), against a TeamSpeak 6 server whose
SSH query is enabled:

```bash
git clone https://github.com/luca-seemann/teamspeak6-mcp.git
cd teamspeak6-mcp

claude mcp add teamspeak \
  -e TSMCP_TeamSpeak__Profiles__home__Host=ts.example.com \
  -e TSMCP_TeamSpeak__Profiles__home__Password='<serveradmin query password>' \
  -- dotnet run --project src/TeamSpeak.Mcp
```

Then ask: *"Which channels are on the server, and who is online?"* — that works straight away,
because a new profile is read-only. Nothing can be changed until you raise
[the safety level](#safety). [docs/setup.md](docs/setup.md) explains what the server needs and how to
install it properly, and [docs/tools.md](docs/tools.md) what each tool does.

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
`IQueryTransport` and keeps everything above that line transport-agnostic.

A few things about them are worth knowing before you set this up, none of which are in TeamSpeak's
documentation — all were measured against a live server, first on 6.0.0-beta12.1 and re-checked on
6.0.0-beta13:

- **Events are SSH-only.** Over the WebQuery, `servernotifyregister` comes back as
  `5120 out of scope — command not in api key scope`. It could hardly work anyway: the WebQuery
  closes every connection, so there is nowhere to deliver a notification.
- **File transfer is SSH-only too.** The WebQuery refuses every `ft*` command with `5120`, even with a
  `manage` key, and the HTTP file transfer the reference mentions (`ftgetchannelfilehttptoken`)
  answers `not implemented`. The bytes themselves travel over the file transfer port, 30033 by
  default, which has to be reachable from wherever this server runs.
- **The WebQuery authenticates with `x-api-key` and nothing else.** HTTP Basic Auth with correct
  `serveradmin` credentials is refused. Keys come from `apikeyadd scope=manage lifetime=0`, which
  you can only run over SSH — so SSH is also the bootstrap path for using the WebQuery at all. From
  6.0.0-beta13 on, a request without the header is not refused either: it is answered as the
  ServerQuery guest, which a profile can use deliberately — `Username=guest`, no credentials, over
  either interface — and which may do only what that server grants guests.
- **The server throttles hard, and connections cost far more than commands.** 160 commands over one
  SSH session at 150 ms spacing were never throttled; bursts with no delay were refused from about
  the fifth command, and five or six connections in quick succession earned an IP-level block that
  took both interfaces down for minutes. A refusal is `524 client is flooding` and states the wait
  it wants; *sending on through it* is what escalates to the block. This server keeps a single
  long-lived connection per profile and paces both commands and connections; see
  [reference/README.md](reference/README.md).

Two deployment details cost people hours, so the setup guide gives each its own section: [which
client address your server actually sees](docs/setup.md#check-which-client-address-your-server-actually-sees)
behind Docker, and [exempting this server from flood
protection](docs/setup.md#exempting-this-server-from-flood-protection).

## Safety

Administration tools can do real damage, so every action is classified as `ReadOnly`, `Write` or
`Destructive`, and a server runs read-only unless you opt in. The check happens on the server before
anything reaches TeamSpeak; tools also carry the MCP `readOnlyHint` / `destructiveHint` annotations so
your client can prompt appropriately.

The level is set globally and can be overridden per profile, in either direction:

```json
{
  "TeamSpeak": {
    "Safety": "ReadOnly",
    "Profiles": {
      "prod":    { "Host": "ts.example.com" },
      "staging": { "Host": "staging.example.com", "Safety": "Destructive" }
    }
  }
}
```

`ts_query_raw` takes the level of the command it sends. Every one of the 143 commands in the captured
reference is classified; anything unknown needs `Destructive`. Commands that reveal credentials — privilege key
lists, temporary passwords, snapshots — are never `ReadOnly`, and commands that control the shared
session (`use`, `login`, `logout`, `quit`, notification registration) are refused outright.

A WebQuery API key has its own server-side scope (`read`, `write` or `manage`). A `read` key keeps a
profile read-only even if the configuration would allow more.

## Configuration

Settings are read from `appsettings.json` next to the process and from environment variables
prefixed `TSMCP_`, with the environment winning. Keep secrets in the environment:

| Setting | Environment variable | Default |
|---|---|---|
| `TeamSpeak:Safety` | `TSMCP_TeamSpeak__Safety` | `ReadOnly` |
| `TeamSpeak:EventBufferSize` | `TSMCP_TeamSpeak__EventBufferSize` | `1000` events per profile |
| `TeamSpeak:DisabledToolGroups` | `TSMCP_TeamSpeak__DisabledToolGroups` | — (every group; comma-separated, see [Tool groups](docs/tools.md#tool-groups)) |
| `TeamSpeak:ToolResultText` | `TSMCP_TeamSpeak__ToolResultText` | `Json` (typed results); `Toon` returns text only, as TOON where shorter, see [Tool results as TOON](docs/tools.md#tool-results-as-toon) |
| `TeamSpeak:FileTransfer:LocalDirectory` | `TSMCP_TeamSpeak__FileTransfer__LocalDirectory` | — (file tools pass content inline only) |
| `TeamSpeak:FileTransfer:MaxInlineBytes` | `TSMCP_TeamSpeak__FileTransfer__MaxInlineBytes` | `32768` (32 KiB, about 10,000 tokens) |
| `TeamSpeak:FileTransfer:MaxLocalBytes` | `TSMCP_TeamSpeak__FileTransfer__MaxLocalBytes` | `1073741824` (1 GiB); `0` for no limit |
| `TeamSpeak:Http:BearerToken` | `TSMCP_TeamSpeak__Http__BearerToken` | — (Streamable HTTP only; required when bound to a non-loopback address) |
| `TeamSpeak:Http:AllowedOrigins:<n>` | `TSMCP_TeamSpeak__Http__AllowedOrigins__<n>` | — (only loopback origins) |
| `TeamSpeak:Http:AllowedHosts:<n>` | `TSMCP_TeamSpeak__Http__AllowedHosts__<n>` | — (only loopback host names; checked when no token is set) |
| `TeamSpeak:KnownHostsFile` | `TSMCP_TeamSpeak__KnownHostsFile` | `teamspeak6-mcp/known_hosts` in `%LOCALAPPDATA%` (Windows) or `~/.local/share` (Linux) |
| `TeamSpeak:Profiles:<name>:Host` | `TSMCP_TeamSpeak__Profiles__<name>__Host` | — |
| `TeamSpeak:Profiles:<name>:Username` | `TSMCP_TeamSpeak__Profiles__<name>__Username` | `serveradmin`; `guest`, with no credentials, connects as the ServerQuery guest (6.0.0-beta13 and above) |
| `TeamSpeak:Profiles:<name>:Password` | `TSMCP_TeamSpeak__Profiles__<name>__Password` | — (enables SSH) |
| `TeamSpeak:Profiles:<name>:HostKeyFingerprint` | `TSMCP_TeamSpeak__Profiles__<name>__HostKeyFingerprint` | — (the first key seen is remembered) |
| `TeamSpeak:Profiles:<name>:SshPort` | `TSMCP_TeamSpeak__Profiles__<name>__SshPort` | `10022` |
| `TeamSpeak:Profiles:<name>:WebQueryUrl` | `TSMCP_TeamSpeak__Profiles__<name>__WebQueryUrl` | — |
| `TeamSpeak:Profiles:<name>:ApiKey` | `TSMCP_TeamSpeak__Profiles__<name>__ApiKey` | — (enables the WebQuery) |
| `TeamSpeak:Profiles:<name>:Transport` | `TSMCP_TeamSpeak__Profiles__<name>__Transport` | `Auto` (SSH when a password is set) |
| `TeamSpeak:Profiles:<name>:DefaultVirtualServerId` | `TSMCP_TeamSpeak__Profiles__<name>__DefaultVirtualServerId` | `1` |
| `TeamSpeak:Profiles:<name>:Safety` | `TSMCP_TeamSpeak__Profiles__<name>__Safety` | the global level |
| `TeamSpeak:Profiles:<name>:KeepAliveSeconds` | `TSMCP_TeamSpeak__Profiles__<name>__KeepAliveSeconds` | `15`; keep it below the server's idle timeout, about 30 seconds on 6.0.0-beta12.1 and beta13 |

**SSH host keys are checked**, as OpenSSH does. The first connection to a server remembers the key it
presents in `TeamSpeak:KnownHostsFile`, and a different key later refuses the connection before the
password is sent: someone may be answering in the server's place. `ts_profiles_list` shows the
fingerprint each server is trusted with. To leave nothing to the first connection, pin the key per
profile with `HostKeyFingerprint`, for example `SHA256:ohD8VZEXGWo6Ez8GSEJQ9WpafgLFsOfLOtGGQCQo6Og`.
After a server is reinstalled and gets a new key, remove its line from the file or pin the new key.

Each profile keeps one long-lived connection, opened on the first tool call that needs it. If that
connection breaks, the tool call waiting on it fails at once and says that it is unknown whether its
command took effect; the next call reconnects. A connection that goes silent without breaking, such as
a dead network route, is only noticed when the command times out after 30 seconds. On shutdown an idle
session ends with `quit`, so the server lets go of it at once instead of holding it for 30 seconds; a
session that is broken, or still waiting for an answer, is simply closed.

## Tools

85 tools in all, and [docs/tools.md](docs/tools.md) describes every one of them: what it answers,
which safety level it needs, and the MCP annotation it carries. In short:

| Group | What it covers |
|---|---|
| `core` | The configured profiles, this server's own login, and a command's help page from the server itself. |
| `servers` | Instance and virtual servers: settings, health, snapshots, starting and stopping. |
| `channels` | The channel tree: listing, creating, editing, moving, deleting. |
| `clients` | People online and every identity the server has seen, with messages and custom properties. |
| `groups` | Server and channel groups, and who is in them. |
| `permissions` | Assigned and effective permissions — **why can or can't someone do something** — and changing them. |
| `moderation` | Bans, complaints, privilege keys and the server log. |
| `access` | API keys and query logins. |
| `events` | **What is happening right now**: messages, people moving, channel and server changes, bans. |
| `files` | The file repository of each channel: listing, downloading, uploading, moving, deleting. |
| `raw` | `ts_query_raw`, for any ServerQuery command no tool covers, at that command's safety level. |

45 of them only read; the rest change something and are refused until the profile allows it. Whole
groups can be switched off to save the tokens their definitions cost, and results can be returned as
[TOON](docs/tools.md#tool-results-as-toon) instead of JSON. The same data is also available as
[resources](docs/tools.md#resources), and four [prompts](docs/tools.md#prompts) start the jobs this
server was built for: a server audit, explaining someone's permissions, tidying the channel tree and
onboarding a member.

## Setting up

[docs/setup.md](docs/setup.md) is the full guide: preparing the TeamSpeak server, the three ways to
install this one — as a NuGet tool through `dnx`, as a self-contained binary, or as a container over
Streamable HTTP — connecting Claude Code or Claude Desktop, and running it as a service behind a
bearer token.

## Building and testing

```bash
dotnet build
dotnet test
```

## Repository layout

```
src/TeamSpeak.Query    Transport-agnostic ServerQuery client library
src/TeamSpeak.Mcp      The MCP server itself (stdio and Streamable HTTP)
tests/                 Unit tests, an in-memory fake server, and a live-server integration suite
docker/                Container image and a compose file that brings up TeamSpeak alongside it
docs/                  The tool reference, the setup guide, how TeamSpeak 6 really behaves, known gaps
reference/             The ServerQuery command reference, captured from the server itself
```

The integration suite skips itself unless `TSMCP_TEST_HOST` points at a live server, so
`dotnet test` stays green without one.

## Contributing

Bug reports and pull requests are welcome. [CONTRIBUTING.md](CONTRIBUTING.md) has the build, the
live-test suite and the house rules; [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md) applies to everyone
taking part. Found a security problem? [SECURITY.md](SECURITY.md) says where it goes, and please do
not open a public issue for it.

Two documents are worth reading before you dig in, because they are what this project actually
learned: [docs/teamspeak6-findings.md](docs/teamspeak6-findings.md) records how TeamSpeak 6 really
behaves where its documentation is silent or wrong, and [docs/known-gaps.md](docs/known-gaps.md)
states plainly what is untested.

## License

Copyright 2026 Luca Seemann. Licensed under the [GNU Affero General Public License, version 3
or later](LICENSE).

`reference/serverquery-6.0.0-beta12.1.txt` is the exception: it is TeamSpeak's own documentation,
as the server prints it, and belongs to TeamSpeak Systems GmbH. See
[reference/README.md](reference/README.md).

## Disclaimer

Not affiliated with or endorsed by TeamSpeak Systems GmbH. "TeamSpeak" is a trademark of TeamSpeak
Systems GmbH, used here only to say which software this project talks to.

Built with AI assistance, as the commit history shows. The design, the review and every decision are
the maintainer's, and what these documents claim was measured against a live TeamSpeak 6 server
rather than assumed.
