import Ajv, { type ErrorObject, type ValidateFunction } from "ajv";
import addFormats from "ajv-formats";
import { createHash } from "node:crypto";
import pino from "pino";

const logger = pino({ name: "schema-validator" });

const MAX_CACHE_SIZE = 500;

export interface ValidationResult {
  valid: boolean;
  errors?: string[];
}

function schemaHash(schema: unknown): string {
  return createHash("sha256")
    .update(JSON.stringify(schema))
    .digest("hex");
}

export function createValidator() {
  // Map preserves insertion order; delete+re-set = O(1) move-to-end for LRU
  const cache = new Map<string, ValidateFunction>();

  function getOrCompile(schema: unknown): ValidateFunction {
    const hash = schemaHash(schema);

    const cached = cache.get(hash);
    if (cached) {
      // O(1) move-to-end: delete and re-set preserves Map insertion order
      cache.delete(hash);
      cache.set(hash, cached);
      return cached;
    }

    // Evict oldest (first) entry if at capacity
    while (cache.size >= MAX_CACHE_SIZE) {
      const oldest = cache.keys().next().value;
      if (oldest) cache.delete(oldest);
    }

    // Create a fresh Ajv instance per compilation to avoid $id collisions
    // from different schemas sharing the same $id across requests
    const compileAjv = new Ajv({ allErrors: true, strict: false });
    addFormats(compileAjv);
    const validateFn = compileAjv.compile(schema as object);

    cache.set(hash, validateFn);

    return validateFn;
  }

  return {
    invalidate(schema: unknown): void {
      const hash = schemaHash(schema);
      if (cache.delete(hash)) {
        logger.info({ hash: hash.slice(0, 12) }, "Schema cache entry invalidated");
      }
    },

    validate(schema: unknown, data: unknown): ValidationResult {
      let validateFn: ValidateFunction;
      try {
        validateFn = getOrCompile(schema);
      } catch (err) {
        const message =
          err instanceof Error ? err.message : "Invalid schema";
        logger.warn({ err: message }, "Schema compilation failed");
        return { valid: false, errors: [`Schema error: ${message}`] };
      }

      const valid = validateFn(data);
      if (valid) {
        return { valid: true };
      }

      const errors = (validateFn.errors as ErrorObject[]).map(
        (e) => `${e.instancePath || "/"} ${e.message ?? "unknown error"}`
      );

      logger.debug({ errors }, "Validation failed");
      return { valid: false, errors };
    },
  };
}
