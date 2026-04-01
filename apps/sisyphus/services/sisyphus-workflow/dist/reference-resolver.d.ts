/**
 * Resolve /skill-xxx and /schema-xxx references in a string.
 * Handles YAML indentation: the substituted content is indented to match
 * the column where the reference appeared.
 */
export declare function resolveReferences(text: string, authorization?: string): Promise<string>;
//# sourceMappingURL=reference-resolver.d.ts.map