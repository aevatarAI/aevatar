import pino from "pino";

const logger = pino({ name: "paper-compiler:sanitizer" });

const UNICODE_REPLACEMENTS: Record<string, string> = {
  // Greek letters
  "α": "$\\alpha$", "β": "$\\beta$", "γ": "$\\gamma$", "δ": "$\\delta$",
  "ε": "$\\varepsilon$", "ζ": "$\\zeta$", "η": "$\\eta$", "θ": "$\\theta$",
  "ι": "$\\iota$", "κ": "$\\kappa$", "λ": "$\\lambda$", "μ": "$\\mu$",
  "ν": "$\\nu$", "ξ": "$\\xi$", "π": "$\\pi$", "ρ": "$\\rho$",
  "σ": "$\\sigma$", "τ": "$\\tau$", "υ": "$\\upsilon$", "φ": "$\\varphi$",
  "χ": "$\\chi$", "ψ": "$\\psi$", "ω": "$\\omega$",
  "Γ": "$\\Gamma$", "Δ": "$\\Delta$", "Θ": "$\\Theta$", "Λ": "$\\Lambda$",
  "Ξ": "$\\Xi$", "Π": "$\\Pi$", "Σ": "$\\Sigma$", "Φ": "$\\Phi$",
  "Ψ": "$\\Psi$", "Ω": "$\\Omega$",
  // Math symbols
  "≠": "$\\neq$", "≤": "$\\leq$", "≥": "$\\geq$", "≈": "$\\approx$",
  "∈": "$\\in$", "∉": "$\\notin$", "⊂": "$\\subset$", "⊃": "$\\supset$",
  "⊆": "$\\subseteq$", "⊇": "$\\supseteq$", "∪": "$\\cup$", "∩": "$\\cap$",
  "∅": "$\\emptyset$", "∞": "$\\infty$", "∀": "$\\forall$", "∃": "$\\exists$",
  "¬": "$\\neg$", "∧": "$\\wedge$", "∨": "$\\vee$",
  "→": "$\\to$", "←": "$\\leftarrow$", "↔": "$\\leftrightarrow$",
  "⇒": "$\\Rightarrow$", "⇐": "$\\Leftarrow$", "⇔": "$\\Leftrightarrow$",
  "×": "$\\times$", "÷": "$\\div$", "±": "$\\pm$", "∓": "$\\mp$",
  "·": "$\\cdot$", "∘": "$\\circ$", "⊗": "$\\otimes$", "⊕": "$\\oplus$",
  "∑": "$\\sum$", "∏": "$\\prod$", "∫": "$\\int$",
  "√": "$\\sqrt{}$", "∂": "$\\partial$", "∇": "$\\nabla$",
  "ℝ": "$\\mathbb{R}$", "ℤ": "$\\mathbb{Z}$", "ℕ": "$\\mathbb{N}$",
  "ℂ": "$\\mathbb{C}$", "ℚ": "$\\mathbb{Q}$",
  "⟨": "$\\langle$", "⟩": "$\\rangle$",
  "‖": "$\\|$", "′": "$'$",
  "ℓ": "$\\ell$", "¹": "$^1$", "²": "$^2$", "³": "$^3$",
};

const DANGEROUS_ENVS = [
  "theorem", "lemma", "definition", "corollary", "conjecture",
  "proposition", "remark", "conclusion", "example", "notation",
  "axiom", "observation", "note", "proof", "document",
];

const MATH_ENVIRONMENTS = new Set([
  "equation", "equation*", "align", "align*", "gather", "gather*",
  "multline", "multline*", "flalign", "flalign*", "split",
  "aligned", "gathered", "cases", "array", "matrix", "pmatrix",
  "bmatrix", "vmatrix", "Vmatrix", "Bmatrix", "smallmatrix",
  "eqnarray", "eqnarray*", "math", "displaymath", "CD",
]);

// Hoisted regex patterns — avoid rebuilding per sanitizeBody() call
const RE_DANGEROUS_COMMANDS = /\\(input|include|usepackage|documentclass|bibliography|bibliographystyle)\b[^\\]*?(\n|$)/g;
const RE_DANGEROUS_INCLUDE = /\\(input|include)\{[^}]*\}/g;
const RE_ITEMIZE_OPEN = /\\begin\{itemize\}/g;
const RE_ITEMIZE_CLOSE = /\\end\{itemize\}/g;
const RE_ENUMERATE_OPEN = /\\begin\{enumerate\}/g;
const RE_ENUMERATE_CLOSE = /\\end\{enumerate\}/g;
const RE_ENV_MATCH = /\\(begin|end)\{([^}]+)\}/g;
const RE_DOUBLE_BACKSLASH_DISPLAY = /\\\\[\[\]]\s*(\n|$)/;
const LIST_ENV_REGEXES: Record<string, { open: RegExp; close: RegExp }> = {
  itemize: { open: RE_ITEMIZE_OPEN, close: RE_ITEMIZE_CLOSE },
  enumerate: { open: RE_ENUMERATE_OPEN, close: RE_ENUMERATE_CLOSE },
};

