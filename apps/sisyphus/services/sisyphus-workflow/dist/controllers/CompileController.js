var __decorate = (this && this.__decorate) || function (decorators, target, key, desc) {
    var c = arguments.length, r = c < 3 ? target : desc === null ? desc = Object.getOwnPropertyDescriptor(target, key) : desc, d;
    if (typeof Reflect === "object" && typeof Reflect.decorate === "function") r = Reflect.decorate(decorators, target, key, desc);
    else for (var i = decorators.length - 1; i >= 0; i--) if (d = decorators[i]) r = (c < 3 ? d(r) : c > 3 ? d(target, key, r) : d(target, key)) || r;
    return c > 3 && r && Object.defineProperty(target, key, r), r;
};
var __metadata = (this && this.__metadata) || function (k, v) {
    if (typeof Reflect === "object" && typeof Reflect.metadata === "function") return Reflect.metadata(k, v);
};
var __param = (this && this.__param) || function (paramIndex, decorator) {
    return function (target, key) { decorator(target, key, paramIndex); }
};
import { Body, Controller, Path, Post, Header, Route, Response, Tags, } from "tsoa";
import pino from "pino";
import { randomUUID } from "node:crypto";
import yaml from "js-yaml";
import { getDb } from "../db.js";
import { getWorkflowCollection } from "../models/workflow-definition.js";
import { getConnectorCollection } from "../models/connector-definition.js";
import { getCompiledArtifactCollection } from "../models/compiled-artifact.js";
import { fetchSkillContent, OrnnUnreachableError } from "../ornn-client.js";
import { resolveReferences } from "../reference-resolver.js";
import { computeContentHash } from "../content-hash.js";
import { mapConnectorToMainnet, mapRoleToMainnet } from "../dto-mappers.js";
import { uploadWorkflow } from "../mainnet-client.js";
const logger = pino({ name: "workflow:compile-controller" });
/**
 * Core compile logic shared between compile endpoint and deploy pipeline.
 * Fetches workflow, resolves skills, builds YAML, maps DTOs, persists artifact.
 */
