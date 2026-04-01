import { describe, it, expect, beforeAll, afterAll, vi } from "vitest";
import request from "supertest";
import { MongoMemoryServer } from "mongodb-memory-server";

// Mock ornn-client before importing anything that uses it
vi.mock("../src/ornn-client.js", () => ({
  fetchSkillContent: vi.fn().mockResolvedValue({
    id: "skill-123",
    name: "researcher",
    content: "You are a mathematical researcher. Generate novel theorems.",
  }),
  OrnnUnreachableError: class OrnnUnreachableError extends Error {
    constructor(message: string) {
      super(message);
      this.name = "OrnnUnreachableError";
    }
  },
}));

let mongod: MongoMemoryServer;

beforeAll(async () => {
  mongod = await MongoMemoryServer.create();
  process.env["MONGO_URI"] = mongod.getUri();
  process.env["DB_NAME"] = "test_sisyphus_workflows";
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
  });
});

describe("Workflow CRUD", () => {
  let workflowId: string;

  it("POST /workflows — creates a workflow", async () => {
    const app = await getApp();
    const res = await request(app)
      .post("/workflows")
      .send({
        name: "research-pipeline",
        description: "Automated research workflow",
        roles: [
          { name: "researcher", skillId: "skill-123" },
          { name: "verifier", skillId: "skill-456" },
        ],
        steps: [
          { name: "generate", type: "llm_call", order: 1, roleRef: "researcher" },
          { name: "validate", type: "connector_call", order: 2, connectorRef: "format_validator" },
          { name: "verify", type: "llm_call", order: 3, roleRef: "verifier" },
        ],
      });

    expect(res.status).toBe(201);
    expect(res.body.id).toBeDefined();
    expect(res.body.name).toBe("research-pipeline");
    expect(res.body.roles).toHaveLength(2);
    expect(res.body.steps).toHaveLength(3);
    workflowId = res.body.id;
  });

  it("GET /workflows — lists workflows", async () => {
    const app = await getApp();
    const res = await request(app).get("/workflows");

    expect(res.status).toBe(200);
    expect(res.body.workflows).toBeInstanceOf(Array);
    expect(res.body.total).toBeGreaterThanOrEqual(1);
  });

  it("GET /workflows/:id — returns a single workflow", async () => {
    const app = await getApp();
    const res = await request(app).get(`/workflows/${workflowId}`);

    expect(res.status).toBe(200);
    expect(res.body.id).toBe(workflowId);
    expect(res.body.roles).toHaveLength(2);
  });

  it("GET /workflows/:id — returns 404 for unknown id", async () => {
    const app = await getApp();
    const res = await request(app).get("/workflows/nonexistent");
    expect(res.status).toBe(404);
  });

  it("PUT /workflows/:id — updates a workflow", async () => {
    const app = await getApp();
    const res = await request(app)
      .put(`/workflows/${workflowId}`)
      .send({ description: "Updated research workflow" });

    expect(res.status).toBe(200);
    expect(res.body.description).toBe("Updated research workflow");
  });

  it("DELETE /workflows/:id — deletes a workflow", async () => {
    const app = await getApp();

    // Create one to delete
    const createRes = await request(app)
      .post("/workflows")
      .send({
        name: "to-delete",
        description: "Will be deleted",
        roles: [],
        steps: [],
      });

    const delRes = await request(app).delete(`/workflows/${createRes.body.id}`);
    expect(delRes.status).toBe(204);

    const getRes = await request(app).get(`/workflows/${createRes.body.id}`);
    expect(getRes.status).toBe(404);
  });
});

describe("Connector CRUD", () => {
  let connectorId: string;

  it("POST /connectors — creates a connector", async () => {
    const app = await getApp();
    const res = await request(app)
      .post("/connectors")
      .send({
        name: "format_validator",
        description: "Schema validation connector",
        type: "http",
        baseUrl: "https://schema-validator.example.com",
        endpoints: [
          { name: "validate", method: "POST", path: "/validate" },
        ],
      });

    expect(res.status).toBe(201);
    expect(res.body.id).toBeDefined();
    expect(res.body.name).toBe("format_validator");
    expect(res.body.type).toBe("http");
    connectorId = res.body.id;
  });

  it("GET /connectors — lists connectors", async () => {
    const app = await getApp();
    const res = await request(app).get("/connectors");

    expect(res.status).toBe(200);
    expect(res.body.connectors).toBeInstanceOf(Array);
    expect(res.body.total).toBeGreaterThanOrEqual(1);
  });

  it("GET /connectors/:id — returns a single connector", async () => {
    const app = await getApp();
    const res = await request(app).get(`/connectors/${connectorId}`);

    expect(res.status).toBe(200);
    expect(res.body.name).toBe("format_validator");
  });

  it("PUT /connectors/:id — updates a connector", async () => {
    const app = await getApp();
    const res = await request(app)
      .put(`/connectors/${connectorId}`)
      .send({ description: "Updated description" });

    expect(res.status).toBe(200);
    expect(res.body.description).toBe("Updated description");
  });

  it("DELETE /connectors/:id — deletes a connector", async () => {
    const app = await getApp();
    const res = await request(app).delete(`/connectors/${connectorId}`);
    expect(res.status).toBe(204);
  });
});

describe("GET /compile/:workflowId", () => {
  let workflowId: string;

  it("compiles a workflow with skill injection", async () => {
    const app = await getApp();

    // Create connector first
    await request(app)
      .post("/connectors")
      .send({
        name: "chrono_graph",
        description: "Graph connector",
        type: "http",
        baseUrl: "https://graph.example.com",
        endpoints: [{ name: "write_nodes", method: "POST", path: "/nodes" }],
      });

    // Create workflow
    const wfRes = await request(app)
      .post("/workflows")
      .send({
        name: "compile-test",
        description: "Test compilation",
        roles: [{ name: "researcher", skillId: "skill-123" }],
        steps: [
          { name: "generate", type: "llm_call", order: 1, roleRef: "researcher" },
          { name: "write", type: "connector_call", order: 2, connectorRef: "chrono_graph" },
        ],
      });

    workflowId = wfRes.body.id;

    const res = await request(app).get(`/compile/${workflowId}`);

    expect(res.status).toBe(200);
    expect(res.body.workflowYaml).toBeDefined();
    expect(res.body.workflowYaml).toContain("researcher");
    expect(res.body.workflowYaml).toContain("system_prompt");
    expect(res.body.workflowYaml).toContain("mathematical researcher");
    expect(res.body.connectorJson).toBeInstanceOf(Array);
    expect(res.body.connectorJson.length).toBeGreaterThanOrEqual(1);
  });

  it("returns 404 for unknown workflow", async () => {
    const app = await getApp();
    const res = await request(app).get("/compile/nonexistent");
    expect(res.status).toBe(404);
  });
});
