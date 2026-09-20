# To do

Work and checks that wait for a trigger: things decided or announced but deliberately not built yet,
and verification that needs something not at hand yet. [docs/known-gaps.md](docs/known-gaps.md)
lists everything that is unverified, including what cannot be checked on the test server at all.

- **Server→client push for events.** Deferred on 15 September 2026. Delivery stays `ts_events_poll`
  and `ts_events_wait` for now. Push is feasible over stdio (`SendNotificationAsync`), impossible over
  stateless Streamable HTTP, and how Claude Code shows such notifications is undocumented.
- **Build and run the container image.** Waiting for a Docker host.
  `docker/Dockerfile` publishes the same self-contained single-file binary that
  was run locally for linux-x64 (the linux-arm64 one was only built), but the image itself has never
  been built.
  Build it for both architectures, start it with `docker/docker-compose.yml` against the test server,
  and call a tool over Streamable HTTP. Check as well what was added since without a build: the
  container refuses to start without `TSMCP_HTTP_TOKEN`, and the unprivileged user can write the
  remembered host keys to `/state/known_hosts` on the named volume, so a recreated container keeps
  trusting the same key.
- **Read the first CI runs, and start the package job by hand.** The repository went to GitHub on
  20 September 2026, so `.github/workflows/ci.yml` runs on every push to `master` from now on;
  nobody has looked at a run yet. Check that build and test pass on Linux and Windows. The package
  job starts by hand only and has still never run, so start it and check its artifacts.
- **Publish to nuget.org.** Waiting for the decision to release. The metadata is in place, so this
  is: tag a version, let the release workflow build the packages, push them with an API key, then
  check that `dnx TeamSpeak6.Mcp` works without `--add-source`. Publishing also lists the server in
  the MCP registry through `.mcp/server.json`.
- **Run the linux-arm64 binary on arm64 hardware.** Waiting for an arm64 machine. It has only been
  built. Start it over stdio and call a tool against the test server, as was done for win-x64 and
  linux-x64.
- **Run the disruptive live test once more.** Waiting for a moment when a connected client may be
  kicked. `TSMCP_TEST_DISRUPTIVE=1` has not been run since the transport rework that moved virtual
  server selection into each command; the other client tests passed afterwards.
- **Track down the one-off tool schema difference.** Waiting for it to happen again, or for someone
  to sit down with it. Once in about ten runs the snapshot came out with `ts_file_download` carrying
  its `IProgress` parameter in the input schema; see [docs/known-gaps.md](docs/known-gaps.md). Run
  `tests/TeamSpeak.Mcp.Tests` in a loop with `TSMCP_UPDATE_SNAPSHOTS=1` and compare the bytes, and if
  it reproduces, try putting every test class that builds a host into one xunit collection so they
  cannot run at the same time. A second sighting may already have happened: one run of the suite
  failed a single test on 20 September 2026 without its name being kept, and 23 runs afterwards were
  green.
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
