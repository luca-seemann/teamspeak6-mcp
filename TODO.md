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
- **Run the CI workflow once.** Waiting for the push to GitHub. `.github/workflows/ci.yml` has never
  run, its package job included. Push, let build and test pass on Linux and Windows, then start the
  workflow by hand and check the package artifacts.
- **Publish to nuget.org.** Waiting for the decision to release. The metadata is in place, so this
  is: tag a version, let the release workflow build the packages, push them with an API key, then
  check that `dnx TeamSpeak6.Mcp` works without `--add-source`. Publishing also lists the server in
  the MCP registry through `.mcp/server.json`.
- **Run the linux-arm64 binary on arm64 hardware.** Waiting for an arm64 machine. It has only been
  built. Start it over stdio and call a tool against the test server, as was done for win-x64 and
  linux-x64.
- **Run the disruptive live test once more.** Waiting for a moment when a connected client may be
  kicked. `TSMCP_TEST_DISRUPTIVE=1` has not been run since the phase 9 transport changes; the other
  client tests passed afterwards.
- **Track down the one-off tool schema difference.** Waiting for it to happen again, or for someone
  to sit down with it. Once in about ten runs the snapshot came out with `ts_file_download` carrying
  its `IProgress` parameter in the input schema; see [docs/known-gaps.md](docs/known-gaps.md). Run
  `tests/TeamSpeak.Mcp.Tests` in a loop with `TSMCP_UPDATE_SNAPSHOTS=1` and compare the bytes, and if
  it reproduces, try putting every test class that builds a host into one xunit collection so they
  cannot run at the same time.
- **Act on the answer to the `serverstop` bug report.** Waiting for TeamSpeak. The report is
  [`serverstop` never completes when a file transfer is pending](https://community.teamspeak.com/t/serverstop-never-completes-when-a-file-transfer-is-pending-and-the-virtual-server-can-never-be-stopped-again/65376),
  posted on 19 September 2026. Two answers would change code: if a version is named in which this is
  fixed, the refusal in [KnownCrashes](src/TeamSpeak.Mcp/Safety/KnownCrashes.cs) should be gated on
  it the way `-keepfiles` is, instead of applying to every version; and if the state can be cleared
  without losing the virtual server, that belongs in the refusal message and in
  [docs/teamspeak6-findings.md](docs/teamspeak6-findings.md).
