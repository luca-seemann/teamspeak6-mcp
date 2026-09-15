# TeamSpeak 6 ServerQuery: what the documentation does not tell you

Everything below was measured against server **6.0.0-beta12.1**. Each contradicted either the
official documentation or a reasonable assumption, and each cost real time to find.

**There is no public command reference.** The official docs have four query pages and not one of
them lists a command, an endpoint path, or a response shape. The real reference ships inside the
server package. We captured it by running `help` and then `help <command>` for every command in its
index — 143 names, 141 of them with a page of their own (`help` is the overview, `quit` has none);
it lives in `reference/`.

**The WebQuery authenticates with `x-api-key` and nothing else.** The docs say HTTP Basic Auth with
`serveradmin` credentials. It is refused — correct credentials still return `5124 missing apikey`,
while a bad key returns the distinct `5122 invalid apikey`. Two different codes are what proves the
header is the only channel the server reads. Keys come from `apikeyadd`, which runs over SSH only,
so **SSH is the bootstrap path for using the WebQuery at all**.

**The SSH query refuses pseudo-terminals and sends no prompt.** The docs show an interactive session
with a `TS6>` prompt; the greeting line is actually `TS3` and no prompt is ever emitted. The
documented example only works because OpenSSH drops its PTY request when stdin is a pipe. A client
must open the channel without a terminal and frame responses on the trailing `error id=` line.

**Connections cost far more than commands.** 160 commands over one session at 150 ms spacing were
never throttled. Five or six connections in quick succession earned an IP-level block that took
*both* interfaces down for minutes. A command sent too fast gets a polite `524 client is flooding —
please wait 1 seconds`; **sending on through that rejection** is what escalates to the block, and
polling to see whether it has lifted keeps it alive.

**Behind Docker Desktop the server sees the bridge gateway, not your client.** Its own log:

```
query from 4 172.20.0.1:49196 issued: login with account "serveradmin"
```

— for a connection from `192.0.2.80`. Per-IP allow lists therefore cannot match anything, which
is why allow-listing a real client address achieved nothing however often it was tried. Native
Docker on Linux forwards with DNAT and preserves the source, so this is deployment-specific: read
the log line before trusting an allow list.

**Idle query sessions are cut off after about 30 seconds, not 300.** The documentation speaks of
300 seconds, and the transport's keepalive, sending a command after 120 idle seconds, was built on
that number. On the test server, one connection at a time:
- a session survived pauses of 10, 20 and 25 seconds, and was gone after 30
- SSH-level keepalive packets every 10 seconds did not keep it
- `version` every 20 seconds did

This was found only because an event session, which never sends commands of its own, lost its
events. The shared tool session had been reconnecting quietly after every longer pause. The
keepalive now fires after 15 idle seconds (`KeepAliveSeconds`), and a session found disconnected
is reopened at once. Several earlier probe runs, in which every session seemed to be cut off at the
same moment, were this timeout and not a flood block.

**A session that ends without `quit` stays on the server for 30 seconds.** A second session,
registered for server events, watched a query session leave:
- closed without `quit`, it stayed on the virtual server as a query client for 30 seconds, then left
  with `reasonid=3 reasonmsg=connection lost`
- with `quit`, it left at once with `reasonid=8`

The server log read through `logview` records neither. An earlier version of this note said the slot
was freed at once; that was never measured, and it was wrong.

**Events are SSH-only.** Over the WebQuery, `servernotifyregister` returns `5120 out of scope`.

**What each event registration delivers is not documented, so it was measured one category at a
time.**
- `event=server` delivered clients entering and leaving (`notifycliententerview`,
  `notifyclientleftview`) and `notifyserveredited`, which names only who made the change, not what
  changed.
- `event=channel id=0` delivered `notifychannelcreated`, `notifychanneledited` (only the changed
  fields), `notifychanneldeleted`, `notifyclientmoved`, and clients entering and leaving too.
- Text messages come as `notifytextmessage`, whose `targetmode` (3, 2, 1) tells server, channel and
  private apart. Channel messages arrive only for the channel the listening session is in, so
  `ts_events_subscribe` takes `textChannelId` and moves the event session there (verified live: a
  Lobby message arrived only after the session was moved into the Lobby).
- Events with only a client id are enriched with `client_nickname` from a name cache; a name never
  learned is left absent rather than fetched per event.
