# Security policy

## Reporting a vulnerability

Please do not open a public issue for a security problem.

Report it through GitHub's private vulnerability reporting, under
[Security → Report a vulnerability](https://github.com/luca-seemann/teamspeak6-mcp/security/advisories/new)
in this repository. That channel is private between you and the maintainers.

What helps:

- what an attacker can reach or do, and what they need to start
- the smallest reproduction you have, ideally a tool call or command sequence
- the version or commit, your TeamSpeak 6 server version, and which query interface was in use

Please do not include a query password, an API key, a privilege key or a server snapshot in the
report. Describe where the value came from instead; a maintainer will reproduce it with their own
server.

Expect an acknowledgement within a few days. This is a spare-time project, so a fix may take
longer; you will be told either way, and credited in the release notes unless you prefer otherwise.

## Which versions get fixes

The project is pre-release. Fixes go into the latest version on `master`, and there are no
backports.

## What this server can do

Worth knowing when judging a report:

- It holds TeamSpeak query credentials, as `serveradmin` passwords or WebQuery API keys, taken from
  configuration or the environment.
- With those credentials, and a high enough safety level, it can change or destroy everything on a
  TeamSpeak server: channels, groups, permissions, bans, stored files.
- **Safety levels are the main defence.** A server starts at `ReadOnly`, `Write` and `Destructive`
  have to be granted per profile, and the check runs before any command is sent. A tool that
  changes something while its profile allows less is a vulnerability, and so is a command that
  reaches TeamSpeak without being classified.
- Over Streamable HTTP the endpoint refuses a foreign `Origin` with 403, accepts only loopback host
  names unless configured otherwise, and requires a bearer token once one is set. Without a token it
  will not start on anything but a loopback address. A request that reaches a tool without meeting
  these checks, such as a browser page through DNS rebinding, is a vulnerability.
- File tools may read and write local files, but only inside the directory configured as
  `TeamSpeak:FileTransfer:LocalDirectory`. A path that escapes that directory, including through a
  symbolic link, is a vulnerability.
- The MCP client, and therefore the model, chooses tool arguments. Assume the model can be talked
  into calling anything; the safety level, not the model's judgement, is what has to hold.
