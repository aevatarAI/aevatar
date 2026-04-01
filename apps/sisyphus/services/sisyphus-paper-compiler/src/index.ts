import pino from "pino";
import { app } from "./app.js";
import { connectDb, closeDb } from "./db.js";
import { ensureCompileIndexes } from "./models/compile-history.js";
import { ensureBucket } from "./storage-client.js";

const logger = pino({ name: "paper-compiler" });

const PORT = parseInt(process.env["PORT"] ?? "8080", 10);

async function start() {
  const db = await connectDb();
  await ensureCompileIndexes(db);
  logger.info("MongoDB connected and indexes ensured");

  // Best-effort bucket creation
  await ensureBucket().catch(() => {});

  const server = app.listen(PORT, () => {
    logger.info({ port: PORT }, "Paper compiler listening");
  });

  const shutdown = async () => {
    logger.info("Shutting down...");
    server.close();
    await closeDb();
    process.exit(0);
  };
  process.on("SIGTERM", shutdown);
  process.on("SIGINT", shutdown);
}

await start();
