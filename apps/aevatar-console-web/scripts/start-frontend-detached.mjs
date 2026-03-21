import { openSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { spawn } from 'node:child_process';

const scriptDir = dirname(fileURLToPath(import.meta.url));
const appDir = resolve(scriptDir, '..');
const logFile = process.argv[2];

if (!logFile) {
  console.error('Usage: node scripts/start-frontend-detached.mjs <log-file>');
  process.exit(1);
}

const outputFd = openSync(logFile, 'a');
const child = spawn('pnpm', ['dev'], {
  cwd: appDir,
  detached: true,
  env: {
    ...process.env,
    PORT:
      process.env.PORT ||
      process.env.AEVATAR_CONSOLE_FRONTEND_PORT ||
      '5173',
    UMI_ENV: 'dev',
    MOCK: 'none',
  },
  stdio: ['ignore', outputFd, outputFd],
});

child.unref();
console.log(String(child.pid));
