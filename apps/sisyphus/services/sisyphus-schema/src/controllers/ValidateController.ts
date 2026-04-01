import {
  Body,
  Controller,
  Example,
  Post,
  Response,
  Route,
  SuccessResponse,
  Tags,
} from "tsoa";
import { createValidator, type ValidationResult } from "../validator.js";

/**
 * The request body for JSON Schema validation.
 */
interface ValidateRequest {
  /**
   * A JSON Schema (draft-07) object that defines the expected structure of the data.
   * This can be any valid JSON Schema, including complex schemas with nested objects,
   * arrays, conditionals, and format validations.
   *
   * @example { "type": "object", "required": ["name"], "properties": { "name": { "type": "string" } } }
   */
  schema?: unknown;

  /**
   * The JSON data to validate against the provided schema.
   * Can be any valid JSON value — object, array, string, number, boolean, or null.
   *
   * @example { "name": "test" }
   */
  data?: unknown;
}

/**
 * Error response returned when the request body is malformed
 * (e.g., missing `schema` or `data` fields).
 */
interface ValidateErrorResponse {
  /** Always `false` for error responses. */
  valid: false;
  /** Human-readable error messages describing what went wrong. */
  errors: string[];
}

const validator = createValidator();

/**
 * Validates arbitrary JSON data against a caller-provided JSON Schema (draft-07).
 *
 * This controller powers the core validation endpoint used by Sisyphus workflow
 * `connector_call` steps to verify that LLM-generated payloads (blue/black nodes
 * and edges) conform to the expected structure before they are persisted to the
 * knowledge graph.
 */
@Route("validate")
@Tags("Validation")
export class ValidateController extends Controller {
  /**
   * Validate JSON data against a JSON Schema.
   *
   * Accepts a JSON Schema and a data payload, then validates the data against the
   * schema using Ajv (draft-07 with formats). Returns whether the data is valid
   * and, if not, a list of human-readable error messages describing each violation.
   *
   * Compiled schemas are cached (LRU, up to 500 entries) for performance — repeated
   * validations against the same schema skip recompilation.
   *
   * @summary Validate data against a JSON Schema
   */
  @Post()
  @SuccessResponse(200, "Validation completed")
  @Response<ValidateErrorResponse>(400, "Bad Request — missing schema or data")
  @Example<ValidationResult>({
    valid: true,
  })
  @Example<ValidationResult>({
    valid: false,
    errors: ["/name must be string"],
  })
  public async validate(
    @Body() body: ValidateRequest
  ): Promise<ValidationResult> {
    const { schema, data } = body;

    if (schema === undefined || data === undefined) {
      this.setStatus(400);
      return {
        valid: false,
        errors: ["Request body must include 'schema' and 'data' fields"],
      };
    }

    return validator.validate(schema, data);
  }
}
