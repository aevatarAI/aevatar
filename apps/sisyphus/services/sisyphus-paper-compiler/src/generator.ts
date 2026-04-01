import pino from "pino";
import type { GraphNode, GraphEdge } from "./types.js";
import { sanitizeBody, isBodyBroken, stripToPlainText, balanceBraces } from "./sanitizer.js";

const logger = pino({ name: "paper-compiler:generator" });

const THEOREM_ENV_NAMES: Record<string, string> = {
  theorem: "theorem",
  lemma: "lemma",
  definition: "definition",
  corollary: "corollary",
  conjecture: "conjecture",
  proposition: "proposition",
  remark: "remark",
  conclusion: "conclusion",
  example: "example",
  notation: "notation",
  axiom: "axiom",
  observation: "observation",
  note: "note",
};

const KNOWN_COMMANDS = new Set([
  "documentclass", "usepackage", "begin", "end", "title", "author", "date", "maketitle",
  "section", "subsection", "subsubsection", "paragraph", "subparagraph",
  "chapter", "part", "appendix", "tableofcontents",
  "textbf", "textit", "texttt", "textrm", "textsf", "textsc", "emph", "underline",
  "tiny", "scriptsize", "footnotesize", "small", "normalsize", "large", "Large",
  "LARGE", "huge", "Huge", "bf", "it", "tt", "rm", "sf", "sc", "sl",
  "frac", "dfrac", "tfrac", "sqrt", "root", "overline", "widehat", "widetilde",
  "hat", "tilde", "bar", "vec", "dot", "ddot", "acute", "grave", "check", "breve",
  "mathbb", "mathbf", "mathcal", "mathfrak", "mathrm", "mathsf", "mathit", "mathtt",
  "boldsymbol", "bm", "operatorname", "DeclareMathOperator",
  "text", "mbox", "hbox", "vbox",
  "alpha", "beta", "gamma", "delta", "epsilon", "varepsilon", "zeta", "eta", "theta",
  "vartheta", "iota", "kappa", "lambda", "mu", "nu", "xi", "pi", "varpi",
  "rho", "varrho", "sigma", "varsigma", "tau", "upsilon", "phi", "varphi",
  "chi", "psi", "omega",
  "Gamma", "Delta", "Theta", "Lambda", "Xi", "Pi", "Sigma", "Upsilon",
  "Phi", "Psi", "Omega",
  "le", "leq", "ge", "geq", "neq", "approx", "equiv", "sim", "simeq", "cong",
  "subset", "supset", "subseteq", "supseteq", "in", "notin", "ni",
  "cup", "cap", "setminus", "emptyset", "varnothing",
  "forall", "exists", "nexists", "neg", "lnot", "wedge", "vee", "land", "lor",
  "to", "rightarrow", "leftarrow", "leftrightarrow", "Rightarrow", "Leftarrow",
  "Leftrightarrow", "implies", "iff", "mapsto", "longmapsto",
  "uparrow", "downarrow", "Uparrow", "Downarrow", "updownarrow",
  "hookrightarrow", "hookleftarrow", "rightharpoonup", "rightharpoondown",
  "times", "div", "cdot", "cdots", "ldots", "vdots", "ddots", "dots",
  "pm", "mp", "circ", "bullet", "star", "ast", "oplus", "otimes", "odot",
  "sum", "prod", "coprod", "int", "oint", "iint", "iiint",
  "bigcup", "bigcap", "bigoplus", "bigotimes", "bigsqcup",
  "lim", "limsup", "liminf", "sup", "inf", "max", "min",
  "sin", "cos", "tan", "cot", "sec", "csc", "arcsin", "arccos", "arctan",
  "sinh", "cosh", "tanh", "coth",
  "log", "ln", "exp", "det", "dim", "ker", "gcd", "lcm", "deg", "hom",
  "arg", "mod", "pmod", "bmod",
  "infty", "partial", "nabla", "prime",
  "langle", "rangle", "lfloor", "rfloor", "lceil", "rceil",
  "lvert", "rvert", "lVert", "rVert", "vert", "Vert",
  "left", "right", "big", "Big", "bigg", "Bigg", "biggl", "biggr",
  "quad", "qquad", "hspace", "vspace", "hfill", "vfill",
  "noindent", "indent", "par", "newline", "linebreak", "pagebreak", "newpage",
  "centering", "raggedright", "raggedleft",
  "label", "ref", "eqref", "cite", "footnote", "footnotemark", "footnotetext",
  "item",
  "hline", "cline", "multicolumn", "multirow",
  "includegraphics", "caption",
  "qed", "qedhere", "proof",
  "input", "include", "newcommand", "renewcommand", "providecommand",
  "def", "let", "makeatletter", "makeatother",
  "setlength", "addtolength",
  "textasciitilde", "textasciicircum", "textbackslash",
  "LaTeX", "TeX",
  "N", "Z", "R", "C", "Q", "eps", "ind", "norm", "abs", "ceil", "floor", "inner",
  "Hom", "Aut", "End", "Spec", "Proj", "colim", "rank", "tr", "diag", "sgn", "supp", "id",
  "placeholder",
  "color", "textcolor", "colorbox",
  "href", "url",
  "coloneqq", "eqqcolon", "vcentcolon",
  "ifmmode", "fi",
  "theoremstyle", "newtheorem",
  "ZZ", "QQ", "RR", "CC", "NN", "FF", "PP", "Tr", "card", "Fold", "Res",
  "ord", "im", "coker", "Gal", "Cl", "vol",
]);

