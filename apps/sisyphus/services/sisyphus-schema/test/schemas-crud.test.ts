import { describe, it, expect, beforeAll, afterAll } from "vitest";
import request from "supertest";
import { MongoMemoryServer } from "mongodb-memory-server";

let mongod: MongoMemoryServer;

beforeAll(async () => {
  mongod = await MongoMemoryServer.create();
  process.env["MONGO_URI"] = mongod.getUri();
  process.env["DB_NAME"] = "test_sisyphus_schemas";
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

describe("Schema CRUD", () => {
  let createdId: string;

  it("POST /schemas — creates a schema", async () => {
    const app = await getApp();
    const res = await request(app)
      .post("/schemas")
      .send({
        name: "test-node-schema",
        description: "A test node schema",
        entityType: "node",
        applicableTypes: ["theorem", "lemma"],
        jsonSchema: {
          type: "object",
          required: ["title"],
          properties: { title: { type: "string" } },
        },
      });

    expect(res.status).toBe(201);
    expect(res.body.id).toBeDefined();
    expect(res.body.name).toBe("test-node-schema");
    expect(res.body.entityType).toBe("node");
    createdId = res.body.id;
  });

  it("POST /schemas — rejects duplicate name", async () => {
    const app = await getApp();
    const res = await request(app)
      .post("/schemas")
      .send({
        name: "test-node-schema",
        description: "Duplicate",
        entityType: "node",
        applicableTypes: [],
        jsonSchema: { type: "object" },
      });

    expect(res.status).toBe(409);
  });

  it("GET /schemas — lists schemas with pagination", async () => {
    const app = await getApp();
    const res = await request(app).get("/schemas");

    expect(res.status).toBe(200);
    expect(res.body.schemas).toBeInstanceOf(Array);
    expect(res.body.total).toBeGreaterThanOrEqual(1);
    expect(res.body.page).toBe(1);
  });

  it("GET /schemas/:id — returns a single schema", async () => {
    const app = await getApp();
    const res = await request(app).get(`/schemas/${createdId}`);

    expect(res.status).toBe(200);
    expect(res.body.id).toBe(createdId);
    expect(res.body.name).toBe("test-node-schema");
  });

  it("GET /schemas/:id — returns 404 for unknown id", async () => {
    const app = await getApp();
    const res = await request(app).get("/schemas/nonexistent-id");

    expect(res.status).toBe(404);
  });

  it("PUT /schemas/:id — updates a schema", async () => {
    const app = await getApp();
    const res = await request(app)
      .put(`/schemas/${createdId}`)
      .send({
        description: "Updated description",
        applicableTypes: ["theorem", "lemma", "definition"],
      });

    expect(res.status).toBe(200);
    expect(res.body.description).toBe("Updated description");
    expect(res.body.applicableTypes).toContain("definition");
  });

  it("PUT /schemas/:id — returns 404 for unknown id", async () => {
    const app = await getApp();
    const res = await request(app)
      .put("/schemas/nonexistent-id")
      .send({ description: "nope" });

    expect(res.status).toBe(404);
  });

  it("POST /validate/:schemaName — validates against stored schema", async () => {
    const app = await getApp();
    const res = await request(app)
      .post("/validate/test-node-schema")
      .send({ data: { title: "My Theorem" } });

    expect(res.status).toBe(200);
    expect(res.body.valid).toBe(true);
  });

  it("POST /validate/:schemaName — returns errors for invalid data", async () => {
    const app = await getApp();
    const res = await request(app)
      .post("/validate/test-node-schema")
      .send({ data: { notTitle: 123 } });

    expect(res.status).toBe(200);
    expect(res.body.valid).toBe(false);
    expect(res.body.errors).toBeDefined();
  });

  it("POST /validate/:schemaName — returns 404 for unknown schema", async () => {
    const app = await getApp();
    const res = await request(app)
      .post("/validate/nonexistent")
      .send({ data: {} });

    expect(res.status).toBe(404);
  });

  it("DELETE /schemas/:id — deletes a schema", async () => {
    const app = await getApp();
    const res = await request(app).delete(`/schemas/${createdId}`);

    expect(res.status).toBe(204);
  });

  it("DELETE /schemas/:id — returns 404 for already deleted", async () => {
    const app = await getApp();
    const res = await request(app).delete(`/schemas/${createdId}`);

    expect(res.status).toBe(404);
  });

  it("POST /validate/:schemaName — returns 404 after schema deleted", async () => {
    const app = await getApp();
    const res = await request(app)
      .post("/validate/test-node-schema")
      .send({ data: { title: "test" } });

    expect(res.status).toBe(404);
  });
});
