# Tools

What every tool does, what it needs, and how to leave some of them out. The setup guide is in
[setup.md](setup.md), and the [README](../README.md) explains the server itself.

85 tools in all. The reading tools need `ReadOnly`, except `ts_token_list`, which needs `Write`
because privilege keys are live credentials. The changing tools are listed further down with the
level each needs.

## Reading

The list tools return at most `limit` entries, starting at `offset`, and say how many there are in
all, usually as `total`.

| Area | Tools | What they answer |
|---|---|---|
| Setup | `ts_profiles_list`, `ts_whoami` | Which servers are configured, and which login this server uses. |
| Instance | `ts_instance_info`, `ts_vserver_list`, `ts_vserver_info`, `ts_health_report` | Version and totals; virtual servers; slots, packet loss and ping, with findings in plain words. |
| Channels | `ts_channel_list`, `ts_channel_info`, `ts_channel_find` | The channel tree in display order, one channel in full, a channel by name. |
| People online | `ts_client_list`, `ts_client_info`, `ts_client_find` | Who is connected, with groups and away state. |
| Known identities | `ts_clientdb_list`, `ts_clientdb_info`, `ts_clientdb_find`, `ts_client_resolve` | Everyone the server has seen; turn a session id, database id or unique identity into all three. |
| Groups | `ts_servergroup_list`, `ts_servergroup_members`, `ts_channelgroup_list`, `ts_channelgroup_members`, `ts_client_groups` | Groups, their members, and every group one person is in. |
| Permissions | `ts_perm_effective`, `ts_perm_find`, `ts_perm_assigned`, `ts_perm_list`, `ts_perm_self` | **Why can or can't someone do something**; who holds a permission; what one group, channel or client has; and what this server's own session may do. |
| Moderation | `ts_ban_list`, `ts_complaint_list`, `ts_token_list` | Bans with expiry, complaints, unused privilege keys. |
| Access and logs | `ts_apikey_list`, `ts_querylogin_list`, `ts_message_list`, `ts_message_get`, `ts_log_view`, `ts_custom_info`, `ts_custom_search` | API keys and query logins, the query inbox, the server log, custom client properties. |
| Events | `ts_events_subscribe`, `ts_events_poll`, `ts_events_wait`, `ts_events_unsubscribe`, `ts_events_status` | **What is happening right now**: messages, people connecting and moving, channel and server changes, bans. |
| Files | `ts_file_list`, `ts_file_info`, `ts_file_transfers`, `ts_file_download` | What is stored in a channel, one file's size and age, transfers under way, and a file's content. |
| Command reference | `ts_command_help` | How a ServerQuery command works, asked from the connected server itself: usage, permissions, description and an example, always for the version it runs. SSH only; the WebQuery does not serve help. |
| Anything else | `ts_query_raw` | Any other ServerQuery command, at the safety level of that command. |

## Changing

Each tool needs the level of what it actually sends; a tool with several actions checks each
action separately. The MCP annotations follow the specification rather than the safety level: a tool
carries `readOnlyHint` only if it changes nothing, and `destructiveHint` if any of its actions can
delete, lift, revoke or replace something, or needs `Destructive`. So lifting a ban is marked
destructive although it needs only `Write`. Subscribing to events is not read-only either: it changes
what is collected for everyone, and moving the event session into a channel with `textChannelId`
needs `Write`.

