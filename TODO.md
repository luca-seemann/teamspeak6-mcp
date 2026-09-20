# To do

Work and checks that wait for a trigger: things decided or announced but deliberately not built yet,
and verification that needs something not at hand yet. [docs/known-gaps.md](docs/known-gaps.md)
lists everything that is unverified, including what cannot be checked on the test server at all.

- **See the channel arrive in an interactive Claude Code session.** Waiting for someone at a
  terminal: start `claude --dangerously-load-development-channels server:teamspeak`, accept the
  warning dialog, subscribe with `ts_events_subscribe`, and watch whether an event shows up as a
  channel tag. The push itself was verified on 21 September 2026 against a live TeamSpeak server,
  notification and sender gate included, but a `claude -p` session on 2.1.268 with the flag saw no
  channel tag, and print mode may simply not deliver them.
- **Build and run the container image.** Waiting for a Docker host.
  `docker/Dockerfile` publishes the same self-contained single-file binary that
  was run locally for linux-x64 (the linux-arm64 one was only built), but the image itself has never
  been built.
  Build it for both architectures, start it with `docker/docker-compose.yml` against the test server,
  and call a tool over Streamable HTTP. Check as well what was added since without a build: the
  container refuses to start without `TSMCP_HTTP_TOKEN`, and the unprivileged user can write the
  remembered host keys to `/state/known_hosts` on the named volume, so a recreated container keeps
  trusting the same key.
- **Turn on private vulnerability reporting.** Waiting for the repository to become public: GitHub
  offers the setting for public repositories only, and while this one is private nobody outside can
  file a report anyway. On the day it opens, enable it under Settings, then open the link in
  [SECURITY.md](SECURITY.md) while logged out and check that the form appears rather than a 404.
- **Publish to nuget.org.** Waiting for the decision to release. The metadata is in place, so this
  is: tag a version, let the release workflow build the packages, push all four with an API key,
  then check that `dnx TeamSpeak6.Mcp` works without `--add-source`. Publishing also lists the
  server in the MCP registry through `.mcp/server.json`.
- **Run the linux-arm64 binary on arm64 hardware.** Waiting for an arm64 machine. It has only been
  built. Start it over stdio and call a tool against the test server, as was done for win-x64 and
  linux-x64.
- **Track down the one-off tool schema difference.** Waiting for it to happen again, or for someone
  to sit down with it. Once in about ten runs the snapshot came out with `ts_file_download` carrying
  its `IProgress` parameter in the input schema; see [docs/known-gaps.md](docs/known-gaps.md). Run
  `tests/TeamSpeak.Mcp.Tests` in a loop with `TSMCP_UPDATE_SNAPSHOTS=1` and compare the bytes, and if
  it reproduces, try putting every test class that builds a host into one xunit collection so they
  cannot run at the same time. Twenty regenerations on 21 September 2026 did not reproduce it, so
  that change stays unjustified for now. A second sighting may already have happened: one run of the
  suite failed a single test on 20 September 2026 without its name being kept, and 23 runs
  afterwards were green.
- **Lift the `serverstop` refusal when the hotfix lands.** Waiting for TeamSpeak to name the version.
  They confirmed the bug on 19 September 2026 in
  [the report](https://community.teamspeak.com/t/serverstop-never-completes-when-a-file-transfer-is-pending-and-the-virtual-server-can-never-be-stopped-again/65376)
  and announced a hotfix. Then: set `KnownCrashes.StopFixedIn` to that version, so the refusal
  narrows to the releases below it; un-skip
  `A_subscription_recovers_on_its_own_after_its_virtual_server_is_restarted`, which stops and starts
  a virtual server; and put the restored assertion on `sid` and `reasonmsg` back into
  `Stopping_a_virtual_server_passes_the_reason`. If they also say how an already stuck virtual
  server can be freed without a process restart, that belongs in the refusal message and in
  [docs/teamspeak6-findings.md](docs/teamspeak6-findings.md).
