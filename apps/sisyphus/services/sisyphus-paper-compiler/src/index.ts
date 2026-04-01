import pino from "pino";
import { app } from "./app.js";

const logger = pino({ name: "paper-compiler" });

const PORT = parseInt(process.env["PORT"] ?? "8080", 10);

const server = app.listen(PORT, () => {
  logger.info({ port: PORT }, "Paper compiler listening");
});

export { app, server };
