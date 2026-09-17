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
> [Installing](#installing). [docs/known-gaps.md](docs/known-gaps.md) lists honestly what is not
> verified, and [TODO.md](TODO.md) what waits for a trigger.

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
[the safety level](#safety), and the sections below explain what the server needs, what each tool
does, and how to install it properly.

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

Three things about them are worth knowing before you set this up, none of which are in TeamSpeak's
documentation — all were measured against a live 6.0.0-beta12.1 server:

- **Events are SSH-only.** Over the WebQuery, `servernotifyregister` comes back as
  `5120 out of scope — command not in api key scope`. It could hardly work anyway: the WebQuery
  closes every connection, so there is nowhere to deliver a notification.
- **File transfer is SSH-only too.** The WebQuery refuses every `ft*` command with `5120`, even with a
  `manage` key, and the HTTP file transfer the reference mentions (`ftgetchannelfilehttptoken`)
  answers `not implemented`. The bytes themselves travel over the file transfer port, 30033 by
  default, which has to be reachable from wherever this server runs.
- **The WebQuery authenticates with `x-api-key` and nothing else.** HTTP Basic Auth with correct
  `serveradmin` credentials is refused. Keys come from `apikeyadd scope=manage lifetime=0`, which
  you can only run over SSH — so SSH is also the bootstrap path for using the WebQuery at all.
- **The server throttles hard, and connections cost far more than commands.** 160 commands over one
  SSH session at 150 ms spacing were never throttled; bursts with no delay were refused from about
  the fifth command, and five or six connections in quick succession earned an IP-level block that
  took both interfaces down for minutes. A refusal is `524 client is flooding` and states the wait
  it wants; *sending on through it* is what escalates to the block. This server keeps a single
  long-lived connection per profile and paces both commands and connections; see
  [reference/README.md](reference/README.md).

### Check which client address your server actually sees

On some Docker setups the TeamSpeak server sees the **bridge gateway address** for every external
client rather than their real addresses. Its own log gives it away:

```
query from 4 172.20.0.1:49196 issued: login with account "serveradmin"
```

That entry was a connection from a client on the LAN, at a completely different address.

It depends on how ports are published. Native Docker on Linux forwards them with iptables DNAT,
which rewrites the destination and leaves the source intact, so real client addresses usually
arrive. Docker Desktop on Windows and macOS routes through a proxy chain into its VM, which
rewrites the source; the same happens on Linux for traffic that goes through the userland proxy.

Where the addresses are rewritten, per-IP allow and deny lists cannot tell anyone apart, so
allow-listing your client's real address silently does nothing — the entry loads and never
matches. Flood accounting is per IP too, so every external client shares one counter and one
impatient script can throttle everybody.

Read the log line above before trusting an allow list. If the address is wrong, either run the
server with `--network host`, or allow-list the bridge network and accept that the exemption then
covers all outside traffic.

### Exempting this server from flood protection

Not required — the client paces itself and works against a stock server — but it makes life easier
where the MCP server and the TeamSpeak server are both yours.

`TSSERVER_QUERY_ALLOW_LIST` names a **file of CIDRs**, not an address. Pointing the variable at an
IP stops the query interfaces from starting at all. Its default is `query_ip_allowlist.txt` in the
server's data directory, shipping with only `127.0.0.1/32` and `::1/128`, so the usual job is to
add a line to that file rather than to set the variable:

```bash
docker exec <container> sh -c \
  "printf '127.0.0.1/32\n::1/128\n172.20.0.0/16\n' > /var/tsserver/query_ip_allowlist.txt"
docker restart <container>
```

Use the address the server actually sees, per the section above — the bridge network where
addresses are rewritten, the real client address where they are not. Confirm it took by looking for
the startup line the server writes:

```
CIDRManager | updated query_ip_allowlist ips: 127.0.0.1/32, ::1/128, 172.20.0.0/16,
```

If that line does not list your address, the allow list is not doing anything.

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
| `TeamSpeak:DisabledToolGroups` | `TSMCP_TeamSpeak__DisabledToolGroups` | — (every group; comma-separated, see [Tool groups](#tool-groups)) |
| `TeamSpeak:ToolResultText` | `TSMCP_TeamSpeak__ToolResultText` | `Json` (typed results); `Toon` returns text only, as TOON where shorter, see [Tool results as TOON](#tool-results-as-toon) |
| `TeamSpeak:FileTransfer:LocalDirectory` | `TSMCP_TeamSpeak__FileTransfer__LocalDirectory` | — (file tools pass content inline only) |
| `TeamSpeak:FileTransfer:MaxInlineBytes` | `TSMCP_TeamSpeak__FileTransfer__MaxInlineBytes` | `102400` (100 KiB) |
| `TeamSpeak:Http:BearerToken` | `TSMCP_TeamSpeak__Http__BearerToken` | — (Streamable HTTP only; required when bound to a non-loopback address) |
| `TeamSpeak:Http:AllowedOrigins:<n>` | `TSMCP_TeamSpeak__Http__AllowedOrigins__<n>` | — (only loopback origins) |
| `TeamSpeak:Http:AllowedHosts:<n>` | `TSMCP_TeamSpeak__Http__AllowedHosts__<n>` | — (only loopback host names; checked when no token is set) |
| `TeamSpeak:KnownHostsFile` | `TSMCP_TeamSpeak__KnownHostsFile` | `teamspeak6-mcp/known_hosts` in `%LOCALAPPDATA%` (Windows) or `~/.local/share` (Linux) |
| `TeamSpeak:Profiles:<name>:Host` | `TSMCP_TeamSpeak__Profiles__<name>__Host` | — |
| `TeamSpeak:Profiles:<name>:Password` | `TSMCP_TeamSpeak__Profiles__<name>__Password` | — (enables SSH) |
| `TeamSpeak:Profiles:<name>:HostKeyFingerprint` | `TSMCP_TeamSpeak__Profiles__<name>__HostKeyFingerprint` | — (the first key seen is remembered) |
| `TeamSpeak:Profiles:<name>:SshPort` | `TSMCP_TeamSpeak__Profiles__<name>__SshPort` | `10022` |
| `TeamSpeak:Profiles:<name>:WebQueryUrl` | `TSMCP_TeamSpeak__Profiles__<name>__WebQueryUrl` | — |
| `TeamSpeak:Profiles:<name>:ApiKey` | `TSMCP_TeamSpeak__Profiles__<name>__ApiKey` | — (enables the WebQuery) |
| `TeamSpeak:Profiles:<name>:Transport` | `TSMCP_TeamSpeak__Profiles__<name>__Transport` | `Auto` (SSH when a password is set) |
| `TeamSpeak:Profiles:<name>:DefaultVirtualServerId` | `TSMCP_TeamSpeak__Profiles__<name>__DefaultVirtualServerId` | `1` |
| `TeamSpeak:Profiles:<name>:Safety` | `TSMCP_TeamSpeak__Profiles__<name>__Safety` | the global level |
| `TeamSpeak:Profiles:<name>:KeepAliveSeconds` | `TSMCP_TeamSpeak__Profiles__<name>__KeepAliveSeconds` | `15`; keep it below the server's idle timeout, about 30 seconds on 6.0.0-beta12.1 |

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

84 tools in all. The reading tools need `ReadOnly`, except `ts_token_list`, which needs `Write`
because privilege keys are live credentials. The changing tools are listed further down with the
level each needs.

### Reading

| Area | Tools | What they answer |
|---|---|---|
| Setup | `ts_profiles_list`, `ts_whoami` | Which servers are configured, and which login this server uses. |
| Instance | `ts_instance_info`, `ts_vserver_list`, `ts_vserver_info`, `ts_health_report` | Version and totals; virtual servers; slots, packet loss and ping, with findings in plain words. |
| Channels | `ts_channel_list`, `ts_channel_info`, `ts_channel_find` | The channel tree in display order, one channel in full, a channel by name. |
| People online | `ts_client_list`, `ts_client_info`, `ts_client_find` | Who is connected, with groups and away state. |
| Known identities | `ts_clientdb_list`, `ts_clientdb_info`, `ts_clientdb_find`, `ts_client_resolve` | Everyone the server has seen; turn a session id, database id or unique identity into all three. |
| Groups | `ts_servergroup_list`, `ts_servergroup_members`, `ts_channelgroup_list`, `ts_channelgroup_members`, `ts_client_groups` | Groups, their members, and every group one person is in. |
| Permissions | `ts_perm_effective`, `ts_perm_find`, `ts_perm_assigned`, `ts_perm_list` | **Why can or can't someone do something**; who holds a permission; what one group, channel or client has. |
| Moderation | `ts_ban_list`, `ts_complaint_list`, `ts_token_list` | Bans with expiry, complaints, unused privilege keys. |
| Access and logs | `ts_apikey_list`, `ts_querylogin_list`, `ts_message_list`, `ts_message_get`, `ts_log_view`, `ts_custom_info`, `ts_custom_search` | API keys and query logins, the query inbox, the server log, custom client properties. |
| Events | `ts_events_subscribe`, `ts_events_poll`, `ts_events_wait`, `ts_events_unsubscribe`, `ts_events_status` | **What is happening right now**: messages, people connecting and moving, channel and server changes, bans. |
| Files | `ts_file_list`, `ts_file_info`, `ts_file_transfers`, `ts_file_download` | What is stored in a channel, one file's size and age, transfers under way, and a file's content. |
| Command reference | `ts_command_help` | How a ServerQuery command works, asked from the connected server itself: usage, permissions, description and an example, always for the version it runs. SSH only; the WebQuery does not serve help. |
| Anything else | `ts_query_raw` | Any other ServerQuery command, at the safety level of that command. |

### Changing

Each tool needs the level of what it actually sends; a tool with several actions checks each
action separately. Tools that can need `Destructive` carry the MCP `destructiveHint`.

| Area | `Write` | `Destructive` |
|---|---|---|
| Virtual servers | `ts_vserver_edit`, `ts_vserver_snapshot_create`, `ts_vserver_power` (start) | `ts_vserver_create`, `ts_vserver_power` (stop), `ts_vserver_delete`, `ts_vserver_snapshot_deploy`, `ts_instance_edit` |
| Channels | `ts_channel_create`, `ts_channel_edit`, `ts_channel_move` | `ts_channel_delete` |
| People | `ts_client_move`, `ts_client_poke`, `ts_client_edit`, `ts_message_send`, `ts_offline_message` | `ts_client_kick`, `ts_clientdb_delete` |
| Groups | `ts_servergroup_manage`, `ts_channelgroup_manage`, `ts_servergroup_membership`, `ts_client_channelgroup_set` | `ts_servergroup_delete`, `ts_channelgroup_delete` |
| Permissions | `ts_perm_set` | `ts_perm_reset` |
| Moderation | `ts_ban_delete`, `ts_complaint_delete`, `ts_token_manage` (delete) | `ts_ban_add`, `ts_token_manage` (add) |
| Access and settings | `ts_temp_password` (list, delete), `ts_custom_property`, `ts_log_add` | `ts_temp_password` (add), `ts_apikey_manage`, `ts_querylogin_manage` |
| Files | `ts_file_upload`, `ts_file_manage`, `ts_file_download` (to a local file) | `ts_file_upload` (overwrite), `ts_file_delete` |

The three actions that cannot be undone and reach a whole virtual server — `ts_vserver_delete`,
`ts_vserver_snapshot_deploy` and `ts_perm_reset` — also require `confirmName`, the virtual server's
exact name. `ts_vserver_create` needs `Destructive` because the key it returns grants full control
of the new server.

A snapshot deploy restarts the virtual server and gives every channel, group and client database id
a new number, so ids read before it are stale. It also drops the files stored in channels. This
server never deploys with `-keepfiles`, not even through `ts_query_raw`. On TeamSpeak 6.0.0-beta12.1
that option crashed the server, and the virtual server could not be started, selected or deleted
afterwards, until its database was wiped.

`ts_client_kick` and `ts_ban_add` refuse to act on this server's own query session, which would
cut off every tool call on the profile. A channel message has to move that session into the
channel and back, so it runs as one uninterrupted sequence; this works over SSH and the WebQuery
alike, because the WebQuery's internal client keeps its channel between requests. Banning a
connected client creates separate rules for its identity, its myTeamSpeak id and its IP address.
Behind NAT or Docker port publishing that address may be shared by everyone, so lift the IP rule if
it is too broad. `ts_client_edit` grants talker status only to a client that lacks the talk power
its channel needs; the server refuses it for anyone who can already speak.

A few commands stay reachable only through `ts_query_raw`, because a dedicated tool
would make them too easy to call: stopping the whole instance (`serverprocessstop`), deleting every
ban or complaint at once, overwriting an existing group with a copy, and the global auto-permissions.

Every tool except `ts_profiles_list` takes an optional `profile`. Tools below the instance level take
a `virtualServerId`, optional except where the wrong server would be costly: `ts_vserver_power` and
`ts_vserver_delete` require it.

`ts_perm_effective` shows, for one client in one channel, the value the client ends up with and
every assignment behind it, marking the one that decided. It also shows when channel values did not
count: a skip flag keeps both channel layers out, and `b_client_skip_channelgroup_permissions`, which
Server Admin holds by default, keeps out the channel group.

### Events

`ts_events_subscribe` starts collecting events from a virtual server:
- `server`: people connecting and disconnecting, server settings changed
- `channel`: channels created, edited or deleted, people moving, connecting and disconnecting
- `textserver`, `textchannel`, `textprivate`: messages to the server, to the channel the event
  session sits in, or to the event session itself
- `bans`: bans added or removed

It returns a cursor. `ts_events_poll` returns what arrived after a cursor, and `ts_events_wait` does
the same but waits up to 60 seconds for something to arrive. Each answer carries the next cursor, so
reading needs no session and works behind the stateless HTTP transport. Every event keeps
TeamSpeak's notification name (for example `notifytextmessage`) and its fields. Events that carry only
a client id, such as a move, are given a `client_nickname` from a cache seeded at subscribe time and
kept up to date from join events; a name it never learned is simply absent.

`textchannel` covers the channel the event session sits in, which is the default channel unless you
pass `textChannelId` to `ts_events_subscribe`. That moves the event session's own query client into
the named channel, where it shows up as a query client, and follows it there after a reconnect.

A few things to know:
- **SSH only.** Events need the SSH query; the WebQuery refuses `servernotifyregister`. A profile
  with a password uses SSH for events even when its tools are set to the WebQuery.
- **Private messages need the event session's id.** `textprivate` covers messages sent to the event
  session's own client, whose `clientId` each subscription reports.
- **A session of its own.** Each subscribed virtual server gets its own query session, so tool calls
  moving the shared session cannot disturb it. That costs one more connection.
- **Shared by everyone.** Subscriptions belong to the profile, not to the MCP client that made them,
  and last until `ts_events_unsubscribe` or a restart. Over stdio each client has its own process, so
  this is invisible; on a shared HTTP server, one client's `ts_events_unsubscribe` stops collection
  for all of them, though reading is independent because each caller keeps its own cursor.
- **The buffer has a limit.** Each profile keeps the last `EventBufferSize` events. A reader that
  falls behind is told how many it missed.
- **A restart of the virtual server is recovered on its own.** Stopping and starting it silently
  drops the registrations; a watchdog re-registers within 30 seconds. `ts_events_status` reports each
  subscription's `lastEventAt`, `lastError` and `healthy` flag, so a stalled one is visible.
- **One instance only.** Subscriptions and buffers live in the process. Several replicas behind a
  load balancer need sticky routing.

### Files

Every channel has a file repository, and channel 0 holds the virtual server's icons and avatars.
`ts_file_list` shows one directory of it, and `ts_file_download` and `ts_file_upload` move files in
and out. `ts_file_manage` creates directories, renames or moves files between channels, and stops
a transfer. `ts_file_delete` removes files, and a directory together with everything in it.

The content comes in one of two ways:
- **Inline**, up to `MaxInlineBytes` (100 KiB by default). A download comes back as text when it is
  valid UTF-8 and as base64 otherwise. An upload takes `content` or `contentBase64`. Inline content
  lands in the model's context, which is why the default is small.
- **As a local file**, through `localPath`, only once `TeamSpeak:FileTransfer:LocalDirectory` is set
  to an absolute path that exists and is not the root of a drive. Every local path is resolved inside
  that directory, and a path leading outside it is refused. So is a path through a symbolic link or
  junction inside it. The model chooses these paths, and over Streamable HTTP it does so from another
  machine. A download never replaces an existing local file.

A few things to know:
- **SSH and port 30033.** The tickets come from the SSH query, and the bytes travel over the file
  transfer port, which must be reachable from this server just as the query port is. Publish it
  alongside the query ports when TeamSpeak runs in a container.
- **Safety levels.** A download returned inline needs `ReadOnly`, because it changes nothing on the
  TeamSpeak server. Saving it to a local file needs `Write`. Uploading needs `Write`. Replacing an
  existing file with `overwrite=true` needs `Destructive`, and so does continuing one with
  `resume=true`: the server cannot tell a partial file from a finished one, and it lengthened a
  finished file when asked to resume it. `ts_file_manage` stop with `deletePartial=true` needs
  `Destructive` too, since the upload it discards may be someone else's.
- **An upload is checked, and can be resumed.** The protocol has no acknowledgement, so after
  sending, the tool compares the size the server stored with the size it sent. A transfer that broke
  off is reported, and its partial file stays. Upload the same content again with `resume=true`: the
  tool first compares the last bytes stored (up to 64 KiB) with the same bytes of the content, refuses
  if they differ, and otherwise sends only the rest. On the test server a resumed file was byte for
  byte identical.
- **A download can be resumed too.** A download to a local file is written to `<localPath>.partial`
  and renamed when complete. If it breaks off, that file stays, and `resume=true` fetches only the
  missing bytes. A `.partial` file the tool did not ask for is never overwritten.
- **Stalls.** The server closed an upload that sent nothing for between 16 and 30 seconds, keeping
  what had arrived. The tools give up after 30 seconds without a byte. A steady 20 KB/s completed.
- **Channel passwords.** The file tools take `channelPassword`. Without it, or with a wrong one, a
  login in the Guest group was refused with `781 invalid channel password`. `serveradmin` is let in
  with any password.

### Resources

The same data is available as resources, for clients that attach context instead of calling tools:
`ts://profiles`, `ts://{profile}/permissions`, and `ts://{profile}/{virtualServerId}/` followed by
`info`, `channels`, `clients` or `groups`.

### Prompts

Four prompts start the jobs this server was built for. Clients offer them by name; Claude Code, for
example, lists them as slash commands such as `/mcp__teamspeak__server-audit`.

| Prompt | Arguments | What it does |
|---|---|---|
| `server-audit` | `profile`, `virtualServerId` | Reviews health, who holds power, bans, complaints, keys and the log, and reports findings by severity. Read-only. |
| `explain-user-permissions` | `client`, `action`, `channel`, `profile`, `virtualServerId` | Traces why someone can or cannot do something through every group and assignment, and suggests the smallest fix. Read-only. |
| `cleanup-channel-tree` | `goal`, `profile`, `virtualServerId` | Proposes a tidier channel tree as a table of changes, and makes only the ones you confirm. |
| `onboard-new-member` | `member`, `role`, `channel`, `profile`, `virtualServerId` | Adds a new member to their server group and channel group, or offers a privilege key if they never connected, asking before every change. |

Only `client`, `action` and `member` are required. The prompts only instruct the model; what it may
actually change is still decided by the safety level.

### Tool groups

A client sends every tool definition to the model with each request: about 34,000 tokens for all 84.
A deployment that never needs some of them can switch whole groups off, for example
`TSMCP_TeamSpeak__DisabledToolGroups=files,events`. An unknown name stops the start with the list of
valid ones.

| Group | Tools | Tokens, measured |
|---|---|---|
| `core` | `ts_profiles_list`, `ts_whoami`, `ts_command_help`; cannot be switched off | ~700 |
| `raw` | `ts_query_raw` | ~600 |
| `servers` | `ts_vserver_*`, `ts_instance_*`, `ts_health_report`, `ts_temp_password` | ~4,400 |
| `channels` | `ts_channel_*` | ~2,600 |
| `groups` | `ts_servergroup_*`, `ts_channelgroup_*`, `ts_client_groups`, `ts_client_channelgroup_set` | ~4,000 |
| `clients` | `ts_client_*`, `ts_clientdb_*`, `ts_message_*`, `ts_offline_message`, `ts_custom_*` | ~7,300 |
| `permissions` | `ts_perm_*` | ~3,000 |
| `moderation` | `ts_ban_*`, `ts_complaint_*`, `ts_token_*`, `ts_log_*` | ~3,600 |
| `access` | `ts_apikey_*`, `ts_querylogin_*` | ~1,500 |
| `events` | `ts_events_*` | ~3,000 |
| `files` | `ts_file_*` | ~3,800 |

Switching a group off is not a safety measure: the safety level decides what may change, and a tool
above it stays listed so the model can say why it was refused. Resources and prompts stay available,
and a prompt may then suggest a tool that is not listed.

### Tool results as TOON

By default every tool result carries its data twice: as `structuredContent`, JSON matching the tool's
output schema, and as a JSON text block. Claude Code gives the model the `structuredContent` whenever
there is some and discards the text block; that was measured, it is not documented.

With `TSMCP_TeamSpeak__ToolResultText=Toon`, results are text only. The tools declare no output
schema and return no `structuredContent`, and the text is written as
[TOON](https://github.com/toon-format/toon) wherever that is shorter than JSON. The server instructions
explain the format. Lists of uniform records become one header and a line per record:

```
permissions[425]{id,name,value,negated,skip}:
  26,b_virtualserver_info_view,1,false,false
```

Measured on the test server, in characters of the text:

| Result | JSON | TOON |
|---|---|---|
| `ts_perm_assigned`, Server Admin, `limit=500` | 43,432 | 26,881 |
| `ts_query_raw permissionlist`, `limit=1000` | 51,083 | 33,766 |
| `ts_servergroup_list` | 1,904 | 602 |
| `ts_channel_list`, `ts_vserver_info` | stays JSON | TOON would be longer |

In Claude Code 2.1.268 (headless, Haiku), the Server Admin permissions cost the model 14,782 tokens as
JSON and 10,089 as TOON. With the default limit of 100 entries the saving is smaller, about 700 tokens
on a large list, and none on small results.

Choose by client:

- **`Json`, the default:** typed results a client can check against the output schema.
- **`Toon`:** fewer tokens for large lists, for a client that only hands text to its model, such as
  Claude Code.

Errors, resources and prompts stay as they are in both modes.

Independent of this setting, results write text as it is. Emoji and umlauts in channel names and
nicknames used to come back as `\u` escapes, twelve characters per emoji and six per umlaut.

## Setting up

### Preparing the TeamSpeak server

1. **Enable the SSH query.** Both query interfaces are off by default. Set
   `TSSERVER_QUERY_SSH_ENABLED=1`, and `TSSERVER_QUERY_HTTP_ENABLED=1` if you also want the
   WebQuery. A port that accepts connections but never greets is the symptom of an interface that is
   published but not enabled.
2. **Know the `serveradmin` password.** Set it with `TSSERVER_QUERY_ADMIN_PASSWORD`; otherwise the
   server generates one on first start and prints it to its log once.
3. **Make the ports reachable** from where this server runs: 10022 for the SSH query, and 30033 for
   file transfer. 10080 only if you use the WebQuery.
4. **Optionally mint a WebQuery key**, over SSH: `apikeyadd scope=manage lifetime=0`. SSH is still
   needed for events and file transfer, so a password is the better choice whenever you have one.
5. **Start read-only.** Leave `TeamSpeak:Safety` at `ReadOnly` until you have seen what the tools do,
   then raise it per profile.

`docker/docker-compose.yml` shows all of this for a TeamSpeak container.

### Installing

There are three ways to run the server, and only building from source needs .NET installed.

#### As a NuGet tool, through `dnx`

`dnx` comes with the .NET 10 SDK. It fetches the package for the machine it runs on and starts it
without installing anything. The package holds the same self-contained binary as below, for
win-x64, linux-x64 and linux-arm64.

The package is not on nuget.org yet. Build it and point `dnx` at the folder:

```bash
dotnet pack src/TeamSpeak.Mcp -c Release -o artifacts/nuget

claude mcp add teamspeak \
  -e TSMCP_TeamSpeak__Profiles__home__Host=ts.example.com \
  -e TSMCP_TeamSpeak__Profiles__home__Password=<query admin password> \
  -- dnx TeamSpeak6.Mcp --version 0.1.0-beta --yes --add-source /path/to/artifacts/nuget
```

#### As a single-file binary

```bash
dotnet publish src/TeamSpeak.Mcp -c Release -r linux-x64 -o publish/linux-x64   # or win-x64, linux-arm64
```

The result is one executable, `teamspeak6-mcp` (`teamspeak6-mcp.exe` on Windows), with the .NET runtime
inside and compiled ahead of time with ReadyToRun. It needs nothing installed.
- **Size:** about 150 MB, and 170 MB for linux-arm64.
- **Start-up:** it answers an MCP client about 0.15 seconds after starting, measured on win-x64.
- **Smaller:** `-p:EnableCompressionInSingleFile=true` brings it to about 70 MB, at about 0.3 seconds to
  the first answer.

Pushing a version tag builds all three binaries and the packages and attaches them to a GitHub
release, with checksums. No release is tagged yet, and that workflow has never run.

```bash
claude mcp add teamspeak \
  -e TSMCP_TeamSpeak__Profiles__home__Host=ts.example.com \
  -e TSMCP_TeamSpeak__Profiles__home__Password=<query admin password> \
  -- /opt/teamspeak6-mcp/teamspeak6-mcp
```

Other clients take the same command and environment in their JSON configuration, for example a
project's `.mcp.json`:

```json
{
  "mcpServers": {
    "teamspeak": {
      "command": "/opt/teamspeak6-mcp/teamspeak6-mcp",
      "env": {
        "TSMCP_TeamSpeak__Profiles__home__Host": "ts.example.com",
        "TSMCP_TeamSpeak__Profiles__home__Password": "<query admin password>"
      }
    }
  }
}
```

#### As a container, over Streamable HTTP

`docker/Dockerfile` builds an image for linux/amd64 and linux/arm64 that serves Streamable HTTP on
port 7801, and `docker/docker-compose.yml` starts it next to a TeamSpeak server. The endpoint is the
root path. Inside a container the server listens on all interfaces, so it needs a bearer token and
refuses to start without one; the compose file publishes the port on 127.0.0.1 only:

```bash
export TSMCP_HTTP_TOKEN=$(openssl rand -hex 32)
docker compose -f docker/docker-compose.yml up -d
claude mcp add --transport http teamspeak http://localhost:7801/ --header "Authorization: Bearer $TSMCP_HTTP_TOKEN"
```

The image has not been built yet; see [TODO.md](TODO.md).

However it is started, the Streamable HTTP endpoint protects itself:
- **A foreign `Origin` is refused with 403.** A web page open in your browser cannot use the server
  through DNS rebinding. Pages on a loopback host, and origins listed in `TeamSpeak:Http:AllowedOrigins`,
  are accepted; MCP clients send no `Origin` at all.
- **Without a token, only loopback addresses and names.** The server binds only to a loopback address
  and accepts only loopback host names, plus `TeamSpeak:Http:AllowedHosts`.
- **With a token, every request must present it** as `Authorization: Bearer <token>`. It must be at
  least 32 characters long.

#### From source

With the [.NET SDK 10.0.400](https://dotnet.microsoft.com/download) or newer:

```bash
dotnet run --project src/TeamSpeak.Mcp                                                     # stdio
dotnet run --project src/TeamSpeak.Mcp -- --transport http --url http://127.0.0.1:7801     # Streamable HTTP
```

On Windows, a start-up failure with `An attempt was made to access a socket in a way forbidden by its
access permissions` (socket error 10013) means the port lies in a range Windows has reserved, often
for Hyper-V or WSL. `netsh interface ipv4 show excludedportrange protocol=tcp` lists the ranges;
pick a port outside them.

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
docs/                  How TeamSpeak 6 really behaves, measured, and the known gaps of this project
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

Not affiliated with or endorsed by TeamSpeak Systems GmbH.
