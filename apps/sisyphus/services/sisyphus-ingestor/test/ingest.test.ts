import { describe, it, expect, beforeAll, afterAll, vi } from "vitest";
import request from "supertest";
import { MongoMemoryServer } from "mongodb-memory-server";

// Mock chrono-graph client before importing anything that uses it
vi.mock("../src/chrono-graph-client.js", () => ({
  writeNodes: vi.fn().mockResolvedValue({
    nodeIds: ["node-1", "node-2"],
  }),
  writeEdges: vi.fn().mockResolvedValue({
    edgeIds: ["edge-1"],
  }),
}));

let mongod: MongoMemoryServer;

beforeAll(async () => {
  mongod = await MongoMemoryServer.create();
  process.env["MONGO_URI"] = mongod.getUri();
  process.env["DB_NAME"] = "test_sisyphus_ingestor";
});

afterAll(async () => {
  const { server } = await import("../src/index.js");
  server.close();
  await mongod.stop();
});

async function getApp() {
  const { app } = await import("../src/index.js");
  return app;
}

describe("GET /health", () => {
  it("returns ok", async () => {
    const app = await getApp();
    const res = await request(app).get("/health");
    expect(res.status).toBe(200);
    expect(res.body.status).toBe("ok");
    expect(res.body.service).toBe("sisyphus-ingestor");
  });
});

describe("POST /ingest", () => {
  it("ingests nodes and edges successfully", async () => {
    const app = await getApp();
    const res = await request(app)
      .post("/ingest")
      .send({
        nodes: [
          { type: "theorem", abstract: "A theorem", body: "Proof..." },
          { type: "lemma", abstract: "A lemma", body: "By..." },
        ],
        edges: [
          { source: "n1", target: "n2", edge_type: "proves" },
        ],
      });

    expect(res.status).toBe(200);
    expect(res.body.uploadId).toBeDefined();
    expect(res.body.nodeIds).toEqual(["node-1", "node-2"]);
    expect(res.body.edgeIds).toEqual(["edge-1"]);
  });

  it("returns 400 for empty nodes", async () => {
    const app = await getApp();
    const res = await request(app)
      .post("/ingest")
      .send({ nodes: [] });

    expect(res.status).toBe(400);
  });

  it("returns 400 for missing nodes", async () => {
    const app = await getApp();
    const res = await request(app)
      .post("/ingest")
      .send({ edges: [] });

    expect(res.status).toBe(400);
  });

  it("ingests nodes without edges", async () => {
    const app = await getApp();
    const res = await request(app)
      .post("/ingest")
      .send({
        nodes: [{ type: "definition", abstract: "A def", body: "Define..." }],
      });

    expect(res.status).toBe(200);
    expect(res.body.nodeIds).toBeDefined();
    expect(res.body.edgeIds).toEqual([]);
  });
});

describe("GET /history", () => {
  it("returns paginated upload history", async () => {
    const app = await getApp();
    const res = await request(app).get("/history");

    expect(res.status).toBe(200);
    expect(res.body.records).toBeInstanceOf(Array);
    expect(res.body.total).toBeGreaterThanOrEqual(2); // From ingest tests above
    expect(res.body.page).toBe(1);
  });
});

describe("GET /history/:uploadId", () => {
  it("returns details for a specific upload", async () => {
    const app = await getApp();

    // First create an upload
    const ingestRes = await request(app)
      .post("/ingest")
      .send({
        nodes: [{ type: "theorem", abstract: "Test", body: "Test" }],
      });

    const uploadId = ingestRes.body.uploadId;

    const res = await request(app).get(`/history/${uploadId}`);
    expect(res.status).toBe(200);
    expect(res.body.id).toBe(uploadId);
    expect(res.body.nodeCount).toBe(1);
    expect(res.body.status).toBe("success");
  });

  it("returns 404 for unknown upload id", async () => {
    const app = await getApp();
    const res = await request(app).get("/history/nonexistent");
    expect(res.status).toBe(404);
  });
});
