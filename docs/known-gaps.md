# Known gaps

Stated plainly, because it is easy to mistake a green suite for complete confidence. The work that
closes a gap once something becomes available, such as a Git remote or an arm64 machine, is listed in
[TODO.md](../TODO.md). How the server really behaves is in
[teamspeak6-findings.md](teamspeak6-findings.md).

All live evidence comes from one TeamSpeak server: 6.0.0-beta12.1 until 18 September 2026 and
6.0.0-beta13 since. Nothing here has ever run against a released 6.0, against several virtual
servers, or against a second TeamSpeak build.

- **Reconnects are tested through a local relay, not a real network.** Live tests break a session
  right after a command goes out, in two ways:
  - a reset: the command fails within seconds, and the next one reconnects
  - silence: only the command timeout ends the wait

  Other live tests abandon a command, end an event session with `quit`, and leave a session idle past
  the timeout. A real network outage, a server restart and a flapping link have not been tried, and
  a hard drop's reopen can take up to a minute under reconnect backoff.
- **The container image has never been built.** Building it is listed in [TODO.md](../TODO.md). The
  Dockerfile publishes the same binary that was verified for linux-x64, cross-compiled for arm64.
- **The CI workflow has never run on GitHub**, its package job included. The command
  sequences, publish and pack among them, work locally.
- **The packages are verified locally only.**
  - `dnx` ran the tool package from a local folder, not from nuget.org, where nothing is published.
  - The win-x64 binary ran over stdio and Streamable HTTP against the test server, and the linux-x64
    binary over stdio in WSL.
  - The linux-arm64 binary was built but never run, for lack of an arm64 machine.
  - No macOS build is offered.
- **150 ms is a proven-safe spacing, not a measured threshold.** We know 150 ms works for 160
  commands and that no-delay bursts fail from about the fifth. The boundary between was never
  measured, because measuring it means being blocked again.
- **The parsers are tested against 53 captured responses covering 31 commands**, not all 143. Exotic
  commands may still hold surprises. The 28 captured on 15 September 2026 keep the server's real
  `\n\r` line endings; the earlier ones had them normalised. All of them were captured from
  6.0.0-beta12.1 and have not been re-captured since; the wire format looks unchanged on beta13,
  where 38 of the 39 live tests pass on the same parsers, but nothing pins that fixture by fixture.
- **The test server is small, and often has no human client.** It carries a permanent channel tree
  of about two dozen channels, but usually only the query session is connected, so the
  client-facing tools — client listing and decoding, effective permissions, complaints, talker status
  — have met a real person only on the occasions someone was connected, and otherwise in unit tests.
  Several live tests skip themselves when nobody is online. After the phase 9 transport changes they
  ran again with a client connected and passed; only the disruptive test was left out.
- **Only two instance-wide WebQuery paths were probed.** `serverstart` and `serveridgetbyport` are
  accepted both as `/{command}` and as `/{sid}/{command}`, so listing them as instance-wide costs
  nothing over HTTP. `serverstop`, `serverdelete` and `serverprocessstop` were not probed, for
  obvious reasons, and are assumed to behave the same.
- **`ts_perm_effective` is checked against the server for talk power only.** A live test compares
  it with the `client_talk_power` the server computes for a connected client, in five scenarios:
  - server groups alone
  - a server group grant
  - a channel group on top
  - the skip flag
  - the negate flag

  That comparison caught `b_client_skip_channelgroup_permissions`. Other permissions, and
  client-in-channel values combined with that permission, have not been compared.
- **Complaint and offline-message fields come from the reference, not from the server.** The server
  refused to create either for the probe: a complaint needs an online target, and a message needs a
  real client identity rather than a query login.
- **Some changing tools have not run against a server.** Every changing tool that can be undone runs
  live as a round trip that removes what it created, and a check afterwards found the test server
  and the connected client as they were. On a real connected client, moving, a channel kick, a
  poke and a private message ran live. A server kick and an offline message ran live too, in a test
  that only runs with `TSMCP_TEST_DISRUPTIVE=1`, because it disconnects the person.

  Banning a connected client was run once by hand, through `ts_ban_add`, against a second connected
  client, and every rule it created was lifted at once. It has no automated live test, because
  every run would lock that client out.

  Stopping and starting the only virtual server and deploying a snapshot ran live by hand,
  including the failing `-keepfiles` deploys described in
  [teamspeak6-findings.md](teamspeak6-findings.md). They have no automated live test,
  because a deploy renumbers every id on the server.
- **`keepFiles` is verified on one server, for one deploy shape.** On 18 September 2026 two files
  survived a deploy with the option and were gone after the same snapshot was deployed without it,
  both through this project's tools against 6.0.0-beta13. That is one virtual server, small text
  files in one channel, and a snapshot of the server itself; a snapshot from a different server, an
  encrypted one, and channels holding many or large files have not been tried.
- **The tool schema snapshot has been seen to generate differently once.** In one run out of about
  ten on 18 September 2026, `ts_file_download` came out with the SDK-injected
  `IProgress<ProgressNotificationValue>` parameter in its input schema, where every other run leaves
  it out; the runs before and after, five of them in a row, were byte-identical. The cause is not
  known — a race between the test classes that each build a host is the obvious suspect, since the
  project runs them in parallel — so a snapshot regenerated in such a run would commit a schema the
  server does not really serve. Compare the diff before committing a regenerated snapshot.
