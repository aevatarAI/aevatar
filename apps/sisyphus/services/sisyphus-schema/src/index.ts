import pino from "pino";
import { app } from "./app.js";
import { connectDb, closeDb } from "./db.js";
import { ensureIndexes } from "./models/schema-definition.js";

const logger = pino({ name: "schema-validator" });

const PORT = parseInt(process.env["PORT"] ?? "8080", 10);

async function start() {
  try {
    const db = await connectDb();
    await ensureIndexes(db);
    logger.info("MongoDB connected and indexes ensured");
  } catch (err) {
    logger.warn({ err }, "MongoDB not available — schema CRUD endpoints will fail, validation-only mode");
  }

  const server = app.listen(PORT, () => {
    logger.info({ port: PORT }, "Schema validator listening");
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