const MATH_ONLY_COMMANDS = new Set([
  "alpha", "beta", "gamma", "delta", "epsilon", "varepsilon", "zeta", "eta",
  "theta", "vartheta", "iota", "kappa", "lambda", "mu", "nu", "xi", "pi",
  "rho", "sigma", "tau", "upsilon", "phi", "varphi", "chi", "psi", "omega",
  "Gamma", "Delta", "Theta", "Lambda", "Xi", "Pi", "Sigma", "Phi", "Psi", "Omega",
  "to", "rightarrow", "leftarrow", "leftrightarrow", "Rightarrow", "Leftarrow",
  "Leftrightarrow", "implies", "iff", "mapsto", "longmapsto", "longrightarrow",
  "Longrightarrow", "Longleftrightarrow", "hookrightarrow", "hookleftarrow",
  "leq", "geq", "neq", "approx", "equiv", "sim", "simeq", "cong", "propto",
  "subset", "supset", "subseteq", "supseteq", "in", "notin", "ni", "preceq",
  "prec", "gg", "ll",
  "frac", "dfrac", "tfrac", "sqrt", "sum", "prod", "int", "oint", "iint",
  "bigcup", "bigcap", "bigoplus", "bigotimes", "bigsqcup",
  "cdot", "cdots", "ldots", "times", "otimes", "oplus", "circ",
  "cup", "cap", "setminus", "wedge", "vee",
  "lim", "limsup", "liminf", "sup", "inf", "infty", "partial", "nabla",
  "langle", "rangle", "lfloor", "rfloor", "lceil", "rceil",
  "bigl", "bigr", "Bigl", "Bigr", "biggl", "biggr", "Biggl", "Biggr",
  "overline", "underline", "widehat", "widetilde", "hat", "tilde", "bar",
  "vec", "dot", "ddot", "boldsymbol", "mathbb", "mathcal", "mathfrak",
  "mathrm", "mathscr", "binom", "boxed", "substack",
  "forall", "exists", "neg", "lnot",
]);

export function isBodyBroken(body: string): boolean {
  if (!body || !body.trim()) return false;

  let braceDepth = 0;
  let mathDepth = 0;
  let negBraceHits = 0;
  let mathErrors = 0;

  for (let i = 0; i < body.length; i++) {
    const c = body[i];

    if (c === "\\" && i + 1 < body.length) {
      const next = body[i + 1];

      if (next === "$" || next === "{" || next === "}" || next === "\\" || next === "_" || next === "^") {
        i++;
        continue;
      }

      if (next === "(") { mathDepth++; i++; continue; }
      if (next === ")") { mathDepth = Math.max(0, mathDepth - 1); i++; continue; }
      if (next === "[") { mathDepth++; i++; continue; }
      if (next === "]") { mathDepth = Math.max(0, mathDepth - 1); i++; continue; }

      if (/[a-zA-Z]/.test(next)) {
        const cmdStart = i + 1;
        let j = cmdStart;
        while (j < body.length && /[a-zA-Z]/.test(body[j])) j++;
        const cmd = body.slice(cmdStart, j);

        if ((cmd === "begin" || cmd === "end") && j < body.length && body[j] === "{") {
          const envStart = j + 1;
          const envEnd = body.indexOf("}", envStart);
          if (envEnd > envStart) {
            const envName = body.slice(envStart, envEnd);
            if (MATH_ENVIRONMENTS.has(envName)) {
              if (cmd === "begin") mathDepth++;
              else mathDepth = Math.max(0, mathDepth - 1);
            }
          }
        }

        if (mathDepth === 0 && MATH_ONLY_COMMANDS.has(cmd)) {
          mathErrors++;
        }

        i = j - 1;
        continue;
      }

      i++;
      continue;
    }

    switch (c) {
      case "{":
        braceDepth++;
        break;
      case "}":
        braceDepth--;
        if (braceDepth < 0) negBraceHits++;
        break;
      case "$":
        if (i + 1 < body.length && body[i + 1] === "$") {
          if (mathDepth > 0) mathDepth--;
          else mathDepth++;
          i++;
        } else {
          if (mathDepth > 0) mathDepth--;
          else mathDepth++;
        }
        break;
      case "_":
      case "^":
        if (mathDepth === 0) mathErrors++;
        break;
    }
  }

  if (negBraceHits > 0) return true;
  if (braceDepth !== 0) return true;
  if (mathErrors > 0) return true;

  // Check for unmatched environments
  const envStack: string[] = [];
  RE_ENV_MATCH.lastIndex = 0;
  let match;
  while ((match = RE_ENV_MATCH.exec(body)) !== null) {
    const action = match[1];
    const env = match[2];
    if (action === "begin") {
      envStack.push(env);
    } else if (envStack.length > 0 && envStack[envStack.length - 1] === env) {
      envStack.pop();
    } else {
      return true;
    }
  }
  if (envStack.length > 0) return true;

  if (RE_DOUBLE_BACKSLASH_DISPLAY.test(body)) return true;

  return false;
}

