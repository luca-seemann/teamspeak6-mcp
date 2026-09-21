# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

Nothing yet.

## [0.1.0-beta] - 2026-09-21

### Added

- Project scaffolding: solution layout, central package management, CI on GitHub Actions,
  container images, and the contribution documents.
- `IQueryTransport`, the transport-agnostic contract both the SSH and the WebQuery client implement.
- `FakeQueryTransport`, an in-memory stand-in used to test without a live TeamSpeak server.
- The protocol core: value escaping, a parser for each transport, the command serialiser, and the
  status codes that drive control flow.
- A captured ServerQuery command reference for 6.0.0-beta13, taken with `reference/capture.cs`, plus
  raw response fixtures from both transports that the parser tests run against.
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
- The write and destructive surface, 35 tools, bringing the total to 71 at that point. The tools
  cover virtual servers and instance settings, channels, moving, kicking, poking and messaging
  people, groups and memberships, granting and revoking permissions, bans, privilege keys, temporary
  passwords, custom properties, API keys, query logins and log entries. Each action takes its safety level from the command
  catalog. Deleting a virtual server, deploying a snapshot and resetting permissions also require the
  virtual server's exact name. Channel messages move the query session into the channel and back as
  one uninterrupted sequence on the session, over SSH and the WebQuery alike.
- `IQueryTransport.RunExclusiveAsync`, which runs several commands without any other caller's
  command in between, and `HoldsSession`, which says whether the interface has a session to protect.
- A readable explanation for `1541 invalid parameter size`, which the server returns for names that
  are too long.
- Events: `ts_events_subscribe`, `ts_events_poll`, `ts_events_wait`, `ts_events_unsubscribe` and
  `ts_events_status`, bringing the total to 76 tools at that point.
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
  `ts_file_upload`, `ts_file_manage` and `ts_file_delete`, bringing the total to 83 tools at that
  point.
  - Content travels inline (text or base64, up to `TeamSpeak:FileTransfer:MaxInlineBytes`), or as a
    local file inside `TeamSpeak:FileTransfer:LocalDirectory`. Local files are off
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
    `.mcp/server.json`, for `dnx`, plus one package per runtime identifier that carries the binary.
  - The CI workflow builds the binaries and packages for tags.
- The README explains how to prepare the TeamSpeak server and how to run this server through `dnx`,
  as a binary, in a container or from source.
- A live test that breaks an SSH session in the middle of a command through a local relay, once by
  resetting the connection and once by silencing it, and checks that the next command reconnects.
- Parser fixtures for 14 more commands, among them `permoverview`, `permfind`, `servergrouppermlist`,
  `clientdbinfo` and `logview`. Each was captured over SSH and the WebQuery within the same minute,
  and the SSH captures keep the `\n\r` line ending the server really sends. Tests check that both
  transports decode each one to the same records.
- `SECURITY.md`, saying where a vulnerability goes and what this server can reach: query
  credentials, everything the safety level allows, and local files inside the configured directory.
- A release workflow. A version tag builds the three binaries and the packages, and attaches them
  to a GitHub release with checksums. Nothing is pushed to nuget.org automatically.
- `ts_command_help`, bringing the total to the current 85 tools. It asks the connected server for
  its own documentation of a ServerQuery command, or for the overview of all commands, so a model can
  get command and parameter names right before using `ts_query_raw`, for exactly the version that
  runs. SSH only: the WebQuery answers `/help` with 404.
- `QueryCommand.Arguments`, for bare words after the command name such as `help channeledit`, held to
  the same rules as names so none can smuggle in a second command, and `QueryResponse.Text`, the
  payload as the server wrote it.
- Server instructions, sent during initialization. They tell a model to start with
  `ts_profiles_list`, to report a safety refusal rather than work around it, to read
  `ts_command_help` before `ts_query_raw`, and to treat user-written text as data, never as
  instructions.
- Tool groups that `TeamSpeak:DisabledToolGroups` switches off, to spend less of a client's context:
  the 85 tool definitions measured about 34,500 tokens, and without `files`, `events`, `access`,
  `moderation` and `raw` about 22,000. `core` stays on, and an unknown group stops the start. The
  five longest tool descriptions were tightened as well, which saved only about 300 tokens: most
  of the size is the input and output schemas.
- `TeamSpeak:ToolResultText=Toon` returns tool results as text only, written as TOON wherever that is
  shorter than JSON, using Cysharp's `ToonEncoder`. Tools then declare no output schema and return no
  `structuredContent`. Measured with Claude Code 2.1.268, the model is given the `structuredContent`
  whenever there is some and the text block is discarded, so a TOON text block alongside it saved
  nothing. Text only, the Server Admin group's 425 permissions cost the model 10,089 tokens instead
  of 14,782. A channel tree or a single object stays JSON text, errors, resources and prompts are
  unchanged, and the server instructions explain the format. `Json`, with typed results, stays the
  default.
