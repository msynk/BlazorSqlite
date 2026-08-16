# BlazorSqlite.Benchmarks

Manual harness for the §2 reference targets. Not a CI gate.

Serve the Playwright test server (`tests/BlazorSqlite.Browser.Tests/server.js` maps `/_content/...`), then open `/benchmarks.html`.

What to record, per provider:

- Cold open after engine init
- Simple indexed `SELECT`
- 10-row insert batch

Compare OPFS vs IndexedDB vs Cache Storage. Numbers are for humans, not for a red build.