- **The `serverstop` guard closes the known way in; it does not make a stop safe.** A pending file
  transfer making the stop hang for good was reproduced deliberately on 6.0.0-beta13, and that is
  what the refusal checks for. But the same virtual server then hung on every later stop as well,
  with `ftlist` empty, the leftover files deleted and the process freshly restarted, so a virtual
  server already in that state passes the check and hangs anyway — and nothing readable through the
  query interface tells the two apart. Two further limits: which earlier versions share the bug is
  unknown, since a stop with a transfer pending was never tried on 6.0.0-beta12.1, so the refusal
  applies to every version; and a profile whose login may stop a server but not read `ftlist` cannot
  stop one through this server at all, by design, because an unreadable list counts as "might be
  busy".
- **The guest login was probed by hand, not in a live test.** Connecting as the ServerQuery guest
  over SSH and over the WebQuery, what such a session may do, and that a permission granted to
  server group 1 reaches it, were all measured against 6.0.0-beta13 on 18 September 2026, through
  this project's own transports. There is no automated live test for it: the test server's guest
  group holds no permissions, and giving it some would change the server other tests read.

  These are covered by unit tests only, because on the shared test server they cannot be undone or
  would take something away:
  - deleting an identity (the live attempt was refused with `523` while the client was online)
  - resetting permissions

  `ts_vserver_create` has only ever been refused live. The TeamSpeak 6 beta test server refuses a
  second virtual server with `2816 virtualserver limit reached`; there are no licenses for the beta
  yet that would lift the limit, so the success path cannot run there.
- **Some actions produce no event at all, and one was never tried.** Probed on a real client with
  every category registered:
  - A move by an admin, a channel kick (`reasonid=4`) and a server kick (`reasonid=5`, as
    `notifyclientleftview`) all arrive, with the invoker.
  - A channel password or description change adds `notifychannelpasswordchanged` or
    `notifychanneldescriptionchanged` next to `notifychanneledited`.
  - Server group and channel group changes, client permission changes, client description edits and
    pokes produce **no notification**, so no event subscription can report them.
  - A ban enforced on a client that connects was not tried, because it needs the banned person to
    reconnect.
- **Switching to another virtual server cannot be tested here.** The test server's licence allows
  one virtual server. Whether registrations survive a `use` of another virtual server is therefore
  unknown. The event session never switches, since it serves exactly one virtual server.
- **`ts_events_wait` and client timeouts were read from documentation, not tried.** According to
  the Claude Code documentation, a tool call there is aborted only after about 28 hours
  (`MCP_TOOL_TIMEOUT`), or after 30 minutes (stdio) or 5 minutes (HTTP) without a response. An HTTP
  request timer is at least 60 seconds. A wait of at most 60 seconds stays inside all of these. Other
  MCP clients may differ.
- **A hard TCP drop is reopened by the keepalive, but not quickly under load.** Measured: a session
  killed with `quit` and then left untouched was reopened and re-registered by the keepalive alone,
  but it took up to about a minute, because each failed reconnect adds exponential backoff and the
  connection throttle spaces attempts. A vserver restart, where the TCP stays up, recovers faster
  (the watchdog re-registers within its interval, 30 seconds by default). Events that arrive during
  either gap are lost. There is no always-on test with a tight time bound, because the reopen time
  depends on backoff and server load; the behaviour is covered instead by the idle-survival and the
  reconnect-re-registration tests.
- **The SSH host key check has never met a genuinely changed key.** Live, against the test server:
  - the first connection remembered the key in a fresh `known_hosts` file;
  - a deliberately wrong pinned fingerprint was refused during the key exchange, after one attempt;
  - the right pin connected.

  A server whose key really changed, after a reinstall or behind an interception, was simulated only
  through that wrong pin. Two processes meeting a new server at the same moment were tried in unit
  tests, not across two real processes.
- **The check of opened local files ran on two platforms only.** Windows, and Linux x64 with glibc in
  WSL, where a self-contained build of the unit tests covered symbolic links, hard links and a
  directory swapped for a link. It uses `statx`, `realpath` and `/proc/self/fd` on Linux, which were
  not run on arm64, on musl or inside the container image.
- **Deletion confirmations are live-tested for some targets only.** The live suite confirmed deleting
  channels, server groups, channel groups and stored files, overwriting a file, deleting this query
  login's own API key by the login name `serveradmin`, and deleting a query login it created, which
  it does only when some identity has no login yet. Confirming a key of another identity by that
  identity's nickname, and deleting an identity, have unit tests only: the test server has no key of
  another identity, and deleting an identity was refused live while its client was online.
- **TOON results were measured in one client.** That Claude Code gives the model `structuredContent`
  and drops the text block, and that the TOON mode saves about a third of the tokens on a large list,
  was measured with Claude Code 2.1.268 and Haiku, headless. Other clients, and later Claude Code
  versions, may choose differently.
- **Some documented setups were never tried.** The README's systemd unit for a long-running HTTP
  server and its Claude Desktop configuration are written from the documentation of systemd and
  Claude Desktop, not run. The Claude Code commands and the `.mcp.json` variable expansion follow the
  Claude Code documentation; the `claude mcp add` form was used throughout development.
- **File transfer is verified on the LAN only.** Some cases are unverified:
  - **Transfer paths:** the `ip` field a server sends when it thinks port 30033 is unreachable from
    the query address, which the test server never did. The client tries such addresses first and
    falls back to the profile's host.
  - **Real slow links:** stalls and a 20 KB/s trickle were simulated on the LAN, not measured over a
    slow network.
  - **A download stalled longer than its socket buffers:** only 100 KB were tried, which may have
    been fully buffered, so whether the server closes a stalled download is unknown.