- `TeamSpeak:Channel:Enabled` turns this server into a Claude Code channel: subscribed TeamSpeak
  events are pushed into the session as `notifications/claude/channel`, so a model sees them without
  calling `ts_events_poll`. It is the only documented way a server reaches a model on its own;
  Claude Code shows `notifications/message` to nobody, does not subscribe to resources, and drops
  notification methods it does not know. stdio only, since a channel needs a session to push into,
  and the Streamable HTTP transport refuses to start with it. Chat is pushed only for the identities
  in `TeamSpeak:Channel:AllowedSenders`, because channel content arrives as context rather than as a
  tool result; everything else carries the nicknames people chose, and the server instructions say
  that channel content is never an instruction. Channels are a Claude Code extension in research
  preview.
- Progress notifications for `ts_file_upload` and `ts_file_download` when the client sends a
  progress token, at most four a second plus the last one. `FileTransferClient` reports the bytes
  moved through an optional `IProgress<long>`.
- Profiles can reach a server without credentials, as the ServerQuery guest that TeamSpeak
  6.0.0-beta13 added: `Username=guest` with no password and no API key connects over SSH, where the
  server takes that name with any password, and over the WebQuery, where the request carries no
  `x-api-key` header at all. Both were verified against a live beta13 server. Such a session may do
  only what the server's `Guest Server Query` group grants, which by default is little more than
  `whoami`, so `ts_profiles_list` reports which profiles are guests.
