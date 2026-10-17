// Launches Playwright with the environment the opt-in suites need.
//
// The browser matrix and the soak suite are both switched on by environment variables, which npm
// scripts cannot set portably: `VAR=value cmd` is shell syntax that neither cmd.exe nor PowerShell
// understands. Setting them here keeps `npm run test:all` and `npm run soak` working on every
// machine without adding a dependency for it.

import { spawn } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const flags = new Set();
const passThrough = [];

for (const argument of process.argv.slice(2)) {
  if (argument === '--all' || argument === '--soak') {
    flags.add(argument);
  } else {
    passThrough.push(argument);
  }
}

const env = { ...process.env };

if (flags.has('--all')) {
  env.BLAZORSQLITE_BROWSERS = 'all';
}

if (flags.has('--soak')) {
  env.BLAZORSQLITE_SOAK = '1';
}

// Playwright's own CLI file, run by this Node rather than through npx: the `.bin` shim is a .cmd on
// Windows and needs a shell, and spawning with a shell would mangle any argument containing a space.
const cli = fileURLToPath(new URL('../node_modules/@playwright/test/cli.js', import.meta.url));

const child = spawn(process.execPath, [cli, 'test', ...passThrough], {
  cwd: fileURLToPath(new URL('..', import.meta.url)),
  env,
  stdio: 'inherit',
});

child.on('exit', (code, signal) => {
  process.exit(signal ? 1 : code ?? 1);
});
