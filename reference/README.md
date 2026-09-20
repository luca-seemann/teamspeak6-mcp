# Captured protocol reference

> **Whose text this is.** `serverquery-6.0.0-beta13.txt` is TeamSpeak's own documentation, as the
> server itself prints it. It is © TeamSpeak Systems GmbH, is **not** covered by this repository's
> licence, and is included unchanged so that this project's behaviour can be checked against it.
> Everything else in this repository is the project's own work. If TeamSpeak Systems would rather it
> were not here, open an issue and it goes; [capturing it yourself](#capturing-it-yourself) takes
> half a minute.

TeamSpeak 6 ships **no public ServerQuery command reference**. The official documentation site has
four query pages and none of them lists a command, an endpoint path, or a response shape. The real
reference lives inside the server package, behind `--query-documentation-path`.

`serverquery-6.0.0-beta13.txt` is that reference, captured on 20 September 2026 from a live
6.0.0-beta13 server by running `help` and then `help <command>` for each of the 141 commands that
have a page of their own. It is the source of truth for this project: when the code and this file
disagree, this file wins.

The same capture from 6.0.0-beta12.1 carried the same 143 names with the same pages, down to the
wording; the one difference is where the overview lists `customdelete`. So these pages describe both
releases, `help serversnapshotdeploy` included, even though `-keepfiles`, which that page describes,
behaves completely differently between the two.

Alongside it, `tests/TeamSpeak.Query.Tests/Fixtures/` holds raw responses for the same commands over
both transports: `ssh/` as raw query text, `http/` as raw WebQuery JSON. They are captured bytes,
not hand-written, and the parser tests run against them.

## Where the official docs are wrong

Verified against 6.0.0-beta12.1 and again against 6.0.0-beta13:

| Docs claim | Reality |
|---|---|
| WebQuery accepts HTTP Basic Auth with `serveradmin` | Rejected. `x-api-key` is the only accepted credential. Basic Auth with correct credentials still returns `5124 missing apikey`, while a bad `x-api-key` returns the distinct `5122 invalid apikey`. On 6.0.0-beta13 a request carrying no key at all is answered as the ServerQuery guest rather than refused. |
| SSH shows a `TS6>` prompt | The greeting line is `TS3` and no prompt is ever emitted. |
| (undocumented) | The SSH query refuses PTY requests. An SSH client must open its channel without one. |

What else beta13 changed is in [docs/teamspeak6-findings.md](../docs/teamspeak6-findings.md): both
interfaces now let a guest in without credentials, and a session can `login` and `logout` in place.

## Capturing it yourself

For a single command there is no need: `ts_command_help` asks the connected server for its page, in
whatever version it runs.

For the whole file, `capture.cs` beside it does the job. It is a .NET 10 file-based app, so it needs
no project of its own:

```bash
dotnet run reference/capture.cs <host> <query password> [port] [login] [output directory]
```

The port defaults to 10022 and the login to `serveradmin`. It opens one SSH session, reads the
overview, and asks for every page listed in it: 143 commands, 31 seconds against the test server on
a LAN. The file goes into `reference/` when you run it from the repository root, into the working
directory otherwise, and is named after the version the server reports, so a capture never silently
overwrites another release. Two runs on the same day produced the same bytes.

Three details of the server shape how it is written, and each one cost an evening to find: the
server refuses a pseudo-terminal, it sends no prompt, so answers can only be framed on the status
line that closes them, and its lines end with `\n\r`, which the file keeps as the blank line it
renders as.

**Pace it: roughly 150 ms between commands is enough.** That is the pace `capture.cs` keeps, and
neither of its runs was throttled. The longest session measured here carried 160 commands the same
way, also without a single rejection.

Send faster and the server rejects with a perfectly ordinary status that says exactly what is
wrong:

```json
{"status":{"code":524,"extra_message":"please wait 1 seconds","message":"client is flooding"}}
```

Honour it. **Continuing to send through a 524 escalates to an IP-level block that takes down both
the SSH and the HTTP interface for several minutes**, and that block presents as a connection
closed before the SSH identification string, or an empty HTTP reply, which looks nothing like rate
limiting. Polling to check whether it has lifted keeps it alive.

Connections cost far more than commands. One session carrying 160 commands was fine; five or six
connections in quick succession earned the block. A client that reuses one session is in far less
danger than one that reconnects.

Two settings that look like they should help do not. `TSSERVER_QUERY_POOL_SIZE` changes nothing
(tested at 32), and `TSSERVER_QUERY_SKIP_BRUTE_FORCE_CHECK` covers failed *logins*, a different
mechanism.

The flood **allow list does** work, but only if it names the address the server actually sees.
An allow-listed client address can still achieve nothing, for a reason that has nothing to do
with the allow list: behind Docker Desktop's port publishing the server saw the
bridge gateway for every external client. See the deployment note in the top-level
[README](../README.md); the short version is to read the server's own log line before trusting an
entry. `TSSERVER_QUERY_ALLOW_LIST` also names a *file* of CIDRs rather than an address, so pointing it
at an address stops the query interfaces from starting at all.

`tests/.../Fixtures/http/flooding.json` is a real 524, captured the hard way.
