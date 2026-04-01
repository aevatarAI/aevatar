import { describe, it, expect, afterAll } from "vitest";
import request from "supertest";
import { app, server } from "../src/index.js";

afterAll(() => {
  server.close();
});

describe("GET /health", () => {
  it("returns ok", async () => {
    const res = await request(app).get("/health");
    expect(res.status).toBe(200);
    expect(res.body).toEqual({ status: "ok" });
  });
});

describe("POST /validate", () => {
  it("returns valid for conforming data", async () => {
    const res = await request(app)
      .post("/validate")
      .send({
        schema: {
          type: "object",
          required: ["name"],
          properties: { name: { type: "string" } },
        },
        data: { name: "test" },
      });

    expect(res.status).toBe(200);
    expect(res.body).toEqual({ valid: true });
  });

  it("returns invalid with errors for non-conforming data", async () => {
    const res = await request(app)
      .post("/validate")
      .send({
        schema: {
          type: "object",
          required: ["name"],
          properties: { name: { type: "string" } },
        },
        data: { name: 42 },
      });

    expect(res.status).toBe(200);
    expect(res.body.valid).toBe(false);
    expect(res.body.errors).toBeDefined();
    expect(res.body.errors.length).toBeGreaterThan(0);
  });

  it("returns 400 when schema is missing", async () => {
    const res = await request(app)
      .post("/validate")
      .send({ data: { name: "test" } });

    expect(res.status).toBe(400);
    expect(res.body.valid).toBe(false);
  });

  it("returns 400 when data is missing", async () => {
    const res = await request(app)
      .post("/validate")
      .send({ schema: { type: "object" } });

    expect(res.status).toBe(400);
    expect(res.body.valid).toBe(false);
  });

  it("handles malformed schema gracefully (not 500)", async () => {
    const res = await request(app)
      .post("/validate")
      .send({
        schema: { type: "not-a-real-type" },
        data: {},
      });

    expect(res.status).toBe(200);
    expect(res.body.valid).toBe(false);
    expect(res.body.errors).toBeDefined();
  });

  it("validates blue-nodes schema — valid payload", async () => {
    const blueNodesSchema = await import(
      "../../../workflows/schemas/blue-nodes.json",
      { with: { type: "json" } }
    );

    const res = await request(app)
      .post("/validate")
      .send({
        schema: blueNodesSchema.default,
        data: {
          blue_nodes: [
            {
              temp_id: "n1",
              type: "theorem",
              abstract: "A theorem",
              body: "Let $x$ be...",
            },
            {
              temp_id: "n2",
              type: "proof",
              abstract: "Proof of theorem",
              body: "By induction...",
            },
          ],
          blue_edges: [
            { source: "n2", target: "n1", edge_type: "proves" },
          ],
        },
      });

    expect(res.status).toBe(200);
    expect(res.body).toEqual({ valid: true });
  });

  it("rejects blue-nodes with invalid type", async () => {
    const blueNodesSchema = await import(
      "../../../workflows/schemas/blue-nodes.json",
      { with: { type: "json" } }
    );

    const res = await request(app)
      .post("/validate")
      .send({
        schema: blueNodesSchema.default,
        data: {
          blue_nodes: [
            {
              temp_id: "n1",
              type: "invalid_type",
              abstract: "Bad",
              body: "Bad",
            },
          ],
        },
      });

    expect(res.status).toBe(200);
    expect(res.body.valid).toBe(false);
    expect(res.body.errors!.some((e: string) => e.includes("allowed values"))).toBe(true);
  });

  it("rejects blue-nodes with missing required fields", async () => {
    const blueNodesSchema = await import(
      "../../../workflows/schemas/blue-nodes.json",
      { with: { type: "json" } }
    );

    const res = await request(app)
      .post("/validate")
      .send({
        schema: blueNodesSchema.default,
        data: {
          blue_nodes: [{ temp_id: "n1" }],
        },
      });

    expect(res.status).toBe(200);
    expect(res.body.valid).toBe(false);
  });

  it("rejects blue-nodes with invalid edge_type", async () => {
    const blueNodesSchema = await import(
      "../../../workflows/schemas/blue-nodes.json",
      { with: { type: "json" } }
    );

    const res = await request(app)
      .post("/validate")
      .send({
        schema: blueNodesSchema.default,
        data: {
          blue_nodes: [
            { temp_id: "n1", type: "theorem", abstract: "A", body: "B" },
          ],
          blue_edges: [
            { source: "n1", target: "n2", edge_type: "depends_on" },
          ],
        },
      });

    expect(res.status).toBe(200);
    expect(res.body.valid).toBe(false);
  });

  it("rejects blue-nodes with empty array", async () => {
    const blueNodesSchema = await import(
      "../../../workflows/schemas/blue-nodes.json",
      { with: { type: "json" } }
    );

    const res = await request(app)
      .post("/validate")
      .send({
        schema: blueNodesSchema.default,
        data: { blue_nodes: [] },
      });

    expect(res.status).toBe(200);
    expect(res.body.valid).toBe(false);
  });

  it("rejects blue-nodes with additional properties", async () => {
    const blueNodesSchema = await import(
      "../../../workflows/schemas/blue-nodes.json",
      { with: { type: "json" } }
    );

    const res = await request(app)
      .post("/validate")
      .send({
        schema: blueNodesSchema.default,
        data: {
          blue_nodes: [
            { temp_id: "n1", type: "theorem", abstract: "A", body: "B", extra: "bad" },
          ],
        },
      });

    expect(res.status).toBe(200);
    expect(res.body.valid).toBe(false);
  });

  it("validates black-nodes schema — valid payload", async () => {
    const blackNodesSchema = await import(
      "../../../workflows/schemas/black-nodes.json",
      { with: { type: "json" } }
    );

    const res = await request(app)
      .post("/validate")
      .send({
        schema: blackNodesSchema.default,
        data: {
          nodes: [
            {
              temp_id: "n1",
              verdict: "PASS",
              confidence: 0.95,
              reasoning: "Correct proof by induction",
            },
            {
              temp_id: "n2",
              verdict: "FAIL",
              confidence: 0.3,
              reasoning: "Missing base case",
            },
          ],
        },
      });

    expect(res.status).toBe(200);
    expect(res.body).toEqual({ valid: true });
  });

  it("rejects black-nodes with invalid verdict", async () => {
    const blackNodesSchema = await import(
      "../../../workflows/schemas/black-nodes.json",
      { with: { type: "json" } }
    );

    const res = await request(app)
      .post("/validate")
      .send({
        schema: blackNodesSchema.default,
        data: {
          nodes: [
            {
              temp_id: "n1",
              verdict: "MAYBE",
              confidence: 0.5,
              reasoning: "Unsure",
            },
          ],
        },
      });

    expect(res.status).toBe(200);
    expect(res.body.valid).toBe(false);
  });

  it("rejects black-nodes with confidence out of range", async () => {
    const blackNodesSchema = await import(
      "../../../workflows/schemas/black-nodes.json",
      { with: { type: "json" } }
    );

    const res = await request(app)
      .post("/validate")
      .send({
        schema: blackNodesSchema.default,
        data: {
          nodes: [
            {
              temp_id: "n1",
              verdict: "PASS",
              confidence: 1.5,
              reasoning: "Too confident",
            },
          ],
        },
      });

    expect(res.status).toBe(200);
    expect(res.body.valid).toBe(false);
  });

  it("validates blue-edges schema — valid payload", async () => {
    const blueEdgesSchema = await import(
      "../../../workflows/schemas/blue-edges.json",
      { with: { type: "json" } }
    );

    const res = await request(app)
      .post("/validate")
      .send({
        schema: blueEdgesSchema.default,
        data: {
          blue_edges: [
            { source: "n1", target: "n2", edge_type: "references" },
            {
              source: "n3",
              target: "n1",
              edge_type: "proves",
              source_id: "uuid1",
              target_id: "uuid2",
            },
          ],
        },
      });

    expect(res.status).toBe(200);
    expect(res.body).toEqual({ valid: true });
  });

  it("validates black-edges schema — valid payload", async () => {
    const blackEdgesSchema = await import(
      "../../../workflows/schemas/black-edges.json",
      { with: { type: "json" } }
    );

    const res = await request(app)
      .post("/validate")
      .send({
        schema: blackEdgesSchema.default,
        data: {
          black_edges: [
            { source: "n1", target: "n2", edge_type: "proves" },
          ],
        },
      });

    expect(res.status).toBe(200);
    expect(res.body).toEqual({ valid: true });
  });

  it("validates research-nodes schema with noderef", async () => {
    const researchSchema = await import(
      "../../../workflows/schemas/research-nodes.json",
      { with: { type: "json" } }
    );

    const res = await request(app)
      .post("/validate")
      .send({
        schema: researchSchema.default,
        data: {
          blue_nodes: [
            {
              temp_id: "n1",
              type: "definition",
              abstract: "A definition",
              body: "Define $x$...",
              noderef: "550e8400-e29b-41d4-a716-446655440000",
            },
          ],
        },
      });

    expect(res.status).toBe(200);
    expect(res.body).toEqual({ valid: true });
  });

  it("validates all node types in whitelist", async () => {
    const blueNodesSchema = await import(
      "../../../workflows/schemas/blue-nodes.json",
      { with: { type: "json" } }
    );

    const types = [
      "theorem", "lemma", "definition", "proof", "corollary",
      "conjecture", "proposition", "remark", "conclusion",
      "example", "notation", "axiom", "observation", "note",
    ];

    for (const type of types) {
      const res = await request(app)
        .post("/validate")
        .send({
          schema: blueNodesSchema.default,
          data: {
            blue_nodes: [
              { temp_id: `n-${type}`, type, abstract: "Test", body: "Test" },
            ],
          },
        });

      expect(res.status).toBe(200);
      expect(res.body.valid).toBe(true);
    }
  });
});
