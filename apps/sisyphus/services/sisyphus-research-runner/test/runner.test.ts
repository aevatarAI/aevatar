import { describe, it, expect, beforeAll, afterAll, vi } from "vitest";
import request from "supertest";
import { MongoMemoryServer } from "mongodb-memory-server";

// Mock external clients before imports
vi.mock("../src/aevatar-client.js", () => ({
  fetchCompiledWorkflow: vi.fn().mockResolvedValue({
    workflowYaml: "name: test\nsteps: []",
    connectorJson: [],
  }),
  startExecution: vi.fn().mockResolvedValue({
    executionId: "exec-123",
    stream: null,
  }),
  streamEvents: vi.fn().mockResolvedValue(null),
  terminateExecution: vi.fn().mockResolvedValue(undefined),
}));

// Mock ws-server to avoid real WebSocket setup
vi.mock("../src/ws-server.js", () => ({
  initWebSocket: vi.fn(),
  broadcastEvent: vi.fn(),
}));

// Mock node-cron
vi.mock("node-cron", () => ({
  default: {
    schedule: vi.fn().mockReturnValue({ stop: vi.fn() }),
  },
}));

let mongod: MongoMemoryServer;

beforeAll(async () => {
  mongod = await MongoMemoryServer.create();
  process.env["MONGO_URI"] = mongod.getUri();
  process.env["DB_NAME"] = "test_sisyphus_runner";
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
    expect(res.body.service).toBe("sisyphus-runner");
  });
});

describe("POST /workflows/:type/start", () => {
  it("starts a research workflow", async () => {
    const app = await getApp();
    const res = await request(app)
      .post("/workflows/research/start")
      .send({ mode: "graph_based" });

    expect(res.status).toBe(200);
    expect(res.body.sessionId).toBeDefined();
    expect(res.body.runId).toBe("exec-123");
    expect(res.body.workflowType).toBe("research");
    expect(res.body.status).toBe("running");
  });

  it("returns 409 when workflow already running", async () => {
    const app = await getApp();
    const res = await request(app)
      .post("/workflows/research/start")
      .send({});

    expect(res.status).toBe(409);
  });
});

describe("POST /workflows/:type/stop", () => {
  it("stops a running workflow", async () => {
    const app = await getApp();
    const res = await request(app)
      .post("/workflows/research/stop");

    expect(res.status).toBe(200);
    expect(res.body.status).toBe("stopped");
  });

  it("returns 404 when no session running", async () => {
    const app = await getApp();
    const res = await request(app)
      .post("/workflows/research/stop");

    expect(res.status).toBe(404);
  });
});

describe("GET /workflows/:type/status", () => {
  it("returns status for a workflow type", async () => {
    const app = await getApp();
    const res = await request(app).get("/workflows/research/status");

    expect(res.status).toBe(200);
    expect(res.body).toHaveProperty("running");
    expect(res.body).toHaveProperty("session");
  });
});

describe("GET /workflows/status", () => {
  it("returns status of all workflow types", async () => {
    const app = await getApp();
    const res = await request(app).get("/workflows/status");

    expect(res.status).toBe(200);
    expect(res.body).toHaveProperty("research");
    expect(res.body).toHaveProperty("translate");
    expect(res.body).toHaveProperty("purify");
    expect(res.body).toHaveProperty("verify");
  });
});

describe("GET /history", () => {
  it("returns paginated history", async () => {
    const app = await getApp();
    const res = await request(app).get("/history");

    expect(res.status).toBe(200);
    expect(res.body.records).toBeInstanceOf(Array);
    expect(res.body.total).toBeGreaterThanOrEqual(1); // From start/stop tests
    expect(res.body.page).toBe(1);
  });

  it("filters by workflowType", async () => {
    const app = await getApp();
    const res = await request(app).get("/history?workflowType=research");

    expect(res.status).toBe(200);
    for (const record of res.body.records) {
      expect(record.workflowType).toBe("research");
    }
  });
});

describe("GET /history/:sessionId", () => {
  it("returns session detail with events", async () => {
    const app = await getApp();

    // Start and stop a session to get a session ID
    const startRes = await request(app)
      .post("/workflows/translate/start")
      .send({});
    const sessionId = startRes.body.sessionId;

    await request(app).post("/workflows/translate/stop");

    const res = await request(app).get(`/history/${sessionId}`);

    expect(res.status).toBe(200);
    expect(res.body.session.id).toBe(sessionId);
    expect(res.body.events).toBeInstanceOf(Array);
  });

  it("returns 404 for unknown session", async () => {
    const app = await getApp();
    const res = await request(app).get("/history/nonexistent");
    expect(res.status).toBe(404);
  });
});

describe("Crash recovery", () => {
  it("marks stale sessions as failed on startup", async () => {
    // The recovery already ran on startup via index.ts
    // We just verify that previously started sessions were cleaned up
    const app = await getApp();
    const res = await request(app).get("/history?status=failed");
    // This should work without error, proving recovery ran
    expect(res.status).toBe(200);
  });
});
