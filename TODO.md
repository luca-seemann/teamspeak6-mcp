# To do

Things decided or announced but deliberately not built yet: the list of work waiting for a trigger.

- **Server→client push for events.** Deferred on 15 September 2026. Delivery stays `ts_events_poll`
  and `ts_events_wait` for now. Push is feasible over stdio (`SendNotificationAsync`), impossible over
  stateless Streamable HTTP, and how Claude Code shows such notifications is undocumented.
- **Build and run the container image.** Waiting for a Docker host.
  `docker/Dockerfile` publishes the same self-contained single-file binary that
  was run locally for linux-x64 (the linux-arm64 one was only built), but the image itself has never
  been built.
  Build it for both architectures, start it with `docker/docker-compose.yml` against the test server,
  and call a tool over Streamable HTTP.
- **Run the CI workflow once.** Waiting for the push to GitHub. `.github/workflows/ci.yml` has never
  run, its package job included. Push, let build and test pass on Linux and Windows, then start the
  workflow by hand and check the package artifacts.
- **Publish to nuget.org.** Waiting for the repository's real address and the decision to release.
  Replace the `REPLACE-WITH-OWNER` placeholder in `src/TeamSpeak.Mcp/.mcp/server.json`, set
  `RepositoryUrl` for the package, push the packages from `dotnet pack`, then check that
  `dnx TeamSpeak6.Mcp` works without `--add-source`.
- **Run the linux-arm64 binary on arm64 hardware.** Waiting for an arm64 machine. It has only been
  built. Start it over stdio and call a tool against the test server, as was done for win-x64 and
  linux-x64.
- **Run the disruptive live test once more.** Waiting for a moment when a connected client may be
  kicked. `TSMCP_TEST_DISRUPTIVE=1` has not been run since the phase 9 transport changes; the other
  client tests passed afterwards.
- **ServerQuery guest login.** The next TeamSpeak 6 beta reportedly adds a way to log in to the
  ServerQuery as a guest. Once a server version with it is available, probe how it works, and support
  it in the profiles, as an alternative to `serveradmin` credentials and API keys.
