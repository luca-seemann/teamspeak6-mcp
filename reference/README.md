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

**Pace it.** The server flood-bans the client IP after about five queries in quick succession,
which takes down *both* the SSH and the HTTP interface for several minutes. Raising
`TSSERVER_QUERY_POOL_SIZE` does not help and neither does
`TSSERVER_QUERY_SKIP_BRUTE_FORCE_CHECK` — the exemption is the flood allow list
(`TSSERVER_QUERY_ALLOW_LIST`, a *file* of CIDRs, default `query_ip_allowlist.txt`, which ships
containing only `127.0.0.1/32` and `::1/128`). Reuse a single connection, put a delay between
commands, and never retry in a tight loop.
