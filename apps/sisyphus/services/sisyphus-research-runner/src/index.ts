import pino from "pino";
import { app } from "./app.js";
import { connectDb, closeDb } from "./db.js";
import { ensureSessionIndexes } from "./models/session.js";
import { ensureEventLogIndexes } from "./models/event-log.js";
import { recoverStaleSessions, startVerifyCron, stopVerifyCron } from "./session-manager.js";
import { initWebSocket } from "./ws-server.js";

const logger = pino({ name: "runner" });

const PORT = parseInt(process.env["PORT"] ?? "8080", 10);

async function start() {
  const db = await connectDb();
  await Promise.all([
    ensureSessionIndexes(db),
    ensureEventLogIndexes(db),
  ]);
  logger.info("MongoDB connected and indexes ensured");

  // Crash recovery
  await recoverStaleSessions();

  const server = app.listen(PORT, () => {
    logger.info({ port: PORT }, "Runner listening");
  });

  // Initialize WebSocket server on the same HTTP server
  initWebSocket(server);

  // Start verify cron job
  startVerifyCron();

  const shutdown = async () => {
    logger.info("Shutting down...");
    stopVerifyCron();
    server.close();
    await closeDb();
    process.exit(0);
  };

  process.on("SIGTERM", shutdown);
  process.on("SIGINT", shutdown);

  return { app, server };
}

const { server } = await start();

export { app, server };
