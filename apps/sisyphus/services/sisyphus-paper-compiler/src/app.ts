import express, { type Request, type Response, type NextFunction } from "express";
import pino from "pino";
import pinoHttp from "pino-http";
import swaggerUi from "swagger-ui-express";
import { ValidateError } from "tsoa";
import { RegisterRoutes } from "./generated/routes.js";
import { readFileSync } from "node:fs";
import { join, dirname } from "node:path";
import { fileURLToPath } from "node:url";
import { randomUUID } from "node:crypto";
import { getDb } from "./db.js";
import { getCompileCollection, type CompileRecord } from "./models/compile-history.js";
import { topologicalSort } from "./topological-sort.js";
import { generateLatex } from "./generator.js";
import { compilePdf } from "./compiler.js";
import { uploadFile, getPresignedUrl } from "./storage-client.js";
import type { GraphNode, GraphEdge } from "./types.js";

const logger = pino({ name: "paper-compiler" });

const __dirname = dirname(fileURLToPath(import.meta.url));
const swaggerSpec = JSON.parse(
  readFileSync(join(__dirname, "generated", "swagger.json"), "utf-8")
);

const app = express();

app.use(express.json({ limit: "50mb" }));
app.use(pinoHttp({ logger }));

app.get("/openapi.json", (_req: Request, res: Response) => {
  res.json(swaggerSpec);
});
app.use("/docs", swaggerUi.serve, swaggerUi.setup(swaggerSpec));

RegisterRoutes(app);

// --- Active compile jobs (for abort support) ---
const activeJobs = new Map<string, AbortController>();

// --- SSE Compile Endpoint ---
const GRAPH_SERVICE_URL = process.env["GRAPH_SERVICE_URL"] ?? "http://chrono-graph.chronoai-platform:8080";
const GRAPH_ID = process.env["GRAPH_ID"] ?? "8f917b59-ebfd-4a8b-912f-21bd262a5514";
const FETCH_BATCH = 200;

import { sanitizeBody, isBodyBroken, stripToPlainText } from "./sanitizer.js";

