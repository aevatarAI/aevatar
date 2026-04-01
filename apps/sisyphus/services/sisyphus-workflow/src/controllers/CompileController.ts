import {
  Body,
  Controller,
  Get,
  Path,
  Post,
  Header,
  Route,
  Response,
  Tags,
} from "tsoa";
import pino from "pino";
import { randomUUID } from "node:crypto";
import yaml from "js-yaml";
import { getDb } from "../db.js";
import { getWorkflowCollection, type WorkflowDefinition } from "../models/workflow-definition.js";
import { getConnectorCollection, type ConnectorDefinition } from "../models/connector-definition.js";
import { getCompiledArtifactCollection, type CompiledArtifact } from "../models/compiled-artifact.js";
import { fetchSkillContent, OrnnUnreachableError } from "../ornn-client.js";
import { resolveReferences } from "../reference-resolver.js";
import { computeContentHash } from "../content-hash.js";
import { mapConnectorToMainnet, mapRoleToMainnet } from "../dto-mappers.js";
import { uploadConnectors, uploadRoles, uploadWorkflow } from "../mainnet-client.js";
import type { ErrorResponse, MainnetConnectorDto, MainnetRoleDto } from "../types.js";

const logger = pino({ name: "workflow:compile-controller" });

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
export async function compileWorkflowFull(workflowId: string, authorization?: string): Promise<CompileFullResult> {
  const db = getDb();
  const workflowCol = getWorkflowCollection(db);
  const connectorCol = getConnectorCollection(db);
  const artifactCol = getCompiledArtifactCollection(db);

  const workflow = await workflowCol.findOne({ id: workflowId });
  if (!workflow) {
    throw new CompileError("Workflow not found", 404);
  }

  logger.info({ workflowId, name: workflow.name, hasYaml: !!workflow.yaml }, "Compiling workflow");

  let workflowYaml: string;

  if (workflow.yaml) {
    // Use stored YAML directly (contains /skill-xxx /schema-xxx references)
    workflowYaml = workflow.yaml;
    logger.info({ workflowId }, "Using stored YAML field for compilation");
  } else {
    // Fallback: build YAML from structured roles/steps
    const compiledRoles: Array<Record<string, unknown>> = [];
    const rolePrompts = new Map<string, string>();
    for (const role of workflow.roles) {
      const compiledRole: Record<string, unknown> = {
        name: role.name,
        description: role.description,
      };
      if (role.skillId) {
        const skill = await fetchSkillContent(role.skillId);
        compiledRole.system_prompt = skill.content;
        rolePrompts.set(role.name, skill.content);
      } else {
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
  let parsedYaml: Record<string, unknown> = {};
  try { parsedYaml = yaml.load(workflowYaml) as Record<string, unknown> ?? {}; } catch { /* use empty */ }

  const yamlSteps = (parsedYaml.steps ?? workflow.steps ?? []) as Array<Record<string, unknown>>;
  const yamlRoles = (parsedYaml.roles ?? workflow.roles ?? []) as Array<Record<string, unknown>>;

  // Fetch referenced connector definitions
  const connectorRefs = new Set<string>();
  const extractConnectors = (steps: Array<Record<string, unknown>>) => {
    for (const s of steps) {
      const conn = (s.connector as string) ?? (s.connectorRef as string) ?? (s.parameters as Record<string, string>)?.connector;
      if (conn) connectorRefs.add(conn);
      if (Array.isArray(s.children)) extractConnectors(s.children as Array<Record<string, unknown>>);
    }
  };
  extractConnectors(yamlSteps);

  const connectorJson: Record<string, unknown>[] = [];
  const referencedConnectors: ConnectorDefinition[] = [];
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
  const mainnetRoles = yamlRoles.map((role) =>
    mapRoleToMainnet(
      { name: (role.name as string) ?? (role.id as string) ?? '' },
      (role.system_prompt as string) ?? (role.description as string) ?? '',
      [...connectorRefs],
    ),
  );

  // Compute content hash
  const contentHash = computeContentHash(workflow, referencedConnectors);

  // Persist compiled artifact (upsert by workflowId)
  const now = new Date().toISOString();
  const artifact: CompiledArtifact = {
    id: randomUUID(),
    workflowId,
    workflowYaml,
    connectorJson,
    mainnetConnectors,
    mainnetRoles,
    contentHash,
    compiledAt: now,
  };

  await artifactCol.updateOne(
    { workflowId },
    { $set: artifact },
    { upsert: true },
  );

  // Update workflow deployment state to "compiled"
  await workflowCol.updateOne(
    { id: workflowId },
    {
      $set: {
        "deploymentState.status": "compiled",
        "deploymentState.contentHash": contentHash,
        "deploymentState.lastCompiledAt": now,
        "deploymentState.lastDeployedArtifactId": artifact.id,
      },
    },
  );

  logger.info(
    { workflowId, yamlLength: workflowYaml.length, connectorCount: connectorJson.length, contentHash },
    "Workflow compiled successfully",
  );

  return { workflowYaml, connectorJson, mainnetConnectors, mainnetRoles, contentHash, workflow, artifact };
}

export class CompileError extends Error {
  constructor(message: string, public statusCode: number) {
    super(message);
    this.name = "CompileError";
  }
}

@Route("compile")
@Tags("Compile")
export class CompileController extends Controller {
  /**
   * Compile a workflow into YAML + connector JSON.
   * Resolves skill prompts from chrono-ornn, persists artifact, updates deployment status.
   * @summary Compile workflow
   */
  @Post("{workflowId}")
  @Response<ErrorResponse>(404, "Workflow not found")
  @Response<ErrorResponse>(502, "chrono-ornn unreachable")
  public async compile(
    @Path() workflowId: string,
    @Body() body?: { userToken?: string },
  ): Promise<CompiledOutput> {
    try {
      const bearerToken = body?.userToken ? `Bearer ${body.userToken}` : undefined;
      const result = await compileWorkflowFull(workflowId, bearerToken);
      return { workflowYaml: result.workflowYaml, connectorJson: result.connectorJson };
    } catch (err) {
      if (err instanceof CompileError) {
        this.setStatus(err.statusCode);
        return { error: err.message } as unknown as CompiledOutput;
      }
      if (err instanceof OrnnUnreachableError) {
        this.setStatus(502);
        return { error: err.message } as unknown as CompiledOutput;
      }
      throw err;
    }
  }
}

@Route("deploy")
@Tags("Deploy")
export class DeployController extends Controller {
  /**
   * Compile and deploy a workflow to Aevatar mainnet.
   * Pipeline: compile -> upload connectors -> upload roles -> upload workflow (Scope API).
   * @summary Deploy workflow to mainnet
   */
  @Post("{workflowId}")
  @Response<ErrorResponse>(404, "Workflow not found")
  @Response<ErrorResponse>(502, "Deploy failed")
  public async deploy(
    @Path() workflowId: string,
    @Header("Authorization") authorization?: string,
    @Body() body?: { userToken?: string },
  ): Promise<DeployResult> {
    const db = getDb();
    const workflowCol = getWorkflowCollection(db);

    // Step 1: Compile
    let compiled: CompileFullResult;
    try {
      const bearerForOrnn = body?.userToken ? `Bearer ${body.userToken}` : authorization;
      compiled = await compileWorkflowFull(workflowId, bearerForOrnn);
    } catch (err) {
      if (err instanceof CompileError) {
        this.setStatus(err.statusCode);
        return { error: err.message } as unknown as DeployResult;
      }
      if (err instanceof OrnnUnreachableError) {
        this.setStatus(502);
        return { error: err.message } as unknown as DeployResult;
      }
      throw err;
    }

    logger.info({ workflowId }, "Starting deploy pipeline to Aevatar mainnet");

    const userAuth = body?.userToken ? `Bearer ${body.userToken}` : authorization;

    try {
      // Connectors and roles are managed by aevatar engine independently
      // (via ~/.aevatar/connectors.json and workflow YAML roles section)
      // Only upload the compiled workflow YAML to the scope
      const result = await uploadWorkflow(
        workflowId,
        compiled.workflowYaml,
        compiled.workflow.name,
        undefined,
        compiled.workflow.deploymentState?.mainnetRevisionId,
        userAuth,
      );

      // Step 5: Update deployment state to "deployed"
      const now = new Date().toISOString();
      await workflowCol.updateOne(
        { id: workflowId },
        {
          $set: {
            "deploymentState.status": "deployed",
            "deploymentState.contentHash": compiled.contentHash,
            "deploymentState.lastCompiledAt": compiled.artifact.compiledAt,
            "deploymentState.lastDeployedAt": now,
            "deploymentState.lastDeployedArtifactId": compiled.artifact.id,
            "deploymentState.mainnetRevisionId": (result.RevisionId as string) ?? undefined,
          },
          $unset: {
            "deploymentState.deployError": "",
          },
        },
      );

      logger.info({ workflowId, result }, "Workflow deployed successfully");

      return {
        success: true,
        workflowId,
        revisionId: (result.RevisionId as string) ?? undefined,
        result,
      };
    } catch (err) {
      const message = err instanceof Error ? err.message : "Unknown error";
      logger.error({ workflowId, err: message }, "Deploy pipeline failed");

      // Record the error on the workflow
      await workflowCol.updateOne(
        { id: workflowId },
        { $set: { "deploymentState.deployError": message } },
      );

      this.setStatus(502);
      return { error: `Deploy failed: ${message}` } as unknown as DeployResult;
    }
  }
}
