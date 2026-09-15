# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- Project scaffolding: solution layout, central package management, CI on GitHub Actions,
  container images, and the contribution documents.
- `IQueryTransport`, the transport-agnostic contract both the SSH and the WebQuery client implement.
- `FakeQueryTransport`, an in-memory stand-in used to test without a live TeamSpeak server.
- The protocol core: value escaping, a parser for each transport, the command serialiser, and the
  status codes that drive control flow.
- A captured ServerQuery command reference for 6.0.0-beta12.1, plus raw response fixtures from both
  transports that the parser tests run against.
- `QueryRecord`, giving typed access to response fields and decoding TeamSpeak's conventions in one
  place: booleans as `1`/`0`, times as Unix seconds, and a zero timestamp meaning never.
- `SshQueryTransport` and `HttpQueryTransport`, both verified against a live server, with virtual
  server selection behind one method despite the two interfaces expressing it very differently.
- `FloodGuard` and `ConnectionThrottle`, which pace commands and connections so the server does not
  block the client, plus `ReconnectPolicy` for backoff with jitter.
- Connection profiles bound from configuration, with `TSMCP_`-prefixed environment variables
  overriding the file so credentials need not be written to disk.
- An integration suite that runs against a live server when `TSMCP_TEST_HOST` is set and skips
  itself otherwise.
- `QueryConnectionManager`, which opens one connection per profile on first use, shares it with
  every caller, and resolves `PreferredTransport.Auto` to SSH whenever a password is configured.
- Safety levels per profile, overriding the global `TeamSpeak:Safety` in either direction and
  enforced on the server before any command is sent.
- The first ten tools: `ts_profiles_list`, `ts_whoami`, `ts_instance_info`, `ts_vserver_list`,
  `ts_vserver_info`, `ts_channel_list`, `ts_channel_info`, `ts_client_list`, `ts_client_info` and
  `ts_query_raw`, with structured output and MCP annotations.
- A command catalog giving `ts_query_raw` the safety level of each of the 143 reference commands,
  and a snapshot test that pins the tool schemas.
- The full read surface, 26 further tools. `ts_perm_effective` explains a client's effective
  permissions in a channel layer by layer. `ts_client_resolve` connects session id, database id and
  unique identity. `ts_health_report` turns slot usage, packet loss and ping into findings. Tools for
  known identities, groups, assigned permissions, bans, complaints, privilege keys, API keys, query
  logins, offline messages, custom properties and the server log complete the set.
- MCP resources for profiles, the permission catalog, and each virtual server's info, channels,
  clients and groups.
- The write and destructive surface, 35 tools, for 71 in all. The tools cover virtual servers and
  instance settings, channels, moving, kicking, poking and messaging people, groups and memberships,
  granting and revoking permissions, bans, privilege keys, temporary passwords, custom properties,
  API keys, query logins and log entries. Each action takes its safety level from the command
  catalog. Deleting a virtual server, deploying a snapshot and resetting permissions also require the
  virtual server's exact name. Channel messages move the query session into the channel and back as
  one uninterrupted sequence on the session, over SSH and the WebQuery alike.
- `IQueryTransport.RunExclusiveAsync`, which runs several commands without any other caller's
  command in between, and `HoldsSession`, which says whether the interface has a session to protect.
- A readable explanation for `1541 invalid parameter size`, which the server returns for names that
  are too long.
- Events: `ts_events_subscribe`, `ts_events_poll`, `ts_events_wait`, `ts_events_unsubscribe` and
  `ts_events_status`, for 76 tools in all.
  - Each subscribed virtual server gets a dedicated SSH session, and the events go into a numbered
    buffer per profile, read with a cursor.
  - Categories follow what each registration was measured to deliver on 6.0.0-beta12.1.
  - The buffer size is `TeamSpeak:EventBufferSize`.
- `SshQueryTransport.ConnectAsync` takes a callback that prepares every session it opens, the first
  and each replacement. `QueryEventHub` and `EventBuffer` are usable without MCP.
- Events that carry only a client id, such as a move or a client leaving, are given a
  `client_nickname` from a per-session name cache, seeded from `clientlist` at subscribe and kept
  current from join events, without a query per event.
- `ts_events_subscribe` takes `textChannelId`: it moves the event session into that channel so its
  `textchannel` events are that channel's chat, re-applied after a reconnect.
