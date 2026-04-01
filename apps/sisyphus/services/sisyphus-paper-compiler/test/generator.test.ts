import { describe, it, expect } from "vitest";
import { generateLatex } from "../src/generator.js";
import type { GraphNode, GraphEdge } from "../src/types.js";

function makeNode(id: string, type: string, body: string, abstract_ = ""): GraphNode {
  return {
    id,
    type,
    properties: { body, abstract: abstract_ },
  };
}

describe("generateLatex", () => {
  it("generates a valid LaTeX document with preamble", () => {
    const nodes = [makeNode("n1", "theorem", "Let $x = 1$.")];
    const latex = generateLatex(nodes, []);

    expect(latex).toContain("\\documentclass");
    expect(latex).toContain("\\begin{document}");
    expect(latex).toContain("\\end{document}");
    expect(latex).toContain("\\begin{theorem}");
    expect(latex).toContain("\\end{theorem}");
    expect(latex).toContain("Let $x = 1$.");
  });

  it("uses correct amsthm environments", () => {
    const types = [
      "theorem", "lemma", "definition", "corollary", "conjecture",
      "proposition", "remark", "conclusion", "example", "notation",
      "axiom", "observation", "note",
    ];

    for (const type of types) {
      const nodes = [makeNode("n1", type, "Body text.")];
      const latex = generateLatex(nodes, []);
      expect(latex).toContain(`\\begin{${type}}`);
      expect(latex).toContain(`\\end{${type}}`);
    }
  });

  it("handles proof environment separately", () => {
    const nodes = [makeNode("n1", "proof", "QED.")];
    const latex = generateLatex(nodes, []);
    expect(latex).toContain("\\begin{proof}");
    expect(latex).toContain("\\end{proof}");
  });

  it("includes abstract as italicized text", () => {
    const nodes = [makeNode("n1", "theorem", "Body.", "This is the abstract.")];
    const latex = generateLatex(nodes, []);
    expect(latex).toContain("\\textit{This is the abstract.}");
  });

  it("includes cross-references", () => {
    const nodes = [
      makeNode("n1", "theorem", "Statement."),
      makeNode("n2", "proof", "Proof."),
    ];
    const edges: GraphEdge[] = [
      { source: "n2", target: "n1", edge_type: "proves" },
    ];
    const latex = generateLatex(nodes, edges);
    expect(latex).toContain("\\ref{node:n1}");
    expect(latex).toContain("See also:");
  });

  it("includes labels for each node", () => {
    const nodes = [makeNode("abc-123", "definition", "Def.")];
    const latex = generateLatex(nodes, []);
    expect(latex).toContain("\\label{node:abc-123}");
  });

  it("adds ifmmode safety after body", () => {
    const nodes = [makeNode("n1", "theorem", "Body text")];
    const latex = generateLatex(nodes, []);
    expect(latex).toContain("\\ifmmode\\)\\fi");
  });

  it("sanitizes broken TeX by falling back to plain text", () => {
    const nodes = [makeNode("n1", "theorem", "\\alpha outside math with _bad")];
    const latex = generateLatex(nodes, []);
    // Should not contain bare \\alpha (broken)
    expect(latex).toContain("\\begin{theorem}");
    expect(latex).toContain("\\end{theorem}");
  });

  it("handles Unicode characters in body", () => {
    const nodes = [makeNode("n1", "theorem", "$x ∈ ℝ$")];
    const latex = generateLatex(nodes, []);
    // Unicode should be replaced with LaTeX equivalents
    expect(latex).not.toContain("∈");
    expect(latex).not.toContain("ℝ");
  });

  it("handles nodes with unknown type via paragraph", () => {
    const nodes = [makeNode("n1", "unknown_type", "Some text.")];
    const latex = generateLatex(nodes, []);
    expect(latex).toContain("\\paragraph{");
    expect(latex).toContain("Some text.");
  });

  it("auto-defines unknown commands", () => {
    const nodes = [makeNode("n1", "theorem", "$\\mycustomcmd{x}$")];
    const latex = generateLatex(nodes, []);
    expect(latex).toContain("\\providecommand{\\mycustomcmd}{}");
  });

  it("handles empty body gracefully", () => {
    const nodes = [makeNode("n1", "theorem", "")];
    const latex = generateLatex(nodes, []);
    expect(latex).toContain("\\begin{theorem}");
    expect(latex).toContain("\\end{theorem}");
  });
});