function findUnknownCommands(text: string): Set<string> {
  const unknown = new Set<string>();
  const regex = /\\([a-zA-Z]+)/g;
  let match;
  while ((match = regex.exec(text)) !== null) {
    const name = match[1];
    if (!KNOWN_COMMANDS.has(name)) {
      unknown.add(name);
    }
  }
  return unknown;
}

function getStringProperty(properties: Record<string, unknown>, key: string): string {
  const val = properties[key];
  if (val === undefined || val === null) return "";
  if (typeof val === "string") return val;
  return String(val);
}

function escapeLatex(text: string): string {
  const placeholder = "\x00BACKSLASH\x00";
  return text
    .replace(/\\/g, placeholder)
    .replace(/\{/g, "\\{")
    .replace(/\}/g, "\\}")
    .replace(/&/g, "\\&")
    .replace(/%/g, "\\%")
    .replace(/\$/g, "\\$")
    .replace(/#/g, "\\#")
    .replace(/_/g, "\\_")
    .replace(/~/g, "\\textasciitilde{}")
    .replace(/\^/g, "\\textasciicircum{}")
    .replace(new RegExp(placeholder.replace(/\x00/g, "\\x00"), "g"), "\\textbackslash{}");
}

export function generateLatex(sortedNodes: GraphNode[], edges: GraphEdge[]): string {
  const lines: string[] = [];

  // Preamble
  lines.push("\\documentclass[11pt,a4paper]{article}");
  lines.push("\\usepackage[utf8]{inputenc}");
  lines.push("\\usepackage{amsmath,amssymb,amsthm}");
  lines.push("\\usepackage{mathtools,mathrsfs,bm,amscd}");
  lines.push("\\usepackage{hyperref}");
  lines.push("\\usepackage{xcolor}");
  lines.push("\\newcommand{\\placeholder}[1]{\\textbf{#1}}");
  // Common LLM commands
  lines.push("\\providecommand{\\N}{\\mathbb{N}}");
  lines.push("\\providecommand{\\Z}{\\mathbb{Z}}");
  lines.push("\\providecommand{\\R}{\\mathbb{R}}");
  lines.push("\\providecommand{\\C}{\\mathbb{C}}");
  lines.push("\\providecommand{\\Q}{\\mathbb{Q}}");
  lines.push("\\providecommand{\\eps}{\\varepsilon}");
  lines.push("\\providecommand{\\ind}{\\mathbf{1}}");
  lines.push("\\providecommand{\\norm}[1]{\\lVert #1 \\rVert}");
  lines.push("\\providecommand{\\abs}[1]{\\lvert #1 \\rvert}");
  lines.push("\\providecommand{\\ceil}[1]{\\lceil #1 \\rceil}");
  lines.push("\\providecommand{\\floor}[1]{\\lfloor #1 \\rfloor}");
  lines.push("\\providecommand{\\inner}[2]{\\langle #1, #2 \\rangle}");
  lines.push("\\providecommand{\\Hom}{\\operatorname{Hom}}");
  lines.push("\\providecommand{\\Aut}{\\operatorname{Aut}}");
  lines.push("\\providecommand{\\End}{\\operatorname{End}}");
  lines.push("\\providecommand{\\Spec}{\\operatorname{Spec}}");
  lines.push("\\providecommand{\\Proj}{\\operatorname{Proj}}");
  lines.push("\\providecommand{\\colim}{\\operatorname{colim}}");
  lines.push("\\providecommand{\\rank}{\\operatorname{rank}}");
  lines.push("\\providecommand{\\tr}{\\operatorname{tr}}");
  lines.push("\\providecommand{\\diag}{\\operatorname{diag}}");
  lines.push("\\providecommand{\\sgn}{\\operatorname{sgn}}");
  lines.push("\\providecommand{\\supp}{\\operatorname{supp}}");
  lines.push("\\providecommand{\\id}{\\mathrm{id}}");
  lines.push("\\providecommand{\\ZZ}{\\mathbb{Z}}");
  lines.push("\\providecommand{\\QQ}{\\mathbb{Q}}");
  lines.push("\\providecommand{\\RR}{\\mathbb{R}}");
  lines.push("\\providecommand{\\CC}{\\mathbb{C}}");
  lines.push("\\providecommand{\\NN}{\\mathbb{N}}");
  lines.push("\\providecommand{\\FF}{\\mathbb{F}}");
  lines.push("\\providecommand{\\PP}{\\mathbb{P}}");
  lines.push("\\providecommand{\\Tr}{\\operatorname{Tr}}");
  lines.push("\\providecommand{\\card}[1]{\\lvert #1 \\rvert}");
  lines.push("\\providecommand{\\Fold}{\\operatorname{Fold}}");
  lines.push("\\providecommand{\\Res}{\\operatorname{Res}}");
  lines.push("\\providecommand{\\ord}{\\operatorname{ord}}");
  lines.push("\\providecommand{\\lcm}{\\operatorname{lcm}}");
  lines.push("\\providecommand{\\im}{\\operatorname{im}}");
  lines.push("\\providecommand{\\coker}{\\operatorname{coker}}");
  lines.push("\\providecommand{\\Gal}{\\operatorname{Gal}}");
  lines.push("\\providecommand{\\Cl}{\\operatorname{Cl}}");
  lines.push("\\providecommand{\\vol}{\\operatorname{vol}}");
  lines.push("");

  // amsthm environments
  lines.push("\\newtheorem{theorem}{Theorem}");
  lines.push("\\newtheorem{lemma}[theorem]{Lemma}");
  lines.push("\\newtheorem{proposition}[theorem]{Proposition}");
  lines.push("\\newtheorem{corollary}[theorem]{Corollary}");
  lines.push("\\newtheorem{conjecture}[theorem]{Conjecture}");
  lines.push("");
  lines.push("\\theoremstyle{definition}");
  lines.push("\\newtheorem{definition}[theorem]{Definition}");
  lines.push("\\newtheorem{example}[theorem]{Example}");
  lines.push("\\newtheorem{notation}[theorem]{Notation}");
  lines.push("\\newtheorem{axiom}[theorem]{Axiom}");
  lines.push("");
  lines.push("\\theoremstyle{remark}");
  lines.push("\\newtheorem{remark}[theorem]{Remark}");
  lines.push("\\newtheorem{observation}[theorem]{Observation}");
  lines.push("\\newtheorem{note}[theorem]{Note}");
  lines.push("\\newtheorem{conclusion}[theorem]{Conclusion}");

  // Pre-sanitize all bodies and abstracts
  const sanitizedBodies = new Map<string, string>();
  const sanitizedAbstracts = new Map<string, string>();

  for (const node of sortedNodes) {
    sanitizedBodies.set(node.id, sanitizeBody(getStringProperty(node.properties, "body")));
    const rawAbstract = getStringProperty(node.properties, "abstract");
    const sanAbstract = sanitizeBody(rawAbstract);
    sanitizedAbstracts.set(
      node.id,
      isBodyBroken(sanAbstract) ? stripToPlainText(rawAbstract) : sanAbstract
    );
  }

  // Auto-define unknown commands (streamed to avoid one giant concatenation)
  const unknownCommands = new Set<string>();
  for (const text of sanitizedBodies.values()) {
    for (const cmd of findUnknownCommands(text)) unknownCommands.add(cmd);
  }
  for (const text of sanitizedAbstracts.values()) {
    for (const cmd of findUnknownCommands(text)) unknownCommands.add(cmd);
  }
  for (const cmd of unknownCommands) {
    lines.push(`\\providecommand{\\${cmd}}{}`);
  }

  lines.push("");
  lines.push("\\begin{document}");
  lines.push("\\title{Knowledge Graph Paper}");
  lines.push("\\maketitle");
  lines.push("");

  // Build edge lookup for cross-references
  const nodeIdSet = new Set(sortedNodes.map((n) => n.id));
  const referencedBy = new Map<string, string[]>();
  for (const edge of edges) {
    if (!nodeIdSet.has(edge.source) || !nodeIdSet.has(edge.target)) continue;
    if (!referencedBy.has(edge.source)) referencedBy.set(edge.source, []);
    referencedBy.get(edge.source)!.push(edge.target);
  }

  // Emit each node
  for (const node of sortedNodes) {
    const label = `node:${node.id}`;
    const abstractText = sanitizedAbstracts.get(node.id) ?? "";
    const body = sanitizedBodies.get(node.id) ?? "";

    if (node.type === "proof") {
      lines.push("\\begin{proof}");
      lines.push(`\\label{${label}}`);
      if (abstractText.trim()) lines.push(`\\textit{${balanceBraces(abstractText)}}`);
      lines.push("");
      emitBodyContained(lines, body);
      emitCrossRefs(lines, node.id, referencedBy);
      lines.push("\\end{proof}");
    } else if (node.type in THEOREM_ENV_NAMES) {
      const envName = THEOREM_ENV_NAMES[node.type];
      lines.push(`\\begin{${envName}}`);
      lines.push(`\\label{${label}}`);
      if (abstractText.trim()) lines.push(`\\textit{${balanceBraces(abstractText)}}`);
      lines.push("");
      emitBodyContained(lines, body);
      emitCrossRefs(lines, node.id, referencedBy);
      lines.push(`\\end{${envName}}`);
    } else {
      lines.push(`\\paragraph{${escapeLatex(node.type)}}`);
      lines.push(`\\label{${label}}`);
      if (abstractText.trim()) lines.push(`\\textit{${balanceBraces(abstractText)}}`);
      lines.push("");
      emitBodyContained(lines, body);
      emitCrossRefs(lines, node.id, referencedBy);
    }

    lines.push("");
  }

  lines.push("\\end{document}");
  return lines.join("\n") + "\n";
}

function emitBodyContained(lines: string[], body: string): void {
  if (!body || !body.trim()) return;
  lines.push(body);
  lines.push("\\ifmmode\\)\\fi");
}

function emitCrossRefs(lines: string[], nodeId: string, referencedBy: Map<string, string[]>): void {
  const targets = referencedBy.get(nodeId);
  if (!targets || targets.length === 0) return;

  const refs = targets.map((t) => `\\ref{node:${t}}`).join(", ");
  lines.push(`\\noindent\\textit{See also: ${refs}}`);
}
