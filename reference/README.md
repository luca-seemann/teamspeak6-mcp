# Captured protocol reference

TeamSpeak 6 ships **no public ServerQuery command reference**. The official documentation site has
four query pages and none of them lists a command, an endpoint path, or a response shape. The real
reference lives inside the server package, behind `--query-documentation-path`.

`serverquery-6.0.0-beta12.1.txt` is that reference, captured from a live server by running `help`
and then `help <command>` for all 141 commands. It is the source of truth for this project: when
the code and this file disagree, this file wins.

Alongside it, `tests/TeamSpeak.Query.Tests/Fixtures/` holds raw responses for the same commands over
both transports — `ssh/` as raw query text, `http/` as raw WebQuery JSON. They are captured bytes,
not hand-written, and the parser tests run against them.

## Where the official docs are wrong

Verified against server 6.0.0-beta12.1:

| Docs claim | Reality |
|---|---|
| WebQuery accepts HTTP Basic Auth with `serveradmin` | Rejected. `x-api-key` is the only accepted credential — Basic Auth with correct credentials still returns `5124 missing apikey`, while a bad `x-api-key` returns the distinct `5122 invalid apikey`. |
| SSH shows a `TS6>` prompt | The greeting line is `TS3` and no prompt is ever emitted. |
| (undocumented) | The SSH query refuses PTY requests. An SSH client must open its channel without one. |

## Re-capturing

The capture scripts are not committed; they are throwaway. To rebuild the reference, open one SSH
session and issue `help` followed by `help <command>` for each name in the first column, writing the
raw bytes out unmodified.

**Pace it — roughly 150 ms between commands is enough.** The reference above was captured that way,
160 commands in one SSH session, without ever being throttled.

Send faster and the server rejects with a perfectly ordinary status that says exactly what is
wrong:

```json
{"status":{"code":524,"extra_message":"please wait 1 seconds","message":"client is flooding"}}
```

Honour it. **Continuing to send through a 524 escalates to an IP-level block that takes down both
the SSH and the HTTP interface for several minutes**, and that block presents as a connection
closed before the SSH identification string, or an empty HTTP reply — which looks nothing like rate
limiting. Polling to check whether it has lifted keeps it alive.

No server-side setting avoids this, and three plausible-looking ones do nothing:
`TSSERVER_QUERY_POOL_SIZE` (tested at 32), `TSSERVER_QUERY_SKIP_BRUTE_FORCE_CHECK` (that covers
failed *logins*), and the flood allow list (tested with the client IP verifiably loaded — see the
startup log line `CIDRManager | updated query_ip_allowlist ips:`). Note also that
`TSSERVER_QUERY_ALLOW_LIST` names a *file* of CIDRs, not an IP value; pointing it at an address
stops the query interfaces from starting at all.

`tests/.../Fixtures/http/flooding.json` is a real 524, captured the hard way.
