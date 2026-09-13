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
