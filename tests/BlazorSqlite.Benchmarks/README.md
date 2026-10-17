# BlazorSqlite.Benchmarks

Manual harness. Not a CI gate.

Serve the browser-test server, which maps `/_content/...` the way a Blazor app does:

```
cd tests/BlazorSqlite.Browser.Tests
node server.js
```

Then open <http://localhost:5199/benchmarks.html> and, in the console:

```js
await run('memory')
await run('opfs')
await run('indexeddb')
await run('cache')
```

Each run reports milliseconds for: cold open after engine init, a 100-row insert batch in one round trip, 1 000 repeated parameterised inserts inside one transaction (exercises the statement cache), a 1 000-row read on the binary path and on the tagged JavaScript path, and an indexed point read.

Numbers are for humans comparing backends and encodings on one machine, not for a red build.