- TeamSpeak 6 adds `notifybanupdate` for the `bans` category: `op=add` carries the whole ban, `op=del`
  only its id.
- `servernotifyunregister event=channel` without an `id` is refused with `1539 parameter not found`,
  while the other categories unregister without one. A unit test against a fake had passed; only the
  live check found it.

A few other things showed up:
- Notifications can arrive in the middle of a pending response.
- The sender of a private message gets an echo without having registered for anything, so even a
  session that never subscribes receives notify lines.
- A query client that creates a channel is moved into it, and an emptied temporary channel is
  deleted at once by "Server".

**File transfer works only over SSH and raw TCP.** The reference offers `ftgetchannelfilehttptoken` for
HTTP file transfer. On the test server it answers `2 not implemented`, over both interfaces. Over the
WebQuery, every `ft*` command is `5120 out of scope`, even with a `manage` key. So the ticket comes
from the SSH query, and the bytes travel over port 30033. The protocol there is minimal: write the
32-character key, then write or read the raw bytes. There is no acknowledgement. The server closes
the connection when an upload is complete and after the last byte of a download, and closes a
connection with an unknown key at once. Probed in three rounds:
- **Refusals come inside the record.** `ftinitdownload` of a missing file, `ftinitupload overwrite=0`
  onto an existing file, and an upload into a missing directory each return `error id=0`, with
  `status=2051`, `2050` or `2054` in the record.
- **The same timestamp comes in two units.** `datetime` is in milliseconds from `ftgetfilelist` and in
  nanoseconds from `ftgetfileinfo` for the same file. The reference shows seconds.
- **An interrupted upload leaves a partial file.** `ftgetfilelist` lists it with the size so far and
  `incompletesize`. `resume=1` continues from the stored size. `ftstop delete=1` removes the file and
  closes the socket, and `ftlist` shows the transfer with its progress while it runs. An unused ticket
  creates a 0-byte placeholder at once, and it was gone about two minutes later.
- **Deletion is generous.** A directory is deleted with its contents. A multi-name `ftdeletefile`
  deletes every name that exists and still reports `2054` for a missing one.
- **Channel passwords bind ordinary logins only.** For `serveradmin`, a wrong `cpw` was accepted. A
  query login created for an identity in the Guest group was refused without the password, or with a
  wrong one: `781 invalid channel password`, inside the record for `ftinit*`. The same login uploading
  without the right permission got `2568`, whose message names the permission
  (`i_ft_needed_file_upload_power`).
- **Resuming works byte for byte, and on any file.** `resume=1` answers with the stored size as
  `seekpos`, and sending the rest produced a file with an identical hash. Combined with `overwrite=1`
  it is refused with `2056 overwrite excludes resume`. For a file that does not exist it gives `2054`;
  it does not start a fresh upload. It cannot tell a partial file from a finished one: resuming a
  complete 1000-byte file with 2000 bytes of other content extended it to 2000 bytes. When nothing is
  left to send, the server closes the connection at once.
- **A stalled upload is closed after 16 to 30 seconds.** An upload that stopped sending was still
  listed as running after 16 seconds and gone by 30, with its connection reset. What had arrived was
  kept. A download the client stopped reading for 45 seconds completed afterwards, though all of its
  100 KB may already have sat in socket buffers. A steady 20 KB/s upload completed.
- **A real client, watched in `ftlist`.** A 786 MB upload from a TeamSpeak client showed `sender=0`,
  and its download `sender=1`. Both ran at about 130 MB/s on the LAN, and each entry vanished when the
  transfer ended. 80 MB through the probe took 0.6 s up and 0.7 s down.

**An empty parameter value is a missing parameter.** `channelfind pattern=` returns
`1542 missing required parameter`, a different code from the `1539` seen elsewhere, so an empty
string never works as a "match anything" pattern.

**An unknown client id is a conversion error.** `clientinfo clid=99999` returns `1540 convert error`
rather than an "invalid client" status, so a tool cannot tell a mistyped id from a malformed one by
the code alone.

**Permission rows say where a value comes from with `t id1 id2`.** Probed with every kind of
assignment set on one client: `t=0` is a server group in `id1`, `t=1` the client's database id in
`id1`, `t=2` a channel in `id1`, `t=3` a channel group with the channel in `id1` and the group in
`id2`, and `t=4` a client-in-channel permission with the channel in `id1` and the client in `id2`.
`permfind` writes 0 as the channel of a channel group assigned in no particular channel.