app.post("/compile-stream", async (req: Request, res: Response) => {
  const body = req.body as {
    nodeIds?: string[];
    edges?: Array<{ source: string; target: string; edge_type?: string; type?: string }>;
    filterName?: string;
    graphId?: string;
  };

  const nodeIds = body.nodeIds ?? [];
  if (nodeIds.length === 0) {
    res.status(400).json({ error: "Request must include a non-empty 'nodeIds' array" });
    return;
  }
  if (nodeIds.length > 50_000) {
    res.status(400).json({ error: `Node count ${nodeIds.length} exceeds maximum of 50000` });
    return;
  }

  // SSE headers
  res.setHeader("Content-Type", "text/event-stream");
  res.setHeader("Cache-Control", "no-cache");
  res.setHeader("Connection", "keep-alive");
  res.flushHeaders();

  const compileId = randomUUID();
  const filterName = body.filterName ?? "unknown";
  const graphId = body.graphId ?? GRAPH_ID;
  const edges: GraphEdge[] = (body.edges ?? []).map((e) => ({
    source: e.source,
    target: e.target,
    edge_type: e.edge_type ?? e.type ?? "references",
  }));
  const now = new Date();
  const ts = `${now.getFullYear()}_${String(now.getMonth() + 1).padStart(2, "0")}_${String(now.getDate()).padStart(2, "0")}__${String(now.getHours()).padStart(2, "0")}_${String(now.getMinutes()).padStart(2, "0")}`;
  const baseFileName = `sisyphus_paper_${filterName}_${ts}`;

  const abortController = new AbortController();
  activeJobs.set(compileId, abortController);

  function send(event: string, data: Record<string, unknown>) {
    if (res.writableEnded) return;
    res.write(`event: ${event}\ndata: ${JSON.stringify(data)}\n\n`);
  }

  // Yield event loop so SSE flushes to client before continuing
  const flush = () => new Promise<void>((r) => setTimeout(r, 0));

  const db = getDb();
  const col = getCompileCollection(db);
  await col.insertOne({
    id: compileId, filterName, nodeCount: nodeIds.length, edgeCount: edges.length,
    status: "running", startedAt: now.toISOString(),
  });

  send("start", { compileId, filterName, nodeCount: nodeIds.length, edgeCount: edges.length });

  req.on("close", () => {
    if (activeJobs.has(compileId)) {
      abortController.abort();
      activeJobs.delete(compileId);
      col.updateOne({ id: compileId }, { $set: { status: "aborted", completedAt: new Date().toISOString() } }).catch(() => {});
    }
  });

  try {
    // Step 1: Fetch full node data from chrono-graph in batches
    send("progress", { step: 1, label: "Fetching node details from graph", status: "running", detail: `0 / ${nodeIds.length}` });
    const fullNodes: GraphNode[] = [];
    for (let i = 0; i < nodeIds.length; i += FETCH_BATCH) {
      if (abortController.signal.aborted) throw new Error("Aborted");
      const batch = nodeIds.slice(i, i + FETCH_BATCH);
      // Fetch each node by ID (chrono-graph GET /nodes/{nodeId})
      const batchPromises = batch.map(async (nid) => {
        try {
          const resp = await fetch(`${GRAPH_SERVICE_URL}/api/graphs/${graphId}/nodes/${nid}`, {
            signal: AbortSignal.timeout(10000),
          });
          if (!resp.ok) return null;
          const data = await resp.json() as Record<string, unknown>;
          return {
            id: data.id as string,
            type: data.type as string,
            properties: data as Record<string, unknown>,
          } as GraphNode;
        } catch { return null; }
      });
      const results = await Promise.all(batchPromises);
      for (const n of results) { if (n) fullNodes.push(n); }
      const fetched = Math.min(i + FETCH_BATCH, nodeIds.length);
      send("progress", { step: 1, label: "Fetching node details from graph", status: "running", detail: `${fetched} / ${nodeIds.length} nodes fetched` });
    }
    send("progress", { step: 1, label: "Fetching node details from graph", status: "done", detail: `${fullNodes.length} nodes fetched` });
    await flush();

    // Step 2: Sanitize LaTeX content with per-batch progress
    send("progress", { step: 2, label: "Sanitizing LaTeX content", status: "running", detail: `0 / ${fullNodes.length}` });
    await flush();
    let fixedCount = 0;
    for (let i = 0; i < fullNodes.length; i++) {
      if (abortController.signal.aborted) throw new Error("Aborted");
      const node = fullNodes[i];
      const rawBody = typeof node.properties.body === "string" ? node.properties.body : "";
      const rawAbstract = typeof node.properties.abstract === "string" ? node.properties.abstract : "";
      const sanBody = sanitizeBody(rawBody);
      const sanAbstract = sanitizeBody(rawAbstract);
      if (sanBody !== rawBody || sanAbstract !== rawAbstract) fixedCount++;
      node.properties = { ...node.properties, body: sanBody, abstract: sanAbstract };
      // Send progress every 500 nodes
      if ((i + 1) % 500 === 0 || i === fullNodes.length - 1) {
        send("progress", { step: 2, label: "Sanitizing LaTeX content", status: "running", detail: `${i + 1} / ${fullNodes.length} sanitized, ${fixedCount} fixed` });
        await flush();
      }
    }
    send("progress", { step: 2, label: "Sanitizing LaTeX content", status: "done", detail: `${fullNodes.length} sanitized, ${fixedCount} fixed` });
    await flush();

    // Step 3: Topological ordering
    if (abortController.signal.aborted) throw new Error("Aborted");
    send("progress", { step: 3, label: "Computing topological ordering", status: "running" });
    await flush();
    const sorted = topologicalSort(fullNodes, edges);
    send("progress", { step: 3, label: "Computing topological ordering", status: "done", detail: `Ordered ${sorted.length} nodes by ${edges.length} edges` });
    await flush();

    // Step 4: Generate LaTeX document
    if (abortController.signal.aborted) throw new Error("Aborted");
    send("progress", { step: 4, label: "Generating LaTeX document", status: "running" });
    await flush();
    const latex = generateLatex(sorted, edges);
    send("progress", { step: 4, label: "Generating LaTeX document", status: "done", detail: `${(latex.length / 1024).toFixed(0)} KB` });
    await flush();

    // Step 5: Compile PDF with tectonic (auto-fallback to plain text on failure)
    if (abortController.signal.aborted) throw new Error("Aborted");
    send("progress", { step: 5, label: "Compiling LaTeX to PDF (tectonic)", status: "running" });
    await flush();
    let pdfBytes: Buffer;
    let finalLatex = latex;
    try {
      pdfBytes = await compilePdf(latex);
      send("progress", { step: 5, label: "Compiling LaTeX to PDF (tectonic)", status: "done", detail: `${(pdfBytes.length / 1024).toFixed(0)} KB` });
    } catch (firstErr) {
      // Fallback: only strip broken bodies to plain text, keep good ones as-is
      const errMsg = firstErr instanceof Error ? firstErr.message : "Unknown error";
      send("progress", { step: 5, label: "Compiling LaTeX to PDF (tectonic)", status: "running", detail: `LaTeX errors detected, fixing broken nodes and retrying...` });
      await flush();
      logger.warn({ err: errMsg }, "First compile failed, stripping broken bodies to plain text");

      let strippedCount = 0;
      for (const node of sorted) {
        const body = typeof node.properties.body === "string" ? node.properties.body : "";
        const abstract = typeof node.properties.abstract === "string" ? node.properties.abstract : "";
        let changed = false;
        if (body && isBodyBroken(body)) {
          node.properties = { ...node.properties, body: stripToPlainText(body) };
          changed = true;
        }
        if (abstract && isBodyBroken(abstract)) {
          node.properties = { ...node.properties, abstract: stripToPlainText(abstract) };
          changed = true;
        }
        if (changed) strippedCount++;
      }
      send("progress", { step: 5, label: "Compiling LaTeX to PDF (tectonic)", status: "running", detail: `${strippedCount} broken nodes fixed, regenerating...` });
      await flush();
      finalLatex = generateLatex(sorted, edges);
      pdfBytes = await compilePdf(finalLatex);
      send("progress", { step: 5, label: "Compiling LaTeX to PDF (tectonic)", status: "done", detail: `${(pdfBytes.length / 1024).toFixed(0)} KB (plain text fallback)` });
    }
    await flush();

    // Step 6: Upload to storage
    if (abortController.signal.aborted) throw new Error("Aborted");
    send("progress", { step: 6, label: "Uploading to storage", status: "running" });
    await flush();
    const latexKey = `papers/${baseFileName}.tex`;
    const pdfKey = `papers/${baseFileName}.pdf`;
    let latexUploaded = false;
    let pdfUploaded = false;
    try { await uploadFile(latexKey, Buffer.from(finalLatex, "utf-8"), "text/x-tex"); latexUploaded = true; } catch (err) { logger.warn({ err: (err as Error).message }, "LaTeX upload failed"); }
    try { await uploadFile(pdfKey, pdfBytes, "application/pdf"); pdfUploaded = true; } catch (err) { logger.warn({ err: (err as Error).message }, "PDF upload failed"); }
    send("progress", { step: 6, label: "Uploading to storage", status: "done", detail: `${latexUploaded ? "LaTeX ✓" : "LaTeX ✗"}, ${pdfUploaded ? "PDF ✓" : "PDF ✗"}` });

    // Done
    await col.updateOne({ id: compileId }, {
      $set: {
        status: "completed", latexKey: latexUploaded ? latexKey : undefined, pdfKey: pdfUploaded ? pdfKey : undefined,
        latexFileName: `${baseFileName}.tex`, pdfFileName: `${baseFileName}.pdf`, completedAt: new Date().toISOString(),
      },
    });
    send("complete", {
      compileId, latexFileName: `${baseFileName}.tex`, pdfFileName: `${baseFileName}.pdf`,
      latexKey: latexUploaded ? latexKey : null, pdfKey: pdfUploaded ? pdfKey : null,
      pdfSize: pdfBytes.length, latexSize: finalLatex.length,
    });

  } catch (err) {
    const message = err instanceof Error ? err.message : "Unknown error";
    if (message === "Aborted") {
      send("aborted", { compileId });
    } else {
      logger.error({ compileId, err: message }, "Compile failed");
      send("error", { compileId, error: message });
      await col.updateOne({ id: compileId }, { $set: { status: "failed", error: message, completedAt: new Date().toISOString() } });
    }
  } finally {
    activeJobs.delete(compileId);
    res.end();
  }
});

