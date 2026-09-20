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
- **What CI has proved, and what it has not.** `.github/workflows/ci.yml` first ran on
  20 September 2026, when the repository was pushed. Build and test passed on ubuntu-latest and on
  windows-latest: restore, `dotnet format --verify-no-changes`, a Release build and the suite.
  Without `TSMCP_TEST_HOST` the live tests skip themselves there, so that run says nothing about a
  real TeamSpeak server. The package job, started by hand the same day, produced all four packages
  and the three binaries as an artifact.
- **The packages are built in CI, and tried by hand.**
  - The artifact of that run was opened: the Linux binaries carry the right machine types in their
    ELF headers (x86-64 and AArch64), and the win-x64 one answered an MCP `initialize` over stdio
    with version `0.1.0-beta` and listed its 85 tools. Nothing from that artifact has spoken to a
    TeamSpeak server.
  - `dnx` ran the tool package from a local folder, not from nuget.org, where nothing is published.
  - The win-x64 binary ran over stdio and Streamable HTTP against the test server, and the linux-x64
    binary over stdio in WSL, both built locally.
  - The linux-arm64 binary has been built twice, locally and in CI, and run neither time, for lack
    of an arm64 machine.
  - No macOS build is offered.
- **The flood limit was measured in one sitting, and the exemption is back.** The test server
  allow-lists the network this project connects from, so ordinarily nothing measured from here says
  anything about pacing. On 20 September 2026 the entry was removed for a few minutes: an unpaced
  burst was refused at the twelfth command, and thirty calls through this project's own client
  absorbed five refusals with no error reaching the caller. That is one server, one build, one
  evening. Everything measured from this machine before and after that window was exempt, and the
  150 ms default is still twice the stock budget of 10 commands per 3 seconds.
- **The parsers are tested against 53 captured responses covering 31 commands**, not all 143. Exotic
  commands may still hold surprises. 28 of them were captured on 15 September 2026, 14 commands
  over both transports within the same minute, and the SSH half of that batch is what keeps the
  server's real `\n\r` line endings: the 19 SSH captures before it had them normalised, and the
  WebQuery ones are JSON, where the question does not arise. All of them were captured from
  6.0.0-beta12.1 and have not been re-captured since; the wire format looks unchanged on beta13,
  where 38 of the 39 live tests pass on the same parsers, but nothing pins that fixture by fixture.
- **The test server is small, and often has no human client.** It carries a permanent channel tree
  of about two dozen channels, but usually only the query session is connected, so the
  client-facing tools, among them client listing and decoding, effective permissions, complaints and
  talker status, have met a real person only on the occasions someone was connected, and otherwise in unit tests.
  Several live tests skip themselves when nobody is online. After the transport rework that moved
  virtual server selection into each command, they ran again with a client connected and passed;
  only the disruptive test was left out.
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

  These are covered by unit tests only, because on the shared test server they cannot be undone or
  would take something away:
  - deleting an identity (the live attempt was refused with `523` while the client was online)
  - resetting permissions

  `ts_vserver_create` has only ever been refused live. The TeamSpeak 6 beta test server refuses a
  second virtual server with `2816 virtualserver limit reached`; there are no licences for the beta
  yet that would lift the limit, so the success path cannot run there.
- **`keepFiles` is verified on one server, for one deploy shape.** On 18 September 2026 two files
  survived a deploy with the option and were gone after the same snapshot was deployed without it,
  both through this project's tools against 6.0.0-beta13. That is one virtual server, small text
  files in one channel, and a snapshot of the server itself; a snapshot from a different server, an
  encrypted one, and channels holding many or large files have not been tried.
- **The tool schema snapshot has been seen to generate differently once.** In one run out of about
  ten on 18 September 2026, `ts_file_download` came out with the SDK-injected
  `IProgress<ProgressNotificationValue>` parameter in its input schema, where every other run leaves
  it out; the runs before and after, five of them in a row, were byte-identical. The cause is not
  known. A race between the test classes that each build a host is the obvious suspect, since the
  project runs them in parallel, so a snapshot regenerated in such a run would commit a schema the
  server does not really serve. Twenty further regenerations on 21 September 2026 came out
  byte-identical, which puts it at one deviation in about thirty runs and leaves the cause exactly
  as unknown as before. Compare the diff before committing a regenerated snapshot.
- **The suite went red once, and which test it was is not known.** On 20 September 2026 one run of
  686 reported `failed: 1`, and only the summary was kept, so the test never got a name. Twenty-three
  runs since, eight of them of the project that holds the schema snapshot test, have all been green.
  The one-off snapshot difference below is the obvious suspect, because it would fail exactly like
  this, but nothing proves it. Read a red CI run with this in mind, and keep the failing test's name
  when it happens again.
- **`serverstop` is refused on every version, which is broader than what was measured.** The bug was
  measured on 6.0.0-beta13 only, and TeamSpeak confirmed it there. Whether 6.0.0-beta12.1 shares it
  was never tried, and the version that carries the announced hotfix is not known yet, so the
  refusal covers everything until one of those answers arrives. It will be too broad from the moment
  the hotfix ships until `KnownCrashes.StopFixedIn` is set to that version. The live test that
  restarts a virtual server is skipped for the same reason, so subscription recovery after a restart
  currently has no live coverage.
- **Three claims in the documents were never measured here.** That TeamSpeak 6 no longer offers the
  raw TCP query of TeamSpeak 3 follows from the server having no setting for one and from
  TeamSpeak's own documentation, but port 10011 was never probed on the test server. A guest
  being refused a command with `5120 out of scope` is read as "guests may not have it at all",
  because the server says `command not allowed for guest access`; whether granting the permission to
  the guest group changes that was not tried. And the HTTPS WebQuery, which the README lists on port
  10443, comes from TeamSpeak's settings rather than from a measurement: the test server only ever
  ran the plain HTTP one, so nothing here has spoken to a TLS query port or met its certificate.
- **The guest login was probed by hand, not in a live test.** Connecting as the ServerQuery guest
  over SSH and over the WebQuery, what such a session may do, and that a permission granted to
  server group 1 reaches it, were all measured against 6.0.0-beta13 on 18 September 2026, through
  this project's own transports. There is no automated live test for it: the test server's guest
  group holds no permissions, and giving it some would change the server other tests read.

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
- **The check of opened local files ran on glibc x64 only.** Windows, Linux x64 with glibc in WSL,
  where a self-contained build of the unit tests covered symbolic links, hard links and a directory
  swapped for a link, and since 20 September 2026 the ubuntu-latest CI runner as well. It uses
  `statx`, `realpath` and `/proc/self/fd` on Linux, which were not run on arm64, on musl or inside
  the container image.
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
