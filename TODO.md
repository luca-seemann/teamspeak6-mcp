# To do

Things decided or announced but deliberately not built yet: the list of work waiting for a trigger.

- **Server→client push for events.** Deferred on 15 September 2026. Delivery stays `ts_events_poll`
  and `ts_events_wait` for now. Push is feasible over stdio (`SendNotificationAsync`), impossible over
  stateless Streamable HTTP, and how Claude Code shows such notifications is undocumented.
- **ServerQuery guest login.** The next TeamSpeak 6 beta reportedly adds a way to log in to the
  ServerQuery as a guest. Once a server version with it is available, probe how it works, and support
  it in the profiles, as an alternative to `serveradmin` credentials and API keys.
