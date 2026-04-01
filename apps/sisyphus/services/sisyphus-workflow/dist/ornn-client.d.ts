export interface SkillContent {
    id: string;
    name: string;
    content: string;
}
/**
 * Fetch skill content from chrono-ornn. Returns the skill markdown content
 * that gets injected as system_prompt into workflow roles.
 *
 * NEVER cached — each compile fetches latest from chrono-ornn.
 */
export declare function fetchSkillContent(skillId: string): Promise<SkillContent>;
export declare class OrnnUnreachableError extends Error {
    constructor(message: string);
}
//# sourceMappingURL=ornn-client.d.ts.map