- File transfer: `ts_file_list`, `ts_file_info`, `ts_file_transfers`, `ts_file_download`,
  `ts_file_upload`, `ts_file_manage` and `ts_file_delete`, for 83 tools in all.
  - Content travels inline (text or base64, up to `TeamSpeak:FileTransfer:MaxInlineBytes`, 100 KiB by
    default), or as a local file inside `TeamSpeak:FileTransfer:LocalDirectory`. Local files are off
    until that is set, and saving a download to one needs `Write`.
  - An upload's stored size is compared with what was sent, so a transfer that broke off is reported
    rather than taken for success. Its partial file is kept, and `resume=true` sends only the missing
    bytes, after checking that the bytes stored last match the content. Resuming needs `Destructive`,
    because the server extends a finished file just the same.
  - A download to a local file goes through `<localPath>.partial`, which a broken download leaves for
    `resume=true` to continue.
  - Local paths through a symbolic link or junction are refused. `LocalDirectory` must be absolute,
    must exist, and cannot be a drive or file system root.
  - `ts_file_manage` creates directories, renames or moves files into another channel, and stops a
    transfer, optionally removing its partial file.
- `FileTransferClient` and `FileTransferTicket` in `TeamSpeak.Query`: the raw byte transfer over the
  file transfer port, usable without MCP. The tickets turn a refusal the server reports inside the
  record, with `error id=0`, into an exception.
- `QueryRecord.GetUnixTimeOfAnyPrecision`, because `ftgetfilelist` writes file times in milliseconds
  and `ftgetfileinfo` in nanoseconds.
- Explanations for the status codes `781`, `2048`, `2050`, `2051`, `2054`, `2056` and `2568`. For a file command over the
  WebQuery, `5120` is now explained as a limit of the WebQuery rather than of the key's scope.
- Prompts: `server-audit`, `explain-user-permissions`, `cleanup-channel-tree` and
  `onboard-new-member`. The audit and the permission explanation are read-only; the cleanup and the
  onboarding present a plan and wait for confirmation before changing anything.
- Packaging.
  - `dotnet publish -r <rid>` produces one self-contained, ReadyToRun-compiled executable for win-x64,
    linux-x64 or linux-arm64, with nothing beside it.
  - `dotnet pack` produces the `TeamSpeak6.Mcp` tool package, of type `McpServer` and with
    `.mcp/server.json`, for `dnx`.
  - The CI workflow builds the binaries and packages for tags.
- The README explains how to prepare the TeamSpeak server and how to run this server through `dnx`,
  as a binary, in a container or from source.
- A live test that breaks an SSH session in the middle of a command through a local relay, once by
  resetting the connection and once by silencing it, and checks that the next command reconnects.
- Parser fixtures for 14 more commands, among them `permoverview`, `permfind`, `servergrouppermlist`,
  `clientdbinfo` and `logview`. Each was captured over SSH and the WebQuery within the same minute,
  and the SSH captures keep the `\n\r` line ending the server really sends. Tests check that both
  transports decode each one to the same records.

### Fixed

- `ts_channel_move` could not reorder a channel within its parent. The server refuses `channelmove`
  to a channel's own parent with `770 already member of channel`, whatever the order, so the tool
  now reads the parent first and sets `channel_order` when it stays the same.
- `ts_file_list` listed channel 0's icons as `/icons/icon_<id>`, a path the server refuses with
  `1538 invalid parameter` for info, download and delete. Listings now give `/icon_<id>`, and the
  file tools also accept the listed form and translate it.
