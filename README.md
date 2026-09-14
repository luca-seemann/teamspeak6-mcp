# teamspeak6-mcp

A [Model Context Protocol](https://modelcontextprotocol.io) server for administering
**TeamSpeak 6** servers, so an AI assistant can do the tedious parts of server administration:
tidy up channel trees, explain why a user lacks a permission, work through bans and complaints,
manage groups, and watch what is happening on the server.

> **Status: early development.** The query client is complete and verified against a live server;
> the MCP tools are not written yet, so this is not usable as an MCP server yet.

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

That entry was a connection from `192.0.2.80` on the LAN.

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
| `TeamSpeak:Profiles:<name>:Host` | `TSMCP_TeamSpeak__Profiles__<name>__Host` | — |
| `TeamSpeak:Profiles:<name>:Password` | `TSMCP_TeamSpeak__Profiles__<name>__Password` | — (enables SSH) |
| `TeamSpeak:Profiles:<name>:SshPort` | `TSMCP_TeamSpeak__Profiles__<name>__SshPort` | `10022` |
| `TeamSpeak:Profiles:<name>:WebQueryUrl` | `TSMCP_TeamSpeak__Profiles__<name>__WebQueryUrl` | — |
| `TeamSpeak:Profiles:<name>:ApiKey` | `TSMCP_TeamSpeak__Profiles__<name>__ApiKey` | — (enables the WebQuery) |
| `TeamSpeak:Profiles:<name>:Transport` | `TSMCP_TeamSpeak__Profiles__<name>__Transport` | `Auto` (SSH when a password is set) |
| `TeamSpeak:Profiles:<name>:DefaultVirtualServerId` | `TSMCP_TeamSpeak__Profiles__<name>__DefaultVirtualServerId` | `1` |
| `TeamSpeak:Profiles:<name>:Safety` | `TSMCP_TeamSpeak__Profiles__<name>__Safety` | the global level |

Each profile keeps one long-lived connection, opened on the first tool call that needs it.

## Tools

All tools so far only read. The one exception to `ReadOnly` is `ts_token_list`, which needs `Write`
because privilege keys are live credentials.

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
| Anything else | `ts_query_raw` | Any other ServerQuery command, at the safety level of that command. |

Every tool except `ts_profiles_list` takes an optional `profile`, and every tool below the instance
level an optional `virtualServerId`.

`ts_perm_effective` shows, for one client in one channel, the value the client ends up with and
every assignment behind it, marking the one that decided and any skip flag that kept the channel
layers out.

### Resources

The same data is available as resources, for clients that attach context instead of calling tools:
`ts://profiles`, `ts://{profile}/permissions`, and `ts://{profile}/{virtualServerId}/` followed by
`info`, `channels`, `clients` or `groups`.

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
claude mcp add teamspeak \
  -e TSMCP_TeamSpeak__Profiles__home__Host=ts.example.com \
  -e TSMCP_TeamSpeak__Profiles__home__Password=<query admin password> \
  -- dotnet run --project /path/to/teamspeak6-mcp/src/TeamSpeak.Mcp
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
