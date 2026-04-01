import pino from "pino";

const logger = pino({ name: "workflow:reference-resolver" });

// Internal k8s: ornn agent route — no auth needed, trusts proxy headers
const ORNN_API_URL = process.env["ORNN_API_URL"] ?? "http://ornn-skill.chronoai-platform:3802";
const SCHEMA_SERVICE_URL = process.env["SCHEMA_SERVICE_URL"] ?? "http://sisyphus-schema:8080";

const REF_REGEX = /\/(?:skill|schema)-[a-zA-Z0-9_-]+/g;

/**
 * Resolve /skill-xxx and /schema-xxx references in a string.
 * Handles YAML indentation: the substituted content is indented to match
 * the column where the reference appeared.
 */
export async function resolveReferences(text: string, authorization?: string): Promise<string> {
  const matches = [...text.matchAll(REF_REGEX)];
  if (matches.length === 0) return text;

  // Collect all replacements first, then apply from end to start to preserve offsets
  const replacements: Array<{ start: number; end: number; content: string }> = [];

  for (const match of matches) {
    const fullRef = match[0];
    const refStart = match.index!;
    const isSkill = fullRef.startsWith("/skill-");
    const name = fullRef.slice(isSkill ? "/skill-".length : "/schema-".length);

    try {
      let content: string | null = null;

      if (isSkill) {
        content = await fetchSkillMarkdown(name, authorization);
        if (content) logger.info({ ref: fullRef, contentLength: content.length }, "Resolved skill reference");
      } else {
        content = await fetchSchemaJson(name);
        if (content) logger.info({ ref: fullRef, contentLength: content.length }, "Resolved schema reference");
      }

      if (content) {
        // Compute the indentation: find the column of the reference in its line
        const lineStart = text.lastIndexOf('\n', refStart - 1) + 1;
        const indent = refStart - lineStart;
        const indentStr = ' '.repeat(indent);

        // Indent all lines of the content (except the first line which replaces in-place)
        const lines = content.split('\n');
        const indented = lines[0] + (lines.length > 1
          ? '\n' + lines.slice(1).map((l) => indentStr + l).join('\n')
          : '');

        replacements.push({ start: refStart, end: refStart + fullRef.length, content: indented });
      } else {
        logger.warn({ ref: fullRef }, "Content not found, leaving reference as-is");
      }
    } catch (err) {
      logger.warn({ ref: fullRef, err: (err as Error).message }, "Failed to resolve reference, leaving as-is");
    }
  }

  // Apply replacements from end to start so offsets stay valid
  let resolved = text;
  for (const r of replacements.reverse()) {
    resolved = resolved.slice(0, r.start) + r.content + resolved.slice(r.end);
  }

  return resolved;
}

/**
 * Fetch skill.md markdown content from ornn (without YAML frontmatter).
 */
async function fetchSkillMarkdown(name: string, _authorization?: string): Promise<string | null> {
  // Use agent route (internal k8s) — set admin permissions via proxy headers
  const headers: Record<string, string> = {
    "X-NyxID-User-Id": "sisyphus-compiler",
    "X-NyxID-User-Permissions": "ornn:skill:read,ornn:admin:skill",
  };

  const url = `${ORNN_API_URL}/api/agent/skills/${encodeURIComponent(name)}/json`;
  logger.info({ name, url }, "Fetching skill markdown from ornn (agent route)");

  const resp = await fetch(url, { headers, signal: AbortSignal.timeout(15000) });
  if (!resp.ok) {
    logger.error({ name, status: resp.status }, "Failed to fetch skill from ornn");
    return null;
  }

  const body = await resp.json() as { data?: { content?: string; files?: Record<string, string> } };

  // Files map keys may be SKILL.md or skill.md — check case-insensitively
  const files = body.data?.files ?? {};
  const skillFileKey = Object.keys(files).find((k) => k.toLowerCase() === "skill.md");
  let markdown = (skillFileKey ? files[skillFileKey] : null) ?? body.data?.content ?? "";

  // Strip YAML frontmatter (--- ... ---)
  if (markdown.startsWith("---")) {
    const endIdx = markdown.indexOf("---", 3);
    if (endIdx > 0) {
      markdown = markdown.slice(endIdx + 3).trim();
    }
  }

  return markdown || null;
}

/**
 * Fetch schema JSON from sisyphus-schema and return as formatted string.
 */
async function fetchSchemaJson(name: string): Promise<string | null> {
  try {
    const resp = await fetch(`${SCHEMA_SERVICE_URL}/schemas?pageSize=100`, {
      signal: AbortSignal.timeout(10000),
    });
    if (!resp.ok) return null;

    const data = await resp.json() as { schemas: Array<{ name: string; jsonSchema: Record<string, unknown> }> };
    const schema = (data.schemas ?? []).find((s) => s.name === name);
    if (!schema) return null;

    return JSON.stringify(schema.jsonSchema, null, 2);
  } catch (err) {
    logger.error({ name, err: (err as Error).message }, "Failed to fetch schema");
    return null;
  }
}