export async function compileWorkflowFull(workflowId, authorization) {
    const db = getDb();
    const workflowCol = getWorkflowCollection(db);
    const connectorCol = getConnectorCollection(db);
    const artifactCol = getCompiledArtifactCollection(db);
    const workflow = await workflowCol.findOne({ id: workflowId });
    if (!workflow) {
        throw new CompileError("Workflow not found", 404);
    }
    logger.info({ workflowId, name: workflow.name, hasYaml: !!workflow.yaml }, "Compiling workflow");
    let workflowYaml;
    if (workflow.yaml) {
        // Use stored YAML directly (contains /skill-xxx /schema-xxx references)
        workflowYaml = workflow.yaml;
        logger.info({ workflowId }, "Using stored YAML field for compilation");
    }
    else {
        // Fallback: build YAML from structured roles/steps
        const compiledRoles = [];
        const rolePrompts = new Map();
        for (const role of workflow.roles) {
            const compiledRole = {
                name: role.name,
                description: role.description,
            };
            if (role.skillId) {
                const skill = await fetchSkillContent(role.skillId);
                compiledRole.system_prompt = skill.content;
                rolePrompts.set(role.name, skill.content);
            }
            else {
                rolePrompts.set(role.name, (role.description ?? ""));
            }
            compiledRoles.push(compiledRole);
        }
        const workflowObj = {
            name: workflow.name,
            description: workflow.description,
            roles: compiledRoles,
            steps: workflow.steps.map((step) => ({
                name: step.name,
                type: step.type,
                order: step.order,
                role: step.roleRef,
                connector: step.connectorRef,
                parameters: step.parameters,
            })),
            parameters: workflow.parameters,
        };
        workflowYaml = yaml.dump(workflowObj, { lineWidth: 120, noRefs: true });
    }
    // Resolve /skill-xxx and /schema-xxx references in the YAML
    workflowYaml = await resolveReferences(workflowYaml, authorization);
    // Parse resolved YAML to extract connector refs and roles for mainnet mapping
    let parsedYaml = {};
    try {
        parsedYaml = yaml.load(workflowYaml) ?? {};
    }
    catch { /* use empty */ }
    const yamlSteps = (parsedYaml.steps ?? workflow.steps ?? []);
    const yamlRoles = (parsedYaml.roles ?? workflow.roles ?? []);
    // Fetch referenced connector definitions
    const connectorRefs = new Set();
    const extractConnectors = (steps) => {
        for (const s of steps) {
            const conn = s.connector ?? s.connectorRef ?? s.parameters?.connector;
            if (conn)
                connectorRefs.add(conn);
            if (Array.isArray(s.children))
                extractConnectors(s.children);
        }
    };
    extractConnectors(yamlSteps);
    const connectorJson = [];
    const referencedConnectors = [];
    for (const connectorName of connectorRefs) {
        const connector = await connectorCol.findOne({ name: connectorName });
        if (connector) {
            const { _id, ...connDef } = connector;
            connectorJson.push(connDef);
            referencedConnectors.push(connector);
        }
    }
    const mainnetConnectors = referencedConnectors.map(mapConnectorToMainnet);
    // Build mainnet roles from resolved YAML
    const mainnetRoles = yamlRoles.map((role) => mapRoleToMainnet({ name: role.name ?? role.id ?? '' }, role.system_prompt ?? role.description ?? '', [...connectorRefs]));
    // Compute content hash
    const contentHash = computeContentHash(workflow, referencedConnectors);
    // Persist compiled artifact (upsert by workflowId)
    const now = new Date().toISOString();
    const artifact = {
        id: randomUUID(),
        workflowId,
        workflowYaml,
        connectorJson,
        mainnetConnectors,
        mainnetRoles,
        contentHash,
        compiledAt: now,
    };
    await artifactCol.updateOne({ workflowId }, { $set: artifact }, { upsert: true });
    // Update workflow deployment state to "compiled"
    await workflowCol.updateOne({ id: workflowId }, {
        $set: {
            "deploymentState.status": "compiled",
            "deploymentState.contentHash": contentHash,
            "deploymentState.lastCompiledAt": now,
            "deploymentState.lastDeployedArtifactId": artifact.id,
        },
    });
    logger.info({ workflowId, yamlLength: workflowYaml.length, connectorCount: connectorJson.length, contentHash }, "Workflow compiled successfully");
    return { workflowYaml, connectorJson, mainnetConnectors, mainnetRoles, contentHash, workflow, artifact };
}
export class CompileError extends Error {
    statusCode;
    constructor(message, statusCode) {
        super(message);
        this.statusCode = statusCode;
        this.name = "CompileError";
    }
}
let CompileController = class CompileController extends Controller {
    /**
     * Compile a workflow into YAML + connector JSON.
     * Resolves skill prompts from chrono-ornn, persists artifact, updates deployment status.
     * @summary Compile workflow
     */
    async compile(workflowId, body) {
        try {
            const bearerToken = body?.userToken ? `Bearer ${body.userToken}` : undefined;
            const result = await compileWorkflowFull(workflowId, bearerToken);
            return { workflowYaml: result.workflowYaml, connectorJson: result.connectorJson };
        }
        catch (err) {
            if (err instanceof CompileError) {
                this.setStatus(err.statusCode);
                return { error: err.message };
            }
            if (err instanceof OrnnUnreachableError) {
                this.setStatus(502);
                return { error: err.message };
            }
            throw err;
        }
    }
};
__decorate([
    Post("{workflowId}"),
    Response(404, "Workflow not found"),
    Response(502, "chrono-ornn unreachable"),
    __param(0, Path()),
    __param(1, Body()),
    __metadata("design:type", Function),
    __metadata("design:paramtypes", [String, Object]),
    __metadata("design:returntype", Promise)
], CompileController.prototype, "compile", null);
CompileController = __decorate([
    Route("compile"),
    Tags("Compile")
], CompileController);
export { CompileController };
let DeployController = class DeployController extends Controller {
    /**
     * Compile and deploy a workflow to Aevatar mainnet.
     * Pipeline: compile -> upload connectors -> upload roles -> upload workflow (Scope API).
     * @summary Deploy workflow to mainnet
     */
    async deploy(workflowId, authorization, body) {
        const db = getDb();
        const workflowCol = getWorkflowCollection(db);
        // Step 1: Compile
        let compiled;
        try {
            const bearerForOrnn = body?.userToken ? `Bearer ${body.userToken}` : authorization;
            compiled = await compileWorkflowFull(workflowId, bearerForOrnn);
        }
        catch (err) {
            if (err instanceof CompileError) {
                this.setStatus(err.statusCode);
                return { error: err.message };
            }
            if (err instanceof OrnnUnreachableError) {
                this.setStatus(502);
                return { error: err.message };
            }
            throw err;
        }
        logger.info({ workflowId }, "Starting deploy pipeline to Aevatar mainnet");
        const userAuth = body?.userToken ? `Bearer ${body.userToken}` : authorization;
        try {
            // Connectors and roles are managed by aevatar engine independently
            // (via ~/.aevatar/connectors.json and workflow YAML roles section)
            // Only upload the compiled workflow YAML to the scope
            const result = await uploadWorkflow(workflowId, compiled.workflowYaml, compiled.workflow.name, undefined, compiled.workflow.deploymentState?.mainnetRevisionId, userAuth);
            // Step 5: Update deployment state to "deployed"
            const now = new Date().toISOString();
            await workflowCol.updateOne({ id: workflowId }, {
                $set: {
                    "deploymentState.status": "deployed",
                    "deploymentState.contentHash": compiled.contentHash,
                    "deploymentState.lastCompiledAt": compiled.artifact.compiledAt,
                    "deploymentState.lastDeployedAt": now,
                    "deploymentState.lastDeployedArtifactId": compiled.artifact.id,
                    "deploymentState.mainnetRevisionId": result.RevisionId ?? undefined,
                },
                $unset: {
                    "deploymentState.deployError": "",
                },
            });
            logger.info({ workflowId, result }, "Workflow deployed successfully");
            return {
                success: true,
                workflowId,
                revisionId: result.RevisionId ?? undefined,
                result,
            };
        }
        catch (err) {
            const message = err instanceof Error ? err.message : "Unknown error";
            logger.error({ workflowId, err: message }, "Deploy pipeline failed");
            // Record the error on the workflow
            await workflowCol.updateOne({ id: workflowId }, { $set: { "deploymentState.deployError": message } });
            this.setStatus(502);
            return { error: `Deploy failed: ${message}` };
        }
    }
};
__decorate([
    Post("{workflowId}"),
    Response(404, "Workflow not found"),
    Response(502, "Deploy failed"),
    __param(0, Path()),
    __param(1, Header("Authorization")),
    __param(2, Body()),
    __metadata("design:type", Function),
    __metadata("design:paramtypes", [String, String, Object]),
    __metadata("design:returntype", Promise)
], DeployController.prototype, "deploy", null);
DeployController = __decorate([
    Route("deploy"),
    Tags("Deploy")
], DeployController);
export { DeployController };
//# sourceMappingURL=CompileController.js.map