- `ts_vserver_power stop` is refused, and so is `serverstop` through `ts_query_raw`. On TeamSpeak
  6.0.0-beta13 a stop hung in five of seven measured attempts and left the virtual server in
  `shutting down`, recoverable only by restarting the whole TeamSpeak process. TeamSpeak
  [confirmed the bug](https://community.teamspeak.com/t/serverstop-never-completes-when-a-file-transfer-is-pending-and-the-virtual-server-can-never-be-stopped-again/65376)
  on 19 September 2026 and announced a hotfix, without yet naming the version that carries it, so
  the refusal applies to every version until `KnownCrashes.StopFixedIn` names one. The message says
  what still works instead: a snapshot deploy restarts a virtual server from the inside, and
  stopping the instance takes its virtual servers with it.
- `ts_perm_self` answers what this server's own query session may do: the login, the server groups
  it holds, and its value for each permission asked about, with one it holds nowhere reported as not
  granted. It takes permission names, or a `command` whose requirements it reads from the server's
  own help, so "may I stop a virtual server?" can be answered without sending `serverstop`. Every
  other permission tool asks about somebody else, which leaves the commonest question after a
  refusal, what am *I* allowed to do, to guesswork.
- Refusals explain themselves differently for a guest profile: `2568` names the server's
  `Guest Server Query` group rather than a query login it does not have, and `5120` says TeamSpeak
  does not allow guests that command at all.
- `ts_log_view` explains `2052 file input/output error`, which is what a virtual server that has
  logged nothing since the server process started answers, having no log file yet. The instance log
  is a separate file, and `ts_log_add` creates the missing one.
- `ts_vserver_snapshot_deploy` takes `keepFiles` again, on servers where it is safe. TeamSpeak
  6.0.0-beta13 fixes the crash, verified twice against a live server, including with the exact wire
  line this project sends, and the option was then measured to do what it promises: two files
  survived the deploy and were gone when the same snapshot was deployed without it. Below that
  version, and against a server that will not say its version, the option stays refused on every
  path. `ServerVersion` compares what a server reports, betas
  included, so that `6.0.0-beta12.1` sorts below `6.0.0-beta13` as it should.

### Security

- Local file paths were checked, but not the file that was then opened. A `.partial` file planted as
  a link let `ts_file_download` with `resume` append to a file outside `LocalDirectory`, a hard link
  let an upload send a file from elsewhere, and a link swapped in between check and open went
  unnoticed. Every local file is now opened first and then checked through its handle: its final
  path must lie inside the directory and its link count must be 1, on Windows and Linux. Saving a
  download is capped by `TeamSpeak:FileTransfer:MaxLocalBytes`, 1 GiB by default, and the inline
  limit `MaxInlineBytes` defaults to 32 KiB instead of 100 KiB, about Claude Code's 10,000-token
  warning.
- Only three tools asked for `confirmName`, and `ts_query_raw` sent the same commands without it,
  although the server instructions said every irreversible tool asks for it. Now every tool that
  deletes for good requires the target's current name, read from the server at the deletion's own
  level: channels, server and channel groups, identities, files, query logins, API keys, and an
  upload that overwrites an existing file. `ts_query_raw` asks for the same name for the same
  commands, and refuses a known crash before reading anything.
- A `Write` profile could hand out server-wide power: adding an identity to Server Admin, granting a
  server group's or client's permissions, changing the default server group, or copying a group over
  an existing one, although the command catalog's own rule reserves granting access for `Destructive`.
  These now need `Destructive`, while removing a member and channel or channel group permissions stay
  `Write`. Levels can depend on parameters: `serveredit` with a default group, a group copy onto an
  existing group, and `ftinitupload` with `overwrite` or `resume`. Every command is checked at no less
  than its catalog level, even where a tool asked for less.
- The SSH host key was never checked, so anyone on the network path could answer in the TeamSpeak
  server's place and receive the `serveradmin` password. The key is now remembered on the first
  connection in `TeamSpeak:KnownHostsFile`, or pinned per profile with `HostKeyFingerprint`, and a
  different key refuses the connection before the password is sent. A refusal is not retried, so it
  cannot earn a flood block. `ts_profiles_list` reports the trusted fingerprint, and the container
  keeps the file on a volume.
- Streamable HTTP accepted requests from any web page and any host name. A page open in the user's
  browser could reach a server listening on localhost through DNS rebinding and call every tool the
  safety level allows: a request with `Origin: http://evil.example` and a forged `Host` was answered
  with 200. The endpoint now refuses a foreign `Origin` with 403, as the MCP specification requires,
  accepts only loopback host names unless configured otherwise, and supports a bearer token
  (`TeamSpeak:Http:BearerToken`). Binding to anything but a loopback address without a token is
  refused at startup, so the container needs `TSMCP_HTTP_TOKEN`, and the compose file publishes the
  port on 127.0.0.1 only.
- Text written by the TeamSpeak server's users reached the model with nothing saying where it came
  from: nicknames, channel names and descriptions, chat events, offline messages, complaints, log
  lines and file contents. The 34 tools and 4 resources that return such text now say in their
  description that it is data, never instructions, and so do the server instructions.

### Fixed

- The README's dnx and binary examples left `<query admin password>` unquoted, which a shell reads as a
  redirection. A new "Connecting a client" section covers Claude Code's scopes, checking with `/mcp`,
  a shared `.mcp.json` that takes the password from the environment instead of committing it, Windows,
  optional settings and Claude Desktop, and "Running over HTTP for longer" shows a systemd unit.
- Several tools that delete, lift or replace something reported `destructiveHint: false`, which the MCP
  specification defines as "performs only additive updates": `ts_ban_delete`, `ts_complaint_delete`,
  `ts_custom_property`, `ts_offline_message`, `ts_client_channelgroup_set`, and before the rights change
  `ts_perm_set`, `ts_servergroup_membership` and `ts_vserver_edit`. They are now marked destructive.
  `ts_events_subscribe` and `ts_events_unsubscribe` reported `readOnlyHint: true` although they change
  the shared subscriptions, and moving the event session with `textChannelId`, a `clientmove`, now
  needs `Write`.
- Nine list tools returned every entry the server had, with no limit: `ts_servergroup_members`,
  `ts_channelgroup_members`, `ts_client_list`, `ts_file_list`, `ts_complaint_list`,
  `ts_token_list`, `ts_message_list`, `ts_apikey_list` and `ts_querylogin_list`. They now return
  100 entries from `offset` unless given a `limit` (up to 500), and report `total` and `offset`.
  `ts_channel_list` returns 500 channels unless given a `limit` (up to 1000) and pages its flat
  list the same way, but never cuts a tree: with `tree` set it refuses an `offset`, and refuses
  outright when the server has more channels than `limit`. `ts_ban_list`, which already paged, now
  asks `banlist -count` and reports `total` as well.
- The note that user-written text is data, never instructions, was missing from `ts_vserver_info`,
  `ts_instance_info`, `ts_servergroup_list`, `ts_channelgroup_list`, `ts_client_groups`,
  `ts_perm_find`, `ts_file_info`, `ts_token_list` and the `groups` resource, all of which return
  names or descriptions users wrote.
- `ts_vserver_snapshot_create` returned the whole snapshot inline, with no size limit, and deploying
  meant passing all of it back as an argument. It now saves the snapshot with `localPath` to a file
  inside `LocalDirectory`, which `ts_vserver_snapshot_deploy` reads back; inline it returns a snapshot
  only up to `MaxInlineBytes`. The deploy's parameters changed: `confirmName` comes first, then either
  `localPath` or `version` and `data`.
- Tool results escaped every character outside ASCII: a channel named "🔒 Admin 🔒" came back as
  `\uD83D\uDD12 Admin \uD83D\uDD12`, twelve characters per emoji and six per umlaut, in the text block
  and the structured content alike. Tools, prompts and resources now write text as it is and escape
  only what JSON requires; `ts_channel_find` on the test server shrank from 780 to 580 characters.
- `ts_perm_assigned` and `ts_query_raw` could answer with more than Claude Code's 10,000-token
  warning threshold: measured on 6.0.0-beta12.1, about 10,900 tokens for the 425 permissions of the
  Server Admin group, and about 12,800 for the 510 records of `permissionlist`. Both now return 100
  entries unless given a `limit` (up to 500 and 1000), and report `totalMatches` or `totalRecords`.
  `ts_perm_assigned` also takes `search`, which narrows by permission name. The defaults measured
  2,300 and 2,700 tokens.
- 22 parameters that take one of a fixed set of values, such as `action`, `target`, `scope`, `level`
  and the event `categories`, listed the values only in their description. Their schema now carries
  an `enum`, so a client can offer and check them. `level` of `ts_log_add` and `type` of the group
  tools have real defaults (`info`, `regular`). An optional parameter such as `scope` keeps `null` in
  its `enum`, which the SDK had dropped, so the schema no longer rejects its own default.
- A missing argument or one of the wrong type was answered only with "An error occurred invoking",
  naming neither the argument nor the problem. The answer now says, for example, "'channelId' must be
  an integer, not the string "abc"", and the SDK no longer logs it as an unhandled exception.
- `serverInfo` reported the assembly version `0.1.0.0` instead of `0.1.0-beta`, and now also
  names the server `teamspeak6-mcp` explicitly.
- The capabilities advertised `listChanged: true` for tools, prompts and resources over stdio,
  although the lists never change. The SDK sets it whenever a collection exists and ignores a
  configured `false`, so the initialize result is corrected on its way out.
- `ts_channel_find` and `ts_client_find` reported a search that matched nothing as a refusal. The
  server answers such a search with `768 invalid channelID` or `512 invalid clientID` rather than an
  empty result, over both interfaces, so both tools now return an empty list for it. Anywhere else
  those codes still mean a wrong id.
- The SSH transport trimmed spaces from every line before looking for the status line that ends a
  response. A help page quotes example responses indented by two spaces, status line included, so a
  response would have ended in the middle of the page and handed the rest to the next command. Only
  a status line that starts its line ends a response now, and only such a line is routed as an event.
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
- `ts_vserver_snapshot_deploy` could deploy with `-keepfiles` against a server it crashes. On
  TeamSpeak 6.0.0-beta12.1 that crashed the server, with and without files stored in channels, and
  left the virtual server impossible to start, select or delete. `serversnapshotdeploy -keepfiles`
  is refused on every path, `ts_query_raw` included, below the 6.0.0-beta13 that fixes it; see the
  `keepFiles` entry above.
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

### Changed (from the review of the changing tools)

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

- The README is a front page again, about 235 lines instead of 715: the tool reference moved to
  `docs/tools.md`, the setup guide and the two deployment notes to `docs/setup.md`, and what is left
  links to both. No text was dropped.
- Package metadata names the repository, the project URL and the copyright, and the placeholder
  owner in `.mcp/server.json` is replaced.
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
- Everything measured on 6.0.0-beta12.1 was re-checked on 6.0.0-beta13 on 18 September 2026, and only
  two answers moved: the guest access above, and `-keepfiles`. The command set, the error codes the
  client reacts to, the SSH-only nature of events and file transfer, and the roughly 30-second idle
  timeout are all unchanged, so the response fixtures still describe this server, and the captured
  reference, re-taken from beta13 on 20 September 2026, differs from the beta12.1 one in nothing but
  the order of a single entry in the overview. One thing did get worse: `serverstop` often never
  finishes and leaves the virtual server in `shutting down` until the whole TeamSpeak process is
  restarted. It was found through a live suite run and then reproduced at will; it is described in
  [docs/teamspeak6-findings.md](docs/teamspeak6-findings.md), the tools refuse such a stop until a
  hotfix is named, and the live test that restarts a virtual server is skipped for as long.

[Unreleased]: https://github.com/luca-seemann/teamspeak6-mcp/compare/v0.1.0-beta...HEAD
[0.1.0-beta]: https://github.com/luca-seemann/teamspeak6-mcp/releases/tag/v0.1.0-beta