export function stripToPlainText(text: string): string {
  if (!text || !text.trim()) return text;

  let result = text.replace(/\\(begin|end)\{[^}]*\}/g, " ");
  result = result.replace(/\\[a-zA-Z]+\[[^\]]*\]\{([^}]*)\}/g, "$1");
  result = result.replace(/\\[a-zA-Z]+\{([^}]*)\}/g, "$1");
  result = result.replace(/\\[a-zA-Z]+/g, " ");
  result = result.replace(/\\\[|\\\]|\$\$?|\\\(|\\\)/g, " ");
  result = result.replace(/\\./g, "");
  result = result.replace(/[{}]/g, "");
  result = result.replace(/\s+/g, " ").trim();
  result = result
    .replace(/&/g, "\\&")
    .replace(/%/g, "\\%")
    .replace(/#/g, "\\#")
    .replace(/_/g, "\\_")
    .replace(/~/g, " ")
    .replace(/\^/g, " ");
  return result;
}

export function balanceBraces(body: string): string {
  const sb: string[] = [];
  let depth = 0;

  for (const c of body) {
    if (c === "{") {
      depth++;
      sb.push(c);
    } else if (c === "}") {
      if (depth > 0) {
        depth--;
        sb.push(c);
      }
    } else {
      sb.push(c);
    }
  }

  while (depth > 0) {
    sb.push("}");
    depth--;
  }

  return sb.join("");
}

export function sanitizeBody(body: string): string {
  if (!body || !body.trim()) return body;

  // Strip control characters
  body = body.replace(/[\x00-\x08\x0B\x0C\x0E-\x1F\x7F]/g, "");

  // De-double-escape LaTeX commands from JSON serialization
  body = body.replace(/\\textbackslash\{\}/g, "\\");
  body = body.replace(/\\\\([a-zA-Z])/g, "\\$1");
  body = body.replace(/\\\\([()])/g, "\\$1");
  body = body.replace(/\\\\\^/g, "^");
  body = body.replace(/\\\\_/g, "_");
  body = body.replace(/\\\\\{/g, "\\{");
  body = body.replace(/\\\\\}/g, "\\}");
  body = body.replace(/\\\\;/g, "\\;");
  body = body.replace(/\\\\,/g, "\\,");
  body = body.replace(/\\\\!/g, "\\!");
  body = body.replace(/\\\\:/g, "\\:");

  // Escape bare # (parameter char)
  body = body.replace(/(?<!\\)#/g, "\\#");

  // Strip dangerous commands
  RE_DANGEROUS_COMMANDS.lastIndex = 0;
  body = body.replace(RE_DANGEROUS_COMMANDS, "\n");
  RE_DANGEROUS_INCLUDE.lastIndex = 0;
  body = body.replace(RE_DANGEROUS_INCLUDE, "");

  // Strip theorem-like environments from body content
  for (const env of DANGEROUS_ENVS) {
    body = body.replaceAll(`\\begin{${env}}`, "");
    body = body.replaceAll(`\\end{${env}}`, "");
  }

  // Strip unmatched list environments
  for (const listEnv of ["itemize", "enumerate"] as const) {
    const { open, close } = LIST_ENV_REGEXES[listEnv];
    open.lastIndex = 0;
    close.lastIndex = 0;
    const opens = (body.match(open) || []).length;
    const closes = (body.match(close) || []).length;
    if (opens !== closes) {
      body = body.replaceAll(`\\begin{${listEnv}}`, "");
      body = body.replaceAll(`\\end{${listEnv}}`, "");
      body = body.replaceAll("\\item", "• ");
    }
  }

  // Replace Unicode math/Greek characters
  for (const [unicode, latex] of Object.entries(UNICODE_REPLACEMENTS)) {
    body = body.replaceAll(unicode, latex);
  }

  // Remove backtick references
  body = body.replaceAll("`", "'");

  // Escape _ and ^ inside \text commands
  body = body.replace(/\\text(tt|rm|bf|it|sf|sc)?\{([^}]*)\}/g, (_match, _suffix, inner: string) => {
    const prefix = _match.slice(0, _match.length - inner.length - 1);
    let cleaned = inner.replace(/_/g, "\\_").replace(/\^/g, "\\^{}");
    cleaned = cleaned.replace(/\\\\_/g, "\\_").replace(/\\\\\^\{\}/g, "\\^{}");
    return prefix + cleaned + "}";
  });

  // Balance braces
  body = balanceBraces(body);

  // If body still has structural issues, strip to plain text
  if (isBodyBroken(body)) {
    logger.debug("Body broken after sanitization, stripping to plain text");
    return stripToPlainText(body);
  }

  return body;
}
