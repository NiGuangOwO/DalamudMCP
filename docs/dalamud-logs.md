# Dalamud runtime logs

`get_dalamud_log` reads Dalamud and plugin diagnostic logs, independently of the
game chat captured by `get_chat_log`. It is read-only and works at the login screen.
The plugin passively subscribes to the installed SDK's managed console log sink;
it does not change the logging level, read arbitrary files, or install native hooks.
If the internal SDK event shape changes, `available=false` and `reason` explain the
failure without preventing the rest of the MCP plugin from loading.

```json
{"name":"get_dalamud_log","arguments":{"count":50,"minLevel":"Warning"}}
```

Optional `source` filters the Serilog `SourceContext` substring; a missing source
remains empty rather than being attributed to a plugin. `contains` searches the
rendered message and exception, case-insensitively. Both filters allow 256 characters.
Levels are `Verbose`, `Debug`, `Information`, `Warning`, `Error`, `Fatal`.

For live polling, pass the response's `streamId` and **`nextAfterId`** as `afterId`
on the next call. Drain `hasMore` pages before waiting for new logs. `lastId` is a
buffer watermark, not a paging cursor: using it can skip unreturned entries.
Filtered-out entries advance the cursor. Filters should stay the same across a
polling sequence; restart from `afterId=0` to query retained history differently.
Concurrent readers have independent cursors and never consume one another's logs.

The buffer retains 1000 entries since plugin load, including while the listener is
stopped. Overwritten entries cannot be recovered; `historyLost=true` reports when
a nonzero cursor fell behind retention. An initial read starts at the oldest
retained entry. There is no historical-file backfill. Plugin reload resets the
buffer and stream identity; a mismatched `streamId` is rejected. Start again by
omitting both cursor fields. Preserve both fields to detect reload reliably.

`count` is 1–200 (default 100). Each message/exception is clipped to 4096 characters,
source to 256; `truncated` marks affected entries. Pages have a 64000-character
entry JSON budget plus array separators and metadata. Entries include monotonic
`id`, original Unix-ms `timestamp`, `level`, `source`, `message` and `exception`.
Logs below Dalamud's configured logging level are not available. An empty result
does not prove a plugin emitted no logs. Log callbacks never write new logs, and
unloading unsubscribes the event handler.

Offline validation: `dotnet run --project tests/DalamudLogTest` exercises the real
installed SDK event, filtering, pagination, concurrency, retention, size limits,
reload guards and disposal. `PluginLoadTest` also exercises plugin registration.
