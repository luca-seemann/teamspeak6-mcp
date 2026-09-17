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
- Over SSH it checks the server's host key before sending the password: against a pinned
  fingerprint, or against the key remembered on the first connection. Connecting despite a changed
  key is a vulnerability. Two limits are accepted: the very first connection to an unpinned server
  is trusted without proof, and the WebQuery over plain `http://` is not encrypted at all.
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
- Much of what the tools return is written by anyone who can connect to the TeamSpeak server:
  nicknames, channel names and descriptions, chat, offline messages, complaints, file contents.
  The server instructions and those tools' descriptions tell the model to treat such text as data,
  never as instructions. That lowers the risk of prompt injection but cannot rule it out, which is
  one more reason to keep a profile at the lowest safety level that does the job. A tool that
  returns user-written text without that notice is a bug worth reporting; a model that follows
  injected text anyway is not a vulnerability of this server.
