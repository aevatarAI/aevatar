import { describe, it, expect, beforeAll, afterAll } from "vitest";
import request from "supertest";
import { isTectonicAvailable } from "../src/compiler.js";
import { app, server } from "../src/index.js";

let tectonicInstalled = false;

beforeAll(async () => {
  tectonicInstalled = await isTectonicAvailable();
});

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

describe("POST /compile", () => {
  it("returns 400 for missing nodes", async () => {
    const res = await request(app).post("/compile").send({ edges: [] });
    expect(res.status).toBe(400);
    expect(res.body.error).toBeDefined();
  });

  it("returns 400 for empty nodes array", async () => {
    const res = await request(app).post("/compile").send({ nodes: [], edges: [] });
    expect(res.status).toBe(400);
  });

  it.skipIf(!tectonicInstalled)("compiles a simple document to PDF", async () => {
    const res = await request(app)
      .post("/compile")
      .send({
        nodes: [
          {
            id: "n1",
            type: "theorem",
            properties: {
              body: "Let $x \\in \\mathbb{R}$. Then $x^2 \\geq 0$.",
              abstract: "Non-negativity of squares",
            },
          },
          {
            id: "n2",
            type: "proof",
            properties: {
              body: "Since $x^2 = x \\cdot x$, if $x \\geq 0$ then $x^2 \\geq 0$. If $x < 0$, then $-x > 0$ and $x^2 = (-x)^2 > 0$.",
              abstract: "",
            },
          },
        ],
        edges: [
          { source: "n2", target: "n1", edge_type: "proves" },
        ],
      })
      .timeout(30000);

    expect(res.status).toBe(200);
    expect(res.headers["content-type"]).toContain("application/pdf");
    expect(res.body.length).toBeGreaterThan(100);
    // PDF magic bytes
    expect(res.body.slice(0, 5).toString()).toBe("%PDF-");
  }, 60000);

  it.skipIf(!tectonicInstalled)("compiles document with math formulas", async () => {
    const res = await request(app)
      .post("/compile")
      .send({
        nodes: [
          {
            id: "d1",
            type: "definition",
            properties: {
              body: "Let $\\mathcal{H}$ be a Hilbert space with inner product $\\langle \\cdot, \\cdot \\rangle$.",
              abstract: "Hilbert space definition",
            },
          },
        ],
        edges: [],
      })
      .timeout(30000);

    expect(res.status).toBe(200);
    expect(res.headers["content-type"]).toContain("application/pdf");
  }, 60000);

  it.skipIf(!tectonicInstalled)("handles broken TeX with guaranteed compilation", async () => {
    const res = await request(app)
      .post("/compile")
      .send({
        nodes: [
          {
            id: "n1",
            type: "theorem",
            properties: {
              body: "\\alpha outside math and _broken subscript {{{unbalanced",
              abstract: "Broken TeX test",
            },
          },
        ],
        edges: [],
      })
      .timeout(30000);

    expect(res.status).toBe(200);
    expect(res.headers["content-type"]).toContain("application/pdf");
  }, 60000);

  it.skipIf(!tectonicInstalled)("compiles document with Unicode", async () => {
    const res = await request(app)
      .post("/compile")
      .send({
        nodes: [
          {
            id: "n1",
            type: "theorem",
            properties: {
              body: "For all α ∈ ℝ, we have α² ≥ 0.",
              abstract: "Unicode test",
            },
          },
        ],
        edges: [],
      })
      .timeout(30000);

    expect(res.status).toBe(200);
    expect(res.headers["content-type"]).toContain("application/pdf");
  }, 60000);
});