| Area | `Write` | `Destructive` |
|---|---|---|
| Virtual servers | `ts_vserver_edit`, `ts_vserver_snapshot_create`, `ts_vserver_power` (start) | `ts_vserver_edit` (default groups), `ts_vserver_create`, `ts_vserver_power` (stop), `ts_vserver_delete`, `ts_vserver_snapshot_deploy`, `ts_instance_edit` |
| Channels | `ts_channel_create`, `ts_channel_edit`, `ts_channel_move` | `ts_channel_delete` |
| People | `ts_client_move`, `ts_client_poke`, `ts_client_edit`, `ts_message_send`, `ts_offline_message` | `ts_client_kick`, `ts_clientdb_delete` |
| Groups | `ts_servergroup_manage`, `ts_channelgroup_manage`, `ts_servergroup_membership` (remove), `ts_client_channelgroup_set` | `ts_servergroup_membership` (add), `ts_servergroup_delete`, `ts_channelgroup_delete` |
| Permissions | `ts_perm_set` (channel, channel group, identity in a channel) | `ts_perm_set` (server group, identity), `ts_perm_reset` |
| Moderation | `ts_ban_delete`, `ts_complaint_delete`, `ts_token_manage` (delete) | `ts_ban_add`, `ts_token_manage` (add) |
| Access and settings | `ts_temp_password` (list, delete), `ts_custom_property`, `ts_log_add` | `ts_temp_password` (add), `ts_apikey_manage`, `ts_querylogin_manage` |
| Files | `ts_file_upload`, `ts_file_manage`, `ts_file_download` (to a local file) | `ts_file_upload` (overwrite), `ts_file_delete` |

Handing out server-wide power needs `Destructive`, even though it can be undone: otherwise a `Write`
profile could make anyone a Server Admin. That covers adding someone to a server group, granting or
revoking a server group's or an identity's permissions (removing a needed power escalates as surely as
a grant), changing a default group with `ts_vserver_edit`, and, through `ts_query_raw`, copying a group
over an existing one or an upload that overwrites a stored file.

Every tool that deletes something for good requires `confirmName`, the current name of what it
deletes, read from the server. A mistyped or mixed-up id then refuses instead of deleting the wrong
thing:

| Tool | `confirmName` is |
|---|---|
| `ts_vserver_delete`, `ts_vserver_snapshot_deploy`, `ts_perm_reset` | the virtual server's name |
| `ts_channel_delete` | the channel's name |
| `ts_servergroup_delete`, `ts_channelgroup_delete` | the group's name |
| `ts_clientdb_delete` | the identity's last nickname |
| `ts_file_delete` | the channel's name, or the virtual server's for channel 0 |
| `ts_file_upload` with `overwrite` | the existing file's name, when there is one |
| `ts_querylogin_manage` delete | the login name |
| `ts_apikey_manage` delete | the owner's nickname, or this login's name for its own keys |

`ts_query_raw` asks for the same name when it sends one of these commands, so it is no way around the
confirmation. Deleting bans, complaints, offline messages, custom properties and temporary passwords
needs no name. `ts_vserver_create` needs `Destructive` because the key it returns grants full control
of the new server.

A snapshot holds a virtual server's whole configuration and is usually larger than a tool answer
should be, so `ts_vserver_snapshot_create` saves it with `localPath` inside
`TeamSpeak:FileTransfer:LocalDirectory`, and `ts_vserver_snapshot_deploy` reads it back from there.
Without `localPath`, a snapshot comes back inline only up to `MaxInlineBytes`.

