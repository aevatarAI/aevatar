import { Controller } from "tsoa";
import { type WorkflowDefinition } from "../models/workflow-definition.js";
import { type CompiledArtifact } from "../models/compiled-artifact.js";
import type { MainnetConnectorDto, MainnetRoleDto } from "../types.js";
interface CompiledOutput {
    workflowYaml: string;
    connectorJson: Record<string, unknown>[];
}
interface DeployResult {
    success: boolean;
    workflowId: string;
    revisionId?: string;
    result?: Record<string, unknown>;
}
export interface CompileFullResult {
    workflowYaml: string;
    connectorJson: Record<string, unknown>[];
    mainnetConnectors: MainnetConnectorDto[];
    mainnetRoles: MainnetRoleDto[];
    contentHash: string;
    workflow: WorkflowDefinition;
    artifact: CompiledArtifact;
}
/**
 * Core compile logic shared between compile endpoint and deploy pipeline.
 * Fetches workflow, resolves skills, builds YAML, maps DTOs, persists artifact.
 */
export declare function compileWorkflowFull(workflowId: string, authorization?: string): Promise<CompileFullResult>;
export declare class CompileError extends Error {
    statusCode: number;
    constructor(message: string, statusCode: number);
}
export declare class CompileController extends Controller {
    /**
     * Compile a workflow into YAML + connector JSON.
     * Resolves skill prompts from chrono-ornn, persists artifact, updates deployment status.
     * @summary Compile workflow
     */
    compile(workflowId: string, body?: {
        userToken?: string;
    }): Promise<CompiledOutput>;
}
export declare class DeployController extends Controller {
    /**
     * Compile and deploy a workflow to Aevatar mainnet.
     * Pipeline: compile -> upload connectors -> upload roles -> upload workflow (Scope API).
     * @summary Deploy workflow to mainnet
     */
    deploy(workflowId: string, authorization?: string, body?: {
        userToken?: string;
    }): Promise<DeployResult>;
}
export {};
//# sourceMappingURL=CompileController.d.ts.map