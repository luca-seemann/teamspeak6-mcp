# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- Project scaffolding: solution layout, central package management, CI on GitHub Actions,
  container images, and the contribution documents.
- `IQueryTransport`, the transport-agnostic contract both the SSH and the WebQuery client implement.
- `FakeQueryTransport`, an in-memory stand-in used to test without a live TeamSpeak server.
- The protocol core: value escaping, a parser for each transport, the command serialiser, and the
  status codes that drive control flow.
- A captured ServerQuery command reference for 6.0.0-beta12.1, plus raw response fixtures from both
  transports that the parser tests run against.
- `QueryRecord`, giving typed access to response fields and decoding TeamSpeak's conventions in one
  place: booleans as `1`/`0`, times as Unix seconds, and a zero timestamp meaning never.
- `SshQueryTransport` and `HttpQueryTransport`, both verified against a live server, with virtual
  server selection behind one method despite the two interfaces expressing it very differently.
- `FloodGuard` and `ConnectionThrottle`, which pace commands and connections so the server does not
  block the client, plus `ReconnectPolicy` for backoff with jitter.
- Connection profiles bound from configuration, with `TSMCP_`-prefixed environment variables
  overriding the file so credentials need not be written to disk.
- An integration suite that runs against a live server when `TSMCP_TEST_HOST` is set and skips
  itself otherwise.
- `QueryConnectionManager`, which opens one connection per profile on first use, shares it with
  every caller, and resolves `PreferredTransport.Auto` to SSH whenever a password is configured.
- Safety levels per profile, overriding the global `TeamSpeak:Safety` in either direction and
  enforced on the server before any command is sent.
- The first ten tools: `ts_profiles_list`, `ts_whoami`, `ts_instance_info`, `ts_vserver_list`,
  `ts_vserver_info`, `ts_channel_list`, `ts_channel_info`, `ts_client_list`, `ts_client_info` and
  `ts_query_raw`, with structured output and MCP annotations.
- A command catalog giving `ts_query_raw` the safety level of each of the 143 reference commands,
  and a snapshot test that pins the tool schemas.
- The full read surface, 26 further tools. `ts_perm_effective` explains a client's effective
  permissions in a channel layer by layer. `ts_client_resolve` connects session id, database id and
  unique identity. `ts_health_report` turns slot usage, packet loss and ping into findings. Tools for
  known identities, groups, assigned permissions, bans, complaints, privilege keys, API keys, query
  logins, offline messages, custom properties and the server log complete the set.
- MCP resources for profiles, the permission catalog, and each virtual server's info, channels,
  clients and groups.
- The write and destructive surface, 35 tools, for 71 in all. The tools cover virtual servers and
  instance settings, channels, moving, kicking, poking and messaging people, groups and memberships,
  granting and revoking permissions, bans, privilege keys, temporary passwords, custom properties,
  API keys, query logins and log entries. Each action takes its safety level from the command
  catalog. Deleting a virtual server, deploying a snapshot and resetting permissions also require the
  virtual server's exact name. Channel messages move the query session into the channel and back as
  one uninterrupted sequence on the session, which needs a profile that uses SSH.
- `IQueryTransport.RunExclusiveAsync`, which runs several commands without any other caller's
  command in between, and `HoldsSession`, which says whether the interface has a session to protect.
- A readable explanation for `1541 invalid parameter size`, which the server returns for names that
  are too long.

### Fixed

- `docker/docker-compose.yml` configured the MCP container with setting names that do not bind, so
  the container started without a profile.
- After a reconnect, the SSH server's greeting was read as the first lines of the next command's
  response. The greeting is now read and discarded while the session is being opened.
- A command abandoned after it was sent, by a timeout or a cancellation, could have its late answer
  taken for the next command's. Such a session is now replaced before anything else is sent on it,
  and a replaced session's reader stops at once instead of passing on buffered lines.
- A channel message moved the session and sent the message as separate commands. Another call
  could select a different virtual server in between, so the message could land in the wrong
  channel while the tool reported success.
- `gm` was treated as belonging to a virtual server, so a message to every server failed when the
  default virtual server could not be selected.
- The API key commands used whatever virtual server an earlier command had left selected. They now
  address the virtual server given, or the profile's default.
- The SSH transport assumed its virtual server was still selected after stopping or deleting it,
  after a permission reset and after a snapshot deployment.
- Kicking or banning a client did not check whether it was this server's own query session.
- `servercreate` needed only `Write`, although it returns a key with full control of the new server.
  It now needs `Destructive`.
- `ts_perm_set` read the permission catalog before checking the safety level.
- Empty property values and an unreadable server name passed validation.

### Changed (review of phase 6)

- `ts_client_edit` no longer offers `isTalker`: TeamSpeak 6 refuses `client_is_talker=1` with
  `1538 invalid parameter`, even in a channel that needs talk power.
- `ts_message_send` takes a `channelPassword` for password-protected channels.
- `ts_apikey_list` and `ts_apikey_manage` take a `virtualServerId`.

### Changed

- The virtual server now travels with each `QueryCommand` instead of being selected on the
  transport. `IQueryTransport.SelectVirtualServerAsync` and `VirtualServerId` are gone; the SSH
  transport sends `use` only when needed, under the same lock as the command, so concurrent callers
  can no longer run on each other's selection.

### Notes

- Several deployment surprises are documented in [README.md](README.md) and
  [reference/README.md](reference/README.md), including which client address the server actually
  sees behind Docker, and why `TESTINGPLATFORM_TELEMETRY_OPTOUT` matters when running the tests.
