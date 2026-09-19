# Setting up

Preparing the TeamSpeak server, installing this one, connecting a client, and running it as a
service. The tools are described in [tools.md](tools.md), the server itself in the
[README](../README.md).

## Preparing the TeamSpeak server

1. **Enable the SSH query.** Both query interfaces are off by default. Set
   `TSSERVER_QUERY_SSH_ENABLED=1`, and `TSSERVER_QUERY_HTTP_ENABLED=1` if you also want the
   WebQuery. A port that accepts connections but never greets is the symptom of an interface that is
   published but not enabled.
2. **Know the `serveradmin` password.** Set it with `TSSERVER_QUERY_ADMIN_PASSWORD`; otherwise the
   server generates one on first start and prints it to its log once.
3. **Make the ports reachable** from where this server runs: 10022 for the SSH query, and 30033 for
   file transfer. 10080 only if you use the WebQuery. Reachable by you also means reachable by
   others: from 6.0.0-beta13 both query ports answer a caller with no credentials at all, as the
   ServerQuery guest, so keep them off the open internet and grant the `Guest Server Query` group
   nothing you would not publish.
4. **Optionally mint a WebQuery key**, over SSH: `apikeyadd scope=manage lifetime=0`. SSH is still
   needed for events and file transfer, so a password is the better choice whenever you have one.
   A profile can also go without either and connect as the guest; see
   [Without credentials](#without-credentials-as-the-serverquery-guest).
5. **Start read-only.** Leave `TeamSpeak:Safety` at `ReadOnly` until you have seen what the tools do,
   then raise it per profile.

`docker/docker-compose.yml` shows all of this for a TeamSpeak container.

## Installing

There are three ways to run the server, and only building from source needs .NET installed.

### As a NuGet tool, through `dnx`

`dnx` comes with the .NET 10 SDK. It fetches the package for the machine it runs on and starts it
without installing anything. The package holds the same self-contained binary as below, for
win-x64, linux-x64 and linux-arm64.

The package is not on nuget.org yet. Build it and point `dnx` at the folder:

```bash
dotnet pack src/TeamSpeak.Mcp -c Release -o artifacts/nuget

claude mcp add teamspeak \
  -e TSMCP_TeamSpeak__Profiles__home__Host=ts.example.com \
  -e TSMCP_TeamSpeak__Profiles__home__Password='<query admin password>' \
  -- dnx TeamSpeak6.Mcp --version 0.1.0-beta --yes --add-source /path/to/artifacts/nuget
```

### As a single-file binary

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
  -e TSMCP_TeamSpeak__Profiles__home__Password='<query admin password>' \
  -- /opt/teamspeak6-mcp/teamspeak6-mcp
```

See [Connecting a client](#connecting-a-client) for scopes, a shared `.mcp.json`, Windows and Claude
Desktop.

### As a container, over Streamable HTTP

`docker/Dockerfile` builds an image for linux/amd64 and linux/arm64 that serves Streamable HTTP on
port 7801, and `docker/docker-compose.yml` starts it next to a TeamSpeak server. The endpoint is the
root path. Inside a container the server listens on all interfaces, so it needs a bearer token and
refuses to start without one; the compose file publishes the port on 127.0.0.1 only:

```bash
export TSMCP_HTTP_TOKEN=$(openssl rand -hex 32)
docker compose -f docker/docker-compose.yml up -d
claude mcp add --transport http teamspeak http://localhost:7801/ --header "Authorization: Bearer $TSMCP_HTTP_TOKEN"
```

The image has not been built yet; see [TODO.md](../TODO.md).

However it is started, the Streamable HTTP endpoint protects itself:
- **A foreign `Origin` is refused with 403.** A web page open in your browser cannot use the server
  through DNS rebinding. Pages on a loopback host, and origins listed in `TeamSpeak:Http:AllowedOrigins`,
  are accepted; MCP clients send no `Origin` at all.
- **Without a token, only loopback addresses and names.** The server binds only to a loopback address
  and accepts only loopback host names, plus `TeamSpeak:Http:AllowedHosts`.
- **With a token, every request must present it** as `Authorization: Bearer <token>`. It must be at
  least 32 characters long.

### From source

With the [.NET SDK 10.0.400](https://dotnet.microsoft.com/download) or newer:

```bash
dotnet run --project src/TeamSpeak.Mcp                                                     # stdio
dotnet run --project src/TeamSpeak.Mcp -- --transport http --url http://127.0.0.1:7801     # Streamable HTTP
```

On Windows, a start-up failure with `An attempt was made to access a socket in a way forbidden by its
access permissions` (socket error 10013) means the port lies in a range Windows has reserved, often
for Hyper-V or WSL. `netsh interface ipv4 show excludedportrange protocol=tcp` lists the ranges;
pick a port outside them.

## Connecting a client

**Claude Code.** `claude mcp add` registers the server with a scope:
- `--scope local`, the default, for you in the current project only;
- `--scope user` for you in every project;
- `--scope project` writes `.mcp.json` into the project, to be committed and shared.

Check it with `/mcp` inside Claude Code, or `claude mcp list`: the server should show as connected.
Ask *"Which TeamSpeak profiles are configured?"* to see the profiles, their safety level and the SSH host
key each server is trusted with.

**Never commit a password.** A shared `.mcp.json` takes environment variables instead, which Claude
Code expands when it starts the server; each person sets `TS_QUERY_PASSWORD` in their own environment:

```json
{
  "mcpServers": {
    "teamspeak": {
      "command": "/opt/teamspeak6-mcp/teamspeak6-mcp",
      "env": {
        "TSMCP_TeamSpeak__Profiles__home__Host": "ts.example.com",
        "TSMCP_TeamSpeak__Profiles__home__Password": "${TS_QUERY_PASSWORD}"
      }
    }
  }
}
```

**On Windows**, in PowerShell, with the binary from above:

```powershell
claude mcp add teamspeak `
  -e TSMCP_TeamSpeak__Profiles__home__Host=ts.example.com `
  -e 'TSMCP_TeamSpeak__Profiles__home__Password=<query admin password>' `
  -- C:\Tools\teamspeak6-mcp\teamspeak6-mcp.exe
```

**Optional settings** go in as further `-e` pairs, for example:
- `TSMCP_TeamSpeak__Profiles__home__Safety=Write` to allow changes on that profile (see [Safety](../README.md#safety));
- `TSMCP_TeamSpeak__DisabledToolGroups=files,events` to leave groups out (see [Tool groups](tools.md#tool-groups));
- `TSMCP_TeamSpeak__ToolResultText=Toon` for fewer tokens on large lists (see
  [Tool results as TOON](tools.md#tool-results-as-toon)).

### Without credentials, as the ServerQuery guest

From TeamSpeak **6.0.0-beta13** on, a server can be reached with no credentials at all. Set the
profile's `Username` to `guest` and leave `Password` and `ApiKey` empty:

```
TSMCP_TeamSpeak__Profiles__public__Host=ts.example.com
TSMCP_TeamSpeak__Profiles__public__Username=guest
```

Over SSH the server takes that name with any password; over the WebQuery — add
`__WebQueryUrl=http://ts.example.com:10080` and select it with `__Transport=WebQuery` — the request
simply carries no key. `ts_profiles_list` marks such a profile as a guest.

A guest is nobody on the server, and by default may do almost nothing: `ts_whoami`, the server
version, and nothing else. Everything else comes back as *insufficient client permissions* or *out
of scope*, which is the TeamSpeak server's answer, not this server's safety level. To make a guest
profile useful, grant the permissions you want it to have to server group **1, `Guest Server
Query`**, on the TeamSpeak side — that group covers guests on both interfaces. Grant read
permissions only; a guest login is unauthenticated, and anyone else can use it too.

**Claude Desktop** reads `claude_desktop_config.json`, in `%APPDATA%\Claude\` on Windows and
`~/Library/Application Support/Claude/` on macOS. It takes the same `mcpServers` entry as the
`.mcp.json` above, with the password written into `env` directly, since that file stays on your
machine. Restart Claude Desktop after changing it.


## Every setting

Settings are read from `appsettings.json` next to the process and from environment variables
prefixed `TSMCP_`, with the environment winning. Keep secrets in the environment:

| Setting | Environment variable | Default |
|---|---|---|
| `TeamSpeak:Safety` | `TSMCP_TeamSpeak__Safety` | `ReadOnly` |
| `TeamSpeak:EventBufferSize` | `TSMCP_TeamSpeak__EventBufferSize` | `1000` events per profile |
| `TeamSpeak:DisabledToolGroups` | `TSMCP_TeamSpeak__DisabledToolGroups` | none (every group; comma-separated, see [Tool groups](tools.md#tool-groups)) |
| `TeamSpeak:ToolResultText` | `TSMCP_TeamSpeak__ToolResultText` | `Json` (typed results); `Toon` returns text only, as TOON where shorter, see [Tool results as TOON](tools.md#tool-results-as-toon) |
| `TeamSpeak:FileTransfer:LocalDirectory` | `TSMCP_TeamSpeak__FileTransfer__LocalDirectory` | none, so file tools pass content inline only |
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
## Running over HTTP for longer

For a server several people or machines use, run the Streamable HTTP transport as a service, bound
to loopback behind your own reverse proxy with TLS, or on a private address with a bearer token. A
systemd unit on Linux, with the secrets in a file only root can read:

```ini
# /etc/systemd/system/teamspeak6-mcp.service
[Unit]
Description=TeamSpeak MCP server
After=network-online.target

[Service]
ExecStart=/opt/teamspeak6-mcp/teamspeak6-mcp --transport http --url http://127.0.0.1:7801
EnvironmentFile=/etc/teamspeak6-mcp.env
DynamicUser=yes
StateDirectory=teamspeak6-mcp
Environment=TSMCP_TeamSpeak__KnownHostsFile=/var/lib/teamspeak6-mcp/known_hosts
Restart=on-failure

[Install]
WantedBy=multi-user.target
```

```ini
# /etc/teamspeak6-mcp.env, chmod 600
TSMCP_TeamSpeak__Profiles__home__Host=ts.example.com
TSMCP_TeamSpeak__Profiles__home__Password=<query admin password>
TSMCP_TeamSpeak__Http__BearerToken=<at least 32 random characters, e.g. openssl rand -hex 32>
```

`StateDirectory` keeps the remembered host keys across restarts. Clients connect with
`claude mcp add --transport http teamspeak https://mcp.example.com/ --header "Authorization: Bearer <token>"`.

## Exempting this server from flood protection

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

## Check which client address your server actually sees

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

