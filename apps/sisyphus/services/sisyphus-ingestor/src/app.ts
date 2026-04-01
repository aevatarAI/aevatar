import express, { type Request, type Response, type NextFunction } from "express";
import pino from "pino";
import pinoHttp from "pino-http";
import swaggerUi from "swagger-ui-express";
import { ValidateError } from "tsoa";
import { RegisterRoutes } from "./generated/routes.js";
import { readFileSync } from "node:fs";
import { join, dirname } from "node:path";
import { fileURLToPath } from "node:url";

const logger = pino({ name: "ingestor" });

const __dirname = dirname(fileURLToPath(import.meta.url));
const swaggerSpec = JSON.parse(
  readFileSync(join(__dirname, "generated", "swagger.json"), "utf-8")
);

const app = express();

app.use(express.json({ limit: "10mb" }));
app.use(pinoHttp({ logger }));

app.get("/openapi.json", (_req: Request, res: Response) => {
  res.json(swaggerSpec);
});

app.use("/docs", swaggerUi.serve, swaggerUi.setup(swaggerSpec));

RegisterRoutes(app);

app.use((err: unknown, _req: Request, res: Response, next: NextFunction) => {
  if (err instanceof ValidateError) {
    logger.warn({ fields: err.fields }, "Validation error");
    res.status(400).json({ error: "Validation failed", details: err.fields });
    return;
  }
  next(err);
});

// Catch-all error handler
app.use((err: unknown, _req: Request, res: Response, _next: NextFunction) => {
  logger.error({ err }, "Unhandled error");
  res.status(500).json({ error: "Internal server error" });
});

export { app };
