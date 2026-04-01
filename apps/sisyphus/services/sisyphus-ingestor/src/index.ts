import pino from "pino";
import { app } from "./app.js";
import { connectDb, closeDb } from "./db.js";
import { ensureIndexes } from "./models/upload-history.js";

const logger = pino({ name: "ingestor" });

const PORT = parseInt(process.env["PORT"] ?? "8080", 10);

async function start() {
  const db = await connectDb();
  await ensureIndexes(db);
  logger.info("MongoDB connected and indexes ensured");

  const server = app.listen(PORT, () => {
    logger.info({ port: PORT }, "Ingestor listening");
  });

  const shutdown = async () => {
    logger.info("Shutting down...");
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