- Asking for the members of a default group passed on TeamSpeak's bare `2564 access to default group
  is forbidden`. The refusal now explains that clients belong to a default group without being added,
  so it has no member list, and where the default groups are shown.
- `docker/docker-compose.yml` configured the MCP container with setting names that do not bind, so
  the container started without a profile.
- After a reconnect, the SSH server's greeting was read as the first lines of the next command's
  response. The greeting is now read and discarded while the session is being opened.
- A command abandoned after it was sent, by a timeout or a cancellation, could have its late answer
  taken for the next command's. Such a session is now replaced before anything else is sent on it,
  and a replaced session's reader stops at once instead of passing on buffered lines.
- A channel message moved the session and sent the message as separate commands. Another call
  could select a different virtual server in between, so the message could land in the wrong
  channel while the tool reported success.
- `gm` was treated as belonging to a virtual server, so a message to every server failed when the
  default virtual server could not be selected.
- The API key commands used whatever virtual server an earlier command had left selected. They now
  address the virtual server given, or the profile's default.
- The SSH transport assumed its virtual server was still selected after stopping or deleting it,
  after a permission reset and after a snapshot deployment.
- Kicking or banning a client did not check whether it was this server's own query session.
- `servercreate` needed only `Write`, although it returns a key with full control of the new server.
  It now needs `Destructive`.
- `ts_perm_set` read the permission catalog before checking the safety level.
- Empty property values and an unreadable server name passed validation.
- `ts_vserver_snapshot_deploy` could deploy with `-keepfiles`. On TeamSpeak 6.0.0-beta12.1 that crashed
  the server, with and without files stored in channels, and left the virtual server impossible to
  start, select or delete. The option is gone from the tool, and `serversnapshotdeploy -keepfiles` is
  refused on every path, `ts_query_raw` included.
- SSH sessions were dropped by the server after about 30 idle seconds, because the keepalive only
  fired after 120, based on a documented 300-second timeout that did not hold on 6.0.0-beta12.1.
  - Event sessions lost their events.
  - The shared tool session reconnected after every longer pause.
  - The keepalive now fires after 15 idle seconds (`KeepAliveSeconds`).
  - It reopens a session found disconnected within seconds.
- Unregistering channel events sent no channel id, which the server refuses with `1539`.
- An event subscription silently stopped delivering after its virtual server was stopped and started,
  and `ts_events_status` still showed it as healthy.
  - The SSH transport now recovers a stale selection: a scoped command refused with `1024 invalid
    serverID` forgets the selection, re-selects and retries once. This also fixed ordinary tool calls
    failing with 1024 right after a virtual server restart.
  - `QueryEventHub` re-registers each subscription on a timer (30 seconds), so a subscription voided
    by a restart comes back on its own.
  - `ts_events_status` and `ts_events_poll` now report each subscription's `lastEventAt`, `lastError`
    and a `healthy` flag, so a stalled subscription is visible.
- A snapshot deploy was given up after the 30-second command timeout. It now waits up to ten minutes,
  and commands can carry their own timeout over SSH and the WebQuery.
- `ts_perm_effective` counted a channel group's value for clients holding
  `b_client_skip_channelgroup_permissions`, such as Server Admin. The server ignores it: 75 talk
  power from Server Admin stays 75 against a channel group granting 62.
- A connection reset in the middle of a command surfaced only after the whole 30-second command
  timeout. The SSH transport now fails the waiting command as soon as SSH.NET reports the session
  lost, and the next command reconnects, in about 4 seconds altogether.
- A session closed without `quit` stayed on the virtual server as a query client for 30 seconds and
  then left with `connection lost`. Disposing a transport now sends `quit` when the session is idle and
  in step, and the session leaves at once.
- `SshQueryTransport` could never reconnect again after a reconnect gave up, and disposing it could
  throw: a failed attempt left a disposed SSH client in place, whose `IsConnected` throws.
- Every refused tool call was logged as an unhandled exception with a full stack trace: a safety level
  too low, a bad argument, a refusal from the server. A call-tool filter now answers these with an error
  result directly, and protocol errors and unexpected exceptions are still logged in full. The message
  the model sees no longer carries the SDK's `An error occurred invoking '…':` prefix.
- One transport failing to close stopped `QueryConnectionManager` and `QueryEventHub` from closing the
  others, and left that event session half-closed. Both now close everything and report the failures
  afterwards.

### Changed (review of phase 6)

- `ts_client_edit` explains that talker status can only be granted to a client lacking the talk
  power its channel needs. For anyone who can already speak, the server refuses it with `1538`.
- Channel messages work over the WebQuery. They had been refused there on the untested assumption
  that the WebQuery keeps no client of its own. Its internal client in fact keeps its channel between
  requests, and its sequences are serialised like SSH's.
- `ts_ban_add` warns that banning a client also bans its IP address, which behind NAT or Docker port
  publishing may be shared by everyone.
- `ts_message_send` takes a `channelPassword` for password-protected channels.
- `ts_apikey_list` and `ts_apikey_manage` take a `virtualServerId`.

### Changed

- The container image carries the self-contained binary on `runtime-deps` instead of a
  framework-dependent build on the ASP.NET image, and builds for linux/amd64 and linux/arm64 without
  emulation.
- `ftinitdownload` needs `ReadOnly` instead of `Write`, through `ts_query_raw` as well. It changes
  nothing on the server: the key it returns opens that one download, and lapses unused.
- The virtual server now travels with each `QueryCommand` instead of being selected on the
  transport. `IQueryTransport.SelectVirtualServerAsync` and `VirtualServerId` are gone; the SSH
  transport sends `use` only when needed, under the same lock as the command, so concurrent callers
  can no longer run on each other's selection.

### Notes

- Several deployment surprises are documented in [README.md](README.md) and
  [reference/README.md](reference/README.md), including which client address the server actually
  sees behind Docker, and why `TESTINGPLATFORM_TELEMETRY_OPTOUT` matters when running the tests.
