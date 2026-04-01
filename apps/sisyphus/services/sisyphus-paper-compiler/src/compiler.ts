import { execFile } from "node:child_process";
import { mkdtemp, writeFile, readFile, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import pino from "pino";

const logger = pino({ name: "paper-compiler:compiler" });

const COMPILE_TIMEOUT_MS = 300_000; // 5 min for large documents
const MAX_CONCURRENT = 3;

// Simple counting semaphore for tectonic concurrency
let activeCompilations = 0;
const waitQueue: Array<() => void> = [];

async function acquireSemaphore(): Promise<void> {
  if (activeCompilations < MAX_CONCURRENT) {
    activeCompilations++;
    return;
  }
  return new Promise<void>((resolve) => {
    waitQueue.push(() => {
      activeCompilations++;
      resolve();
    });
  });
}

function releaseSemaphore(): void {
  activeCompilations--;
  const next = waitQueue.shift();
  if (next) next();
}

export async function compilePdf(latex: string): Promise<Buffer> {
  await acquireSemaphore();

  const tempDir = await mkdtemp(join(tmpdir(), "sisyphus-paper-"));

  try {
    const texPath = join(tempDir, "paper.tex");
    const pdfPath = join(tempDir, "paper.pdf");

    await writeFile(texPath, latex, "utf-8");

    logger.info({ tempDir, latexLength: latex.length, activeCompilations }, "Compiling LaTeX with tectonic");

    await new Promise<void>((resolve, reject) => {
      const proc = execFile(
        "tectonic",
        ["--untrusted", "-Z", "continue-on-errors", texPath],
        { cwd: tempDir, timeout: COMPILE_TIMEOUT_MS },
        (error, _stdout, stderr) => {
          if (error) {
            // Fatal process errors: binary not found, signal kills, spawn failures
            const code = (error as NodeJS.ErrnoException).code;
            if (code === "ENOENT") {
              reject(new Error("tectonic binary not found — is it installed?"));
              return;
            }
            if (proc.signalCode) {
              reject(new Error(`Tectonic killed by signal ${proc.signalCode}`));
              return;
            }

            // Non-fatal: tectonic exited with error code but may have produced a PDF
            logger.warn({ exitCode: proc.exitCode, stderr: stderr?.slice(-2000) }, "Tectonic exited with error");
          }
          resolve();
        }
      );
    });

    let pdfBytes: Buffer;
    try {
      pdfBytes = await readFile(pdfPath);
    } catch {
      logger.error("Tectonic did not produce a PDF file");
      throw new Error("Tectonic compilation failed — no PDF produced");
    }

    logger.info({ pdfSize: pdfBytes.length }, "PDF compiled successfully");
    return pdfBytes;
  } finally {
    releaseSemaphore();
    try {
      await rm(tempDir, { recursive: true });
    } catch (err) {
      logger.warn({ err, tempDir }, "Failed to clean up temp directory");
    }
  }
}

/**
 * Check if tectonic is available on the system.
 */
export async function isTectonicAvailable(): Promise<boolean> {
  return new Promise((resolve) => {
    execFile("tectonic", ["--version"], (error) => {
      resolve(!error);
    });
  });
}