**The reference's examples are not always the real format.** Ban `created` looks like
milliseconds in the reference and is seconds on the server. `apikeylist` writes `unlimited` in
place of a number. `clientdblist -count` and `logview` put their totals and positions on the first
record only. An offline client's `clientgetids` is an empty result, not an error.

**Over-long values are refused with `1541 invalid parameter size`, and the limits are not
written down.** A 31-character channel group name was refused where 29 characters were accepted, so
group names are probably capped at 30. A 23-character query login name was already refused. The
tools pass such refusals on with an explanation rather than guessing the limits.

**Talker status only exists for someone who could not otherwise speak.** `clientedit
client_is_talker=1` worked for a client with 0 talk power in a channel needing 100. For a client with
75 talk power in a channel needing 10 it was refused with `1538 invalid parameter`, which at first
looked like the command not supporting the property at all.

**The WebQuery has a lasting client of its own.** `whoami` over the WebQuery reports a `client_id`, a
channel and the login `<internal>`. Moving that client with one request and asking `whoami` in the
next showed the same id in the new channel, so channel messages work over the WebQuery as well.
`apikeylist` answers the same with and without a virtual server in the path.

**Banning a client creates three rules, one of them for an address that may be everyone's.**
`banclient` on a connected client produced separate rules for the unique identity, the myTeamSpeak id
and the IP address. Behind Docker Desktop that address was the bridge gateway `172.20.0.1`, shared by
every external client, so for as long as the ban lasted nobody could have connected from outside.
Clients already connected stayed connected.

**Deploying a snapshot with `-keepfiles` crashes the server.** The option is documented in the
reference. It was tried three times. The last try was sent exactly in the documented form, with a
file stored in a channel, on a server that had not just been restarted.
- **What happened:** once the deploy hung in `deploy running`. Twice the whole process crashed within
  seconds: "TeamSpeak server has crashed", a crashdump, and container exit code 139.
- **What it left behind:** every time, the virtual server could not come back.
  - The log reads `VIRTUALSERVER_DEFAULT_SERVER_GROUP points to 0`.
  - `use -virtual` returns `2560 invalid group ID`.
  - `serverdelete` returns `1281` (tried after the first failure only).
  - Only wiping the database helped.
- **Without `-keepfiles`** the same deploy finished in about a second, also right after a restart.

A normal deploy restarts the virtual server, drops the files stored in channels, and renumbers
every channel, group and client database id. The option is refused on every path now. A bug report
was posted to the TeamSpeak community forum on 14 September 2026:
[`-keepfiles` crashes the server and leaves the virtual server unrecoverable](https://community.teamspeak.com/t/keepfiles-crashes-the-server-and-leaves-the-virtual-server-unrecoverable/65326).

**Server Admin ignores channel groups.** It holds `b_client_skip_channelgroup_permissions`, a
permission rather than the `permskip` flag, and with it the server computed 75 talk power from
Server Admin against a channel group granting 62. `ts_perm_effective` had missed it, because the
live test happened to pick a guest until a deploy reordered the client list.

## Traps that were not TeamSpeak's fault

A connection reset in the middle of a command looked like a slow server. The reader only checks
whether data has arrived, so nothing told the waiting command that its connection was gone, and it
failed only when the 30-second command timeout ran out. SSH.NET does report the loss at once, through
`ErrorOccurred` and `Closed`, and the transport now listens to both. A live test found it: a local
relay resets the session right after a command goes out.

Windows reserves port ranges. Streamable HTTP on `127.0.0.1:7811` failed to start with socket error
10013, in the published binary and the debug build alike. `netsh interface ipv4 show
excludedportrange protocol=tcp` showed a reserved range that included it and the default 7801. Such
reservations, often made for Hyper-V or WSL, can change when the machine restarts.

`ShellStream.ReadAsync` returns zero because nothing has arrived yet, not because the stream ended
— treating zero as EOF silently kills the reader after the greeting. And it ignores its
cancellation token, so disposal must dispose the stream rather than merely cancel.

The test host appeared to hang for two minutes after every run. A thread dump showed no frames from
this project at all: the tests were long finished and the process was flushing Application Insights
telemetry pulled in by the coverage extension. `TESTINGPLATFORM_TELEMETRY_OPTOUT=1` fixes it; the
run went from two minutes to eight seconds.
