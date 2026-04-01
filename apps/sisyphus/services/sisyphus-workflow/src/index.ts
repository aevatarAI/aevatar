import pino from "pino";
import { app } from "./app.js";
import { connectDb, closeDb } from "./db.js";
import { ensureWorkflowIndexes } from "./models/workflow-definition.js";
import { ensureConnectorIndexes } from "./models/connector-definition.js";
import { ensureCompiledArtifactIndexes } from "./models/compiled-artifact.js";

const logger = pino({ name: "workflow" });

const PORT = parseInt(process.env["PORT"] ?? "8080", 10);

async function start() {
  const db = await connectDb();
  await Promise.all([
    ensureWorkflowIndexes(db),
    ensureConnectorIndexes(db),
    ensureCompiledArtifactIndexes(db),
  ]);
  logger.info("MongoDB connected and indexes ensured");

  const server = app.listen(PORT, () => {
    logger.info({ port: PORT }, "Workflow service listening");
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
