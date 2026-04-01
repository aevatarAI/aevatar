import { describe, it, expect } from "vitest";
import { sanitizeBody, isBodyBroken, balanceBraces, stripToPlainText } from "../src/sanitizer.js";

describe("sanitizeBody", () => {
  it("returns empty string for empty input", () => {
    expect(sanitizeBody("")).toBe("");
    expect(sanitizeBody("   ")).toBe("   ");
  });

  it("preserves valid LaTeX", () => {
    const body = "Let $x \\in \\mathbb{R}$ such that $x > 0$.";
    expect(sanitizeBody(body)).toBe(body);
  });

  it("fixes double-escaped commands inside math mode", () => {
    const body = "$\\\\frac{1}{2} + \\\\alpha$";
    const result = sanitizeBody(body);
    expect(result).toContain("\\frac{1}{2}");
    expect(result).toContain("\\alpha");
  });

  it("replaces Unicode Greek letters", () => {
    const body = "Let α be a constant and β > 0.";
    const result = sanitizeBody(body);
    expect(result).toContain("$\\alpha$");
    expect(result).toContain("$\\beta$");
  });

  it("replaces Unicode math symbols", () => {
    const body = "x ∈ ℝ and y ≤ z";
    const result = sanitizeBody(body);
    expect(result).toContain("$\\in$");
    expect(result).toContain("$\\mathbb{R}$");
    expect(result).toContain("$\\leq$");
  });

  it("strips dangerous commands", () => {
    const body = "\\input{secret}\n\\usepackage{tikz}\nSome text.";
    const result = sanitizeBody(body);
    expect(result).not.toContain("\\input");
    expect(result).not.toContain("\\usepackage");
    expect(result).toContain("Some text.");
  });

  it("strips dangerous environments from body", () => {
    const body = "\\begin{theorem}Some content\\end{theorem}";
    const result = sanitizeBody(body);
    expect(result).not.toContain("\\begin{theorem}");
    expect(result).not.toContain("\\end{theorem}");
    expect(result).toContain("Some content");
  });

  it("fixes unbalanced braces inside math mode", () => {
    const body = "$\\frac{1}{2$";
    const result = sanitizeBody(body);
    // balanceBraces adds closing brace, result should not be broken
    expect(isBodyBroken(result)).toBe(false);
  });

  it("strips excess closing braces", () => {
    const body = "text}extra}";
    const result = sanitizeBody(body);
    expect(isBodyBroken(result)).toBe(false);
  });

  it("escapes bare # characters", () => {
    const body = "Item #1 and #2";
    const result = sanitizeBody(body);
    expect(result).toContain("\\#1");
    expect(result).toContain("\\#2");
  });

  it("replaces backticks with apostrophes", () => {
    const body = "the `definition` of x";
    const result = sanitizeBody(body);
    expect(result).not.toContain("`");
    expect(result).toContain("'definition'");
  });

  it("falls back to plain text for broken math mode", () => {
    const body = "This has bare \\alpha outside math and _subscript too";
    const result = sanitizeBody(body);
    // Should be stripped to plain text since \\alpha outside math mode is broken
    expect(isBodyBroken(result)).toBe(false);
  });

  it("handles double-escaped special chars inside math mode", () => {
    const body = "$x\\\\_y$ and $z\\\\^w$";
    const result = sanitizeBody(body);
    expect(result).toContain("_");
    expect(result).toContain("^");
  });

  it("handles double-escaped spacing commands", () => {
    const body = "a\\\\;b\\\\,c\\\\!d\\\\:e";
    const result = sanitizeBody(body);
    expect(result).toContain("\\;");
    expect(result).toContain("\\,");
  });

  it("strips control characters", () => {
    const body = "text\x08with\x0Ccontrol\x1Fchars";
    const result = sanitizeBody(body);
    expect(result).not.toMatch(/[\x00-\x08\x0B\x0C\x0E-\x1F\x7F]/);
  });

  it("handles unmatched itemize environments", () => {
    const body = "\\begin{itemize}\\item one\\item two";
    const result = sanitizeBody(body);
    // Should strip the begin/end and convert \\item
    expect(result).not.toContain("\\begin{itemize}");
  });
});

describe("isBodyBroken", () => {
  it("returns false for empty input", () => {
    expect(isBodyBroken("")).toBe(false);
  });

  it("returns false for valid LaTeX", () => {
    expect(isBodyBroken("Let $x \\in \\mathbb{R}$")).toBe(false);
  });

  it("returns true for unbalanced braces", () => {
    expect(isBodyBroken("\\frac{1}{2")).toBe(true);
  });

  it("returns true for excess closing braces", () => {
    expect(isBodyBroken("text}extra")).toBe(true);
  });

  it("returns true for math-only commands outside math mode", () => {
    expect(isBodyBroken("\\alpha outside math")).toBe(true);
  });

  it("returns false for math-only commands inside math mode", () => {
    expect(isBodyBroken("$\\alpha$ inside math")).toBe(false);
  });

  it("returns true for bare subscript outside math", () => {
    expect(isBodyBroken("x_1")).toBe(true);
  });

  it("returns false for subscript inside math", () => {
    expect(isBodyBroken("$x_1$")).toBe(false);
  });

  it("returns true for unmatched environments", () => {
    expect(isBodyBroken("\\begin{align}x\\end{equation}")).toBe(true);
  });

  it("returns false for properly matched environments", () => {
    expect(isBodyBroken("\\begin{align}x\\end{align}")).toBe(false);
  });

  it("handles nested math environments", () => {
    expect(isBodyBroken("\\begin{equation}\\begin{cases}a\\\\b\\end{cases}\\end{equation}")).toBe(false);
  });

  it("handles display math with $$", () => {
    expect(isBodyBroken("$$x + y$$")).toBe(false);
  });
});

describe("balanceBraces", () => {
  it("adds missing closing braces", () => {
    expect(balanceBraces("a{b{c")).toBe("a{b{c}}");
  });

  it("removes excess closing braces", () => {
    expect(balanceBraces("a}b}")).toBe("ab");
  });

  it("leaves balanced braces unchanged", () => {
    expect(balanceBraces("{a{b}c}")).toBe("{a{b}c}");
  });
});

describe("stripToPlainText", () => {
  it("strips LaTeX commands", () => {
    const result = stripToPlainText("\\textbf{hello} and \\emph{world}");
    expect(result).toContain("hello");
    expect(result).toContain("world");
    expect(result).not.toContain("\\textbf");
  });

  it("removes math delimiters", () => {
    const result = stripToPlainText("$x + y$ and $$z$$");
    expect(result).not.toContain("$");
  });

  it("removes braces", () => {
    const result = stripToPlainText("{content}");
    expect(result).not.toContain("{");
    expect(result).not.toContain("}");
  });

  it("escapes special chars", () => {
    const result = stripToPlainText("a & b");
    expect(result).toContain("\\&");
  });
});
