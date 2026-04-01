import {
  Controller,
  Post,
  Route,
  Tags,
  Body,
  Response,
  SuccessResponse,
} from "tsoa";
import pino from "pino";
import type { CompileRequest } from "../types.js";
import { topologicalSort } from "../topological-sort.js";
import { generateLatex } from "../generator.js";
import { compilePdf } from "../compiler.js";

const logger = pino({ name: "paper-compiler:compile-controller" });

interface ErrorResponse {
  error: string;
}

/**
 * Compiles knowledge graph nodes into LaTeX research papers and produces PDF output.
 *
 * The compilation pipeline:
 * 1. **Sanitize** — broken or malformed TeX in node bodies is automatically repaired
 *    (brace balancing, stripping dangerous commands, Unicode-to-LaTeX conversion)
 * 2. **Topological sort** — nodes are ordered by edge dependencies using Kahn's algorithm
 *    so that referenced nodes appear before referencing nodes; falls back to type-based
 *    ordering on cycles
 * 3. **Generate LaTeX** — assembles a complete LaTeX document with amsthm environments,
 *    preamble, cross-references, and auto-defined unknown commands
 * 4. **Compile with tectonic** — produces the final PDF binary; compilation is guaranteed
 *    because broken TeX is stripped to plain text as a last resort
 */
@Route("compile")
@Tags("Compilation")
export class CompileController extends Controller {
  /**
   * Compile knowledge graph nodes into a PDF research paper.
   *
   * Accepts an array of black (verified) nodes with TeX content and edges defining
   * their dependency relationships. Returns the compiled PDF as a binary stream.
   *
   * Broken or malformed TeX is auto-repaired: brace balancing, dangerous command
   * stripping, and fallback to plain text ensure compilation always succeeds.
   *
   * The response is a raw PDF binary with Content-Type `application/pdf`.
   *
   * @summary Compile nodes to PDF
   */
  @Post()
  @SuccessResponse(200, "PDF compiled successfully")
  @Response<ErrorResponse>(400, "Invalid request — nodes array is empty or exceeds maximum")
  @Response<ErrorResponse>(500, "Compilation failure — tectonic could not produce a PDF")
  public async compile(
    @Body() body: CompileRequest,
  ): Promise<Buffer> {
    if (!body.nodes || !Array.isArray(body.nodes) || body.nodes.length === 0) {
      this.setStatus(400);
      return Buffer.from(JSON.stringify({ error: "Request must include a non-empty 'nodes' array" }));
    }

    const MAX_NODES = 50_000;
    if (body.nodes.length > MAX_NODES) {
      this.setStatus(400);
      return Buffer.from(JSON.stringify({ error: `Node count ${body.nodes.length} exceeds maximum of ${MAX_NODES}` }));
    }

    const edges = Array.isArray(body.edges) ? body.edges : [];

    try {
      logger.info({ nodeCount: body.nodes.length, edgeCount: edges.length }, "Compiling paper");

      const sorted = topologicalSort(body.nodes, edges);
      const latex = generateLatex(sorted, edges);

      logger.info({ latexLength: latex.length }, "LaTeX generated, compiling to PDF");

      const pdfBytes = await compilePdf(latex);

      this.setHeader("Content-Type", "application/pdf");
      this.setHeader("Content-Disposition", "attachment; filename=paper.pdf");
      this.setStatus(200);
      return pdfBytes;
    } catch (err) {
      const message = err instanceof Error ? err.message : "Compilation failed";
      logger.error({ err: message }, "Compilation failed");
      this.setStatus(500);
      return Buffer.from(JSON.stringify({ error: message }));
    }
  }
}