// --- Abort Compile ---
app.post("/compile-stream/:compileId/abort", (req: Request, res: Response) => {
  const compileId = req.params.compileId as string;
  const controller = activeJobs.get(compileId);
  if (controller) {
    controller.abort();
    activeJobs.delete(compileId);
    res.json({ aborted: true });
  } else {
    res.status(404).json({ error: "Compile job not found or already finished" });
  }
});

// --- Compile History ---
app.get("/compile-history", async (_req: Request, res: Response) => {
  const db = getDb();
  const col = getCompileCollection(db);
  const records = await col.find().sort({ startedAt: -1 }).limit(50).toArray();
  // Strip MongoDB _id
  const items = records.map(({ _id, ...rest }) => rest);
  res.json({ items });
});

// --- Download (presigned URL redirect) ---
app.get("/compile-history/:compileId/download/:type", async (req: Request, res: Response) => {
  const compileId = req.params.compileId as string;
  const type = req.params.type as string;
  if (type !== "latex" && type !== "pdf") {
    res.status(400).json({ error: "Type must be 'latex' or 'pdf'" });
    return;
  }

  const db = getDb();
  const col = getCompileCollection(db);
  const record = await col.findOne({ id: compileId });
  if (!record) {
    res.status(404).json({ error: "Compile record not found" });
    return;
  }

  const key = type === "latex" ? record.latexKey : record.pdfKey;
  if (!key) {
    res.status(404).json({ error: `${type} file not available for this compile` });
    return;
  }

  try {
    const url = await getPresignedUrl(key);
    res.json({ url, fileName: type === "latex" ? record.latexFileName : record.pdfFileName });
  } catch (err) {
    res.status(502).json({ error: (err as Error).message });
  }
});

// Tsoa validation error handler
app.use((err: unknown, _req: Request, res: Response, next: NextFunction) => {
  if (err instanceof ValidateError) {
    logger.warn({ fields: err.fields }, "Validation error");
    res.status(400).json({ error: "Validation failed", details: err.fields });
    return;
  }
  next(err);
});

export { app };
