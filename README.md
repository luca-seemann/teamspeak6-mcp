# teamspeak6-mcp

[![CI](https://github.com/luca-seemann/teamspeak6-mcp/actions/workflows/ci.yml/badge.svg)](https://github.com/luca-seemann/teamspeak6-mcp/actions/workflows/ci.yml)
[![License: AGPL v3](https://img.shields.io/badge/license-AGPL--3.0-blue.svg)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4.svg)](https://dotnet.microsoft.com/download)
[![MCP](https://img.shields.io/badge/MCP-server-orange.svg)](https://modelcontextprotocol.io)

A [Model Context Protocol](https://modelcontextprotocol.io) server for administering
**TeamSpeak 6** servers. It puts the ServerQuery interface behind MCP tools, so an assistant can
read a server, explain it and change it: permissions, channels, groups, bans, stored files and live
events.

> **Status: pre-release, 0.1.0-beta.** Reading, changing, events, file transfer and prompts are
> complete and verified against a live TeamSpeak 6 server, which is itself still in beta.
> [The release](https://github.com/luca-seemann/teamspeak6-mcp/releases/tag/v0.1.0-beta) carries a
> self-contained binary for win-x64, linux-x64 and linux-arm64, with checksums; nothing is published
> on nuget.org, so `dnx` still needs a package you built. See
> [Installing](docs/setup.md#installing), [docs/known-gaps.md](docs/known-gaps.md) for what is not
> verified, and [TODO.md](TODO.md) for what waits on a trigger.

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

A new profile is read-only, so reading works straight away and nothing can be changed until you
raise [the safety level](#safety). [docs/setup.md](docs/setup.md) covers what the TeamSpeak server
needs and how to install this one properly.

## What it looks like

> **Why can't Alice upload a file to Deep Focus?**

The assistant resolves the name to an identity, reads her effective permission in that channel with
every assignment behind it, and answers that her server group grants 50 file upload power while the
channel needs 75, naming the group that decided it and what to change.

> **Tidy up the channel tree, but show me first.**

It reads the tree, proposes a table of renames and moves, and changes nothing until you confirm.

> **Who joined in the last ten minutes?**

It subscribes to server events once, then reads them from a buffer, so this works even though each
tool call is its own request.

## Why this exists

TeamSpeak's permission system is powerful and genuinely hard to reason about: server groups,
channel groups, client permissions and channel-client permissions all overlap, each with skip
and negate flags. Answering "why can't this user upload a file here?" means cross-referencing
several query commands by hand. It is mechanical work, which is what makes it worth handing to a
model.

## How it talks to TeamSpeak

TeamSpeak 6 offers two query interfaces, and the unencrypted raw TCP query of TeamSpeak 3 is not
among them:

| Interface | Default port | Enable with | Events | File transfer |
|---|---|---|---|---|
| SSH query | 10022 | `--query-ssh-enable` / `TSSERVER_QUERY_SSH_ENABLED` | yes | yes |
| HTTP WebQuery | 10080 | `--query-http-enable` / `TSSERVER_QUERY_HTTP_ENABLED` | no | no |
| HTTPS WebQuery | 10443 | `--query-https-enable` / `TSSERVER_QUERY_HTTPS_ENABLED` | no | no |

Both speak the same command set, so this project models them as two implementations of one
`IQueryTransport` and keeps everything above that line transport-agnostic. SSH is the richer
interface: events and file transfer work only there, and the WebQuery's API keys can only be minted
over it.

The server also throttles hard, and connections cost far more than commands, so each profile keeps
one long-lived connection and paces what it sends. What that is based on, and everything else this
project measured rather than assumed, is in
[docs/teamspeak6-findings.md](docs/teamspeak6-findings.md).

Two deployment details cost people hours, so the setup guide gives each its own section: [which
client address your server actually sees](docs/setup.md#check-which-client-address-your-server-actually-sees)
behind Docker, and [exempting this server from flood
protection](docs/setup.md#exempting-this-server-from-flood-protection).

## Safety

Administration tools can do real damage, so every action is classified as `ReadOnly`, `Write` or
`Destructive`, and a server runs read-only unless you opt in. The check happens here, before
anything reaches TeamSpeak; tools also carry the MCP `readOnlyHint` and `destructiveHint`
annotations so your client can prompt appropriately.

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
reference is classified, and anything unknown needs `Destructive`. Commands that reveal credentials,
such as privilege key lists, temporary passwords and snapshots, are never `ReadOnly`. Commands that
control the shared session (`use`, `login`, `logout`, `quit`, notification registration) are refused
outright. So are two that have left a virtual server unrecoverable: `serverstop`, until the hotfix
TeamSpeak announced names a version, and `serversnapshotdeploy -keepfiles` on anything below
6.0.0-beta13, which fixed it. A WebQuery API key has its own server-side scope as well: a `read` key
keeps a profile read-only even if the configuration would allow more.

## Configuration

Settings come from `appsettings.json` next to the process and from environment variables prefixed
`TSMCP_`, with the environment winning. Keep secrets in the environment. The ones most people set:

| Setting | Environment variable | Default |
|---|---|---|
| `TeamSpeak:Safety` | `TSMCP_TeamSpeak__Safety` | `ReadOnly` |
| `TeamSpeak:Profiles:<name>:Host` | `TSMCP_TeamSpeak__Profiles__<name>__Host` | none |
| `TeamSpeak:Profiles:<name>:Password` | `TSMCP_TeamSpeak__Profiles__<name>__Password` | none, and it is what enables SSH |
| `TeamSpeak:Profiles:<name>:Safety` | `TSMCP_TeamSpeak__Profiles__<name>__Safety` | the global level |
| `TeamSpeak:DisabledToolGroups` | `TSMCP_TeamSpeak__DisabledToolGroups` | none, see [Tool groups](docs/tools.md#tool-groups) |
| `TeamSpeak:FileTransfer:LocalDirectory` | `TSMCP_TeamSpeak__FileTransfer__LocalDirectory` | none, so file tools pass content inline only |

[Every setting](docs/setup.md#every-setting) lists the rest: the WebQuery, host keys, timeouts, the
HTTP endpoint and its bearer token.

**SSH host keys are checked**, as OpenSSH does. The first connection to a server remembers the key it
presents in `TeamSpeak:KnownHostsFile`, and a different key later refuses the connection before the
password is sent, because someone may be answering in the server's place. `ts_profiles_list` shows
the fingerprint each server is trusted with. To leave nothing to the first connection, pin the key
per profile with `HostKeyFingerprint`, for example
`SHA256:ohD8VZEXGWo6Ez8GSEJQ9WpafgLFsOfLOtGGQCQo6Og`. After a server is reinstalled and gets a new
key, remove its line from the file or pin the new one.

Each profile keeps one long-lived connection, opened on the first tool call that needs it. If that
connection breaks, the waiting call fails at once and says that it is unknown whether its command
took effect; the next call reconnects. A connection that goes silent without breaking, such as a dead
network route, is only noticed when the command times out after 30 seconds.

## Tools

85 tools in all, and [docs/tools.md](docs/tools.md) describes every one of them: what it answers,
which safety level it needs, and the MCP annotation it carries. In short:

| Group | What it covers |
|---|---|
| `core` | The configured profiles, this server's own login and permissions, and a command's help page from the server itself. |
| `servers` | Instance and virtual servers: settings, health, snapshots, starting and stopping. |
| `channels` | The channel tree: listing, creating, editing, moving, deleting. |
| `clients` | People online and every identity the server has seen, with messages and custom properties. |
| `groups` | Server and channel groups, and who is in them. |
| `permissions` | Assigned and effective permissions, **why can or can't someone do something**, and changing them. |
| `moderation` | Bans, complaints, privilege keys and the server log. |
| `access` | API keys and query logins. |
| `events` | **What is happening right now**: messages, people moving, channel and server changes, bans. |
| `files` | The file repository of each channel: listing, downloading, uploading, moving, deleting. |
| `raw` | `ts_query_raw`, for any ServerQuery command no tool covers, at that command's safety level. |

43 of them change nothing; the rest are refused until the profile allows them. Whole groups can be
switched off to save the tokens their definitions cost, and results can be returned as
[TOON](docs/tools.md#tool-results-as-toon) instead of JSON. The same data is also available as
[resources](docs/tools.md#resources), and four [prompts](docs/tools.md#prompts) start the jobs this
server was built for: a server audit, explaining someone's permissions, tidying the channel tree and
onboarding a member.

## Setting up

[docs/setup.md](docs/setup.md) is the full guide: preparing the TeamSpeak server, the three ways to
install this one (as a NuGet tool through `dnx`, as a self-contained binary, or as a container over
Streamable HTTP), connecting Claude Code or Claude Desktop, and running it as a service behind a
bearer token.

## Building and testing

```bash
dotnet build
dotnet test
```

The integration suite skips itself unless `TSMCP_TEST_HOST` points at a live server, so
`dotnet test` stays green without one.

## Repository layout

```
src/TeamSpeak.Query    Transport-agnostic ServerQuery client library
src/TeamSpeak.Mcp      The MCP server itself (stdio and Streamable HTTP)
tests/                 Unit tests, an in-memory fake server, and a live-server integration suite
docker/                Container image and a compose file that brings up TeamSpeak alongside it
docs/                  The tool reference, the setup guide, how TeamSpeak 6 really behaves, known gaps
reference/             The ServerQuery command reference, captured from the server itself
```

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

`reference/serverquery-6.0.0-beta13.txt` is the exception: it is TeamSpeak's own documentation,
as the server prints it, and belongs to TeamSpeak Systems GmbH. See
[reference/README.md](reference/README.md).

## Disclaimer

Not affiliated with or endorsed by TeamSpeak Systems GmbH. "TeamSpeak" is a trademark of TeamSpeak
Systems GmbH, used here only to say which software this project talks to.

Built with AI assistance, as the commit history shows. The design, the review and every decision are
the maintainer's, and what these documents claim was measured against a live TeamSpeak 6 server
rather than assumed.
