// Serves the library's static assets the way a Blazor application does, so the tests exercise the same
// URLs and MIME types production will use.
//
// Deliberately does not send COOP/COEP: the default tier must work without cross-origin isolation, and
// a test server that granted it would hide the day we accidentally depend on it.

import { createServer } from 'node:http';
import { createReadStream } from 'node:fs';
import { stat } from 'node:fs/promises';
import { extname, join, normalize, sep } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = fileURLToPath(new URL('.', import.meta.url));
const port = Number(process.env.PORT ?? 5199);

// Mirrors the RCL layout: the package's wwwroot is served under _content/<package>.
const roots = [
  { prefix: '/_content/BlazorSqlite.Js/', directory: join(here, '..', '..', 'src', 'BlazorSqlite.Js', 'wwwroot') },
  { prefix: '/_content/BlazorSqlite.Storage.Opfs/', directory: join(here, '..', '..', 'src', 'BlazorSqlite.Storage.Opfs', 'wwwroot') },
  { prefix: '/_content/BlazorSqlite.Storage.IndexedDb/', directory: join(here, '..', '..', 'src', 'BlazorSqlite.Storage.IndexedDb', 'wwwroot') },
  { prefix: '/_content/BlazorSqlite.Storage.CacheStorage/', directory: join(here, '..', '..', 'src', 'BlazorSqlite.Storage.CacheStorage', 'wwwroot') },
  { prefix: '/', directory: join(here, 'public') },
];

const MIME = {
  '.html': 'text/html; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.mjs': 'text/javascript; charset=utf-8',
  '.json': 'application/json; charset=utf-8',
  '.wasm': 'application/wasm',
  '.txt': 'text/plain; charset=utf-8',
};

function resolve(urlPath) {
  for (const { prefix, directory } of roots) {
    if (!urlPath.startsWith(prefix)) {
      continue;
    }

    const relative = normalize(urlPath.slice(prefix.length) || 'index.html');

    // Refuse to escape the root: normalize collapses '..' but can still leave a leading traversal.
    if (relative.startsWith('..') || relative.startsWith(sep)) {
      return null;
    }

    return join(directory, relative);
  }

  return null;
}

const server = createServer(async (request, response) => {
  const urlPath = decodeURIComponent(new URL(request.url, 'http://localhost').pathname);
  const file = resolve(urlPath);

  if (!file) {
    response.writeHead(404).end('Not found');
    return;
  }

  try {
    const info = await stat(file);
    if (!info.isFile()) {
      response.writeHead(404).end('Not found');
      return;
    }

    response.writeHead(200, {
      'Content-Type': MIME[extname(file)] ?? 'application/octet-stream',
      'Content-Length': info.size,
      'Cache-Control': 'no-store',
    });

    createReadStream(file).pipe(response);
  } catch {
    response.writeHead(404).end(`Not found: ${urlPath}`);
  }
});

server.listen(port, () => console.log(`Serving BlazorSqlite assets on http://localhost:${port}`));
