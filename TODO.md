# To do

Work and checks that wait for a trigger: things decided or announced but deliberately not built yet,
and verification that needs something not at hand. [docs/known-gaps.md](docs/known-gaps.md) lists
what is unverified about the software itself, including what cannot be checked on the test server at
all.

## Waiting for a machine, or a moment

- **Build and run the container image.** `docker/Dockerfile` publishes the same self-contained
  binary that was run locally for linux-x64, and the linux-arm64 one that has still never been
  started. The image itself has never been built. Build it for both architectures, bring it up with
  `docker/docker-compose.yml` against a test server, and call a tool over Streamable HTTP. Check as
  well what was added since without a build: the container refuses to start without
  `TSMCP_HTTP_TOKEN`, and the unprivileged user can write the remembered host keys to
  `/state/known_hosts` on the named volume, so a recreated container keeps trusting the same key.
- **Run the linux-arm64 binary on arm64 hardware.** It has been built twice, locally and in CI, and
  started neither time. Run it over stdio and call a tool against a server, as was done for win-x64
  and linux-x64. A public repository would also make GitHub's arm64 runners available for this.
- **See the channel arrive in an interactive Claude Code session.** Start
  `claude --dangerously-load-development-channels server:teamspeak`, accept the warning dialog,
  subscribe with `ts_events_subscribe`, and watch whether an event shows up as a channel tag. The
  push itself was measured on 21 September 2026 against a live server, notification and sender gate
  included, but a `claude -p` session on 2.1.268 with the flag saw no channel tag, and print mode
  may simply not deliver them.

## Waiting for a decision

- **Publish to nuget.org.** The metadata is in place, so this is: tag a version, let the release
  workflow build the packages, push all four with an API key, then check that `dnx TeamSpeak6.Mcp`
  works without `--add-source`. Publishing also lists the server in the MCP registry through
  `.mcp/server.json`. It is the one step that cannot be taken back.

## Waiting for somebody else

- **Lift the `serverstop` refusal when the hotfix lands.** TeamSpeak confirmed the bug on
  19 September 2026 in
  [the report](https://community.teamspeak.com/t/serverstop-never-completes-when-a-file-transfer-is-pending-and-the-virtual-server-can-never-be-stopped-again/65376)
  and announced a hotfix without naming the version. Then: set `KnownCrashes.StopFixedIn` to that
  version, so the refusal narrows to the releases below it; un-skip
  `A_subscription_recovers_on_its_own_after_its_virtual_server_is_restarted`, which stops and starts
  a virtual server; and put the restored assertion on `sid` and `reasonmsg` back into
  `Stopping_a_virtual_server_passes_the_reason`. If they also say how a virtual server already stuck
  in `shutting down` can be freed without a process restart, that belongs in the refusal message and
  in [docs/teamspeak6-findings.md](docs/teamspeak6-findings.md).
- **Track down the one-off tool schema difference.** Once, on 18 September 2026, a regenerated
  snapshot came out with `ts_file_download` carrying its `IProgress` parameter in the input schema,
  which the server does not really serve. Twenty regenerations on 21 September 2026 were
  byte-identical, so it stands at one run in about thirty and the cause is still unknown. A race
  between the test classes that each build a host is the suspect, since they run in parallel, but
  putting them into one xunit collection would be a fix for something nobody has seen twice. Until
  then the guard is the one in [CONTRIBUTING.md](CONTRIBUTING.md): read the diff before committing a
  regenerated snapshot. One further sighting may belong here: a run of the suite on
  20 September 2026 failed a single test of 686 and was read through a pipe that kept only the
  summary, so the test has no name; 23 runs afterwards were green.
