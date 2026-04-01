import pino from "pino";
const logger = pino({ name: "workflow:ornn-client" });
const ORNN_URL = process.env["CHRONO_ORNN_URL"] ?? "http://localhost:3001";
/**
 * Fetch skill content from chrono-ornn. Returns the skill markdown content
 * that gets injected as system_prompt into workflow roles.
 *
 * NEVER cached — each compile fetches latest from chrono-ornn.
 */
export async function fetchSkillContent(skillId) {
    const url = `${ORNN_URL}/api/agent/skills/${skillId}/json`;
    logger.info({ skillId, url }, "Fetching skill from chrono-ornn");
    const resp = await fetch(url, {
        signal: AbortSignal.timeout(15000),
    });
    if (!resp.ok) {
        const body = await resp.text();
        logger.error({ skillId, status: resp.status, body }, "Failed to fetch skill from chrono-ornn");
        if (resp.status === 502 || resp.status === 503 || resp.status === 504) {
            throw new OrnnUnreachableError(`chrono-ornn unreachable: HTTP ${resp.status}`);
        }
        throw new Error(`chrono-ornn GET /api/skills/${skillId}/json failed: HTTP ${resp.status}`);
    }
    const result = await resp.json();
    logger.info({ skillId, contentLength: result.content?.length }, "Skill fetched successfully");
    return {
        id: skillId,
        name: result.name ?? skillId,
        content: result.content ?? "",
    };
}
export class OrnnUnreachableError extends Error {
    constructor(message) {
        super(message);
        this.name = "OrnnUnreachableError";
    }
}
//# sourceMappingURL=ornn-client.js.map