**Stopping a virtual server is refused for now.** On TeamSpeak 6.0.0-beta13 a `serverstop` often
never finishes: the virtual server stays `shutting down`, `use` on it answers `1035`, `serverstart`
answers `2816`, and only restarting the whole TeamSpeak process brings it back. It hung in five of
seven measured attempts, with and without file transfers pending, and TeamSpeak
[confirmed the bug](https://community.teamspeak.com/t/serverstop-never-completes-when-a-file-transfer-is-pending-and-the-virtual-server-can-never-be-stopped-again/65376)
on 19 September 2026, announcing a hotfix but not yet the version that carries it.

Until a version is known that survives a stop, `ts_vserver_power stop` refuses on every version, and
so does `ts_query_raw serverstop`. Two things still work and cover most reasons to stop one: a
snapshot deploy restarts a virtual server from the inside, and stopping the whole instance takes its
virtual servers with it. When the hotfix names its version, the refusal narrows to the releases
below it, the way the `-keepfiles` one already does.

A snapshot deploy restarts the virtual server and gives every channel, group and client database id
a new number, so ids read before it are stale. It also drops the files stored in channels unless
`keepFiles` is set, which was measured on 6.0.0-beta13: the files were still in their channel, under
its new id, after a deploy with the option, and gone after the same snapshot was deployed without it. That option is allowed only against servers from **6.0.0-beta13** on, where it
was measured to be harmless: on 6.0.0-beta12.1 it crashed the server, and the virtual server could
not be started, selected or deleted afterwards, until its database was wiped. Below that version —
and against a server that does not answer `version` — it is refused on every path, `ts_query_raw`
included. The check costs one `version` command before the deploy.

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

A profile can also reach a server without credentials, as the ServerQuery guest that TeamSpeak
6.0.0-beta13 added; `ts_profiles_list` marks those with `guest`. Such a profile still has a safety
level, but what it may actually do is decided on the TeamSpeak side by the `Guest Server Query`
group, which by default grants almost nothing: expect *insufficient client permissions* (`2568`) or
*out of scope* (`5120`) from nearly every tool, and read those as the server's answer rather than
this server's refusal. [setup.md](setup.md#without-credentials-as-the-serverquery-guest) explains how
to configure one.

`ts_perm_effective` shows, for one client in one channel, the value the client ends up with and
every assignment behind it, marking the one that decided. It also shows when channel values did not
count: a skip flag keeps both channel layers out, and `b_client_skip_channelgroup_permissions`, which
Server Admin holds by default, keeps out the channel group.

`ts_perm_self` answers the same question about this server's own query session, which is the one
`ts_perm_effective` cannot reach. Use it after a call came back as *insufficient client permissions*
(`2568`), or before trying something that might be refused: it names the login, the server groups it
holds and the value it has for each permission asked about, where a permission it holds nowhere
comes back as `granted: false`. Rather than naming permissions, `command` looks up what a
ServerQuery command requires, read from the server's own help — so `command: "serverstop"` answers
"may this session stop a virtual server?" without sending `serverstop`. That lookup needs an SSH
profile, because the WebQuery serves no help. A guest profile has no account, so it holds no groups.

## Events

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

## Files

Every channel has a file repository, and channel 0 holds the virtual server's icons and avatars.
`ts_file_list` shows one directory of it, and `ts_file_download` and `ts_file_upload` move files in
and out. `ts_file_manage` creates directories, renames or moves files between channels, and stops
a transfer. `ts_file_delete` removes files, and a directory together with everything in it.

The content comes in one of two ways:
- **Inline**, up to `MaxInlineBytes` (32 KiB by default). A download comes back as text when it is
  valid UTF-8 and as base64 otherwise. An upload takes `content` or `contentBase64`. Inline content
  lands in the model's context, which is why the default is small.
- **As a local file**, through `localPath`, only once `TeamSpeak:FileTransfer:LocalDirectory` is set
  to an absolute path that exists and is not the root of a drive. Every local path is resolved inside
  that directory, and a path leading outside it is refused. So is a path through a symbolic link or
  junction inside it. The model chooses these paths, and over Streamable HTTP it does so from another
  machine. A download never replaces an existing local file, and saves at most `MaxLocalBytes`
  (1 GiB by default), since whoever uploaded the file decides how large it is.
- **The opened file is checked, not only its path.** After opening, the file the operating system
  actually opened must lie inside the directory and have no second name. So a link planted between
  the check and the open, a `.partial` file that is really a link, and a hard link to a file elsewhere
  are all refused before a byte is read or written.

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

## Resources

The same data is available as resources, for clients that attach context instead of calling tools:
`ts://profiles`, `ts://{profile}/permissions`, and `ts://{profile}/{virtualServerId}/` followed by
`info`, `channels`, `clients` or `groups`.

## Prompts

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

## Tool groups

A client sends every tool definition to the model with each request: about 34,000 tokens for all 85.
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

## Tool results as TOON

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

