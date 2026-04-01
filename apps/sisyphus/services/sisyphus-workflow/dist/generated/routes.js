import { fetchMiddlewares, ExpressTemplateService } from '@tsoa/runtime';
// WARNING: This file was auto-generated with tsoa. Please do not modify it. Re-run tsoa to re-generate this file: https://github.com/lukeautry/tsoa
import { WorkflowController } from './../controllers/WorkflowController.js';
// WARNING: This file was auto-generated with tsoa. Please do not modify it. Re-run tsoa to re-generate this file: https://github.com/lukeautry/tsoa
import { HealthController } from './../controllers/HealthController.js';
// WARNING: This file was auto-generated with tsoa. Please do not modify it. Re-run tsoa to re-generate this file: https://github.com/lukeautry/tsoa
import { ConnectorController } from './../controllers/ConnectorController.js';
// WARNING: This file was auto-generated with tsoa. Please do not modify it. Re-run tsoa to re-generate this file: https://github.com/lukeautry/tsoa
import { CompileController } from './../controllers/CompileController.js';
// WARNING: This file was auto-generated with tsoa. Please do not modify it. Re-run tsoa to re-generate this file: https://github.com/lukeautry/tsoa
import { DeployController } from './../controllers/CompileController.js';
// WARNING: This file was auto-generated with tsoa. Please do not modify it. Re-run tsoa to re-generate this file: https://github.com/lukeautry/tsoa
const models = {
    "RoleDefinition": {
        "dataType": "refObject",
        "properties": {
            "name": { "dataType": "string", "required": true },
            "skillId": { "dataType": "string" },
            "description": { "dataType": "string" },
        },
        "additionalProperties": false,
    },
    // WARNING: This file was auto-generated with tsoa. Please do not modify it. Re-run tsoa to re-generate this file: https://github.com/lukeautry/tsoa
    "Record_string.unknown_": {
        "dataType": "refAlias",
        "type": { "dataType": "nestedObjectLiteral", "nestedProperties": {}, "additionalProperties": { "dataType": "any" }, "validators": {} },
    },
    // WARNING: This file was auto-generated with tsoa. Please do not modify it. Re-run tsoa to re-generate this file: https://github.com/lukeautry/tsoa
    "StepDefinition": {
        "dataType": "refObject",
        "properties": {
            "name": { "dataType": "string", "required": true },
            "type": { "dataType": "string", "required": true },
            "order": { "dataType": "double", "required": true },
            "roleRef": { "dataType": "string" },
            "connectorRef": { "dataType": "string" },
            "parameters": { "ref": "Record_string.unknown_" },
        },
        "additionalProperties": false,
    },
    // WARNING: This file was auto-generated with tsoa. Please do not modify it. Re-run tsoa to re-generate this file: https://github.com/lukeautry/tsoa
    "DeploymentStatus": {
        "dataType": "refAlias",
        "type": { "dataType": "union", "subSchemas": [{ "dataType": "enum", "enums": ["draft"] }, { "dataType": "enum", "enums": ["compiled"] }, { "dataType": "enum", "enums": ["deployed"] }, { "dataType": "enum", "enums": ["out_of_sync"] }], "validators": {} },
    },
    // WARNING: This file was auto-generated with tsoa. Please do not modify it. Re-run tsoa to re-generate this file: https://github.com/lukeautry/tsoa
    "DeploymentState": {
        "dataType": "refObject",
        "properties": {
            "status": { "ref": "DeploymentStatus", "required": true },
            "contentHash": { "dataType": "string" },
            "lastCompiledAt": { "dataType": "string" },
            "lastDeployedAt": { "dataType": "string" },
            "lastDeployedArtifactId": { "dataType": "string" },
            "mainnetRevisionId": { "dataType": "string" },
            "deployError": { "dataType": "string" },
        },
        "additionalProperties": false,
    },
    // WARNING: This file was auto-generated with tsoa. Please do not modify it. Re-run tsoa to re-generate this file: https://github.com/lukeautry/tsoa
    "WorkflowDefinitionDTO": {
        "dataType": "refObject",
        "properties": {
            "id": { "dataType": "string", "required": true },
            "name": { "dataType": "string", "required": true },
            "description": { "dataType": "string", "required": true },
            "yaml": { "dataType": "string" },
            "roles": { "dataType": "array", "array": { "dataType": "refObject", "ref": "RoleDefinition" }, "required": true },
            "steps": { "dataType": "array", "array": { "dataType": "refObject", "ref": "StepDefinition" }, "required": true },
            "parameters": { "ref": "Record_string.unknown_" },
            "deploymentState": { "ref": "DeploymentState" },
            "createdAt": { "dataType": "string", "required": true },
            "updatedAt": { "dataType": "string", "required": true },
        },
        "additionalProperties": false,
    },
    // WARNING: This file was auto-generated with tsoa. Please do not modify it. Re-run tsoa to re-generate this file: https://github.com/lukeautry/tsoa
    "ErrorResponse": {
        "dataType": "refObject",
        "properties": {
            "error": { "dataType": "string", "required": true },
        },
        "additionalProperties": false,
    },
    // WARNING: This file was auto-generated with tsoa. Please do not modify it. Re-run tsoa to re-generate this file: https://github.com/lukeautry/tsoa
    "CreateWorkflowRequest": {
        "dataType": "refObject",
        "properties": {
            "name": { "dataType": "string", "required": true },
            "description": { "dataType": "string", "required": true },
            "yaml": { "dataType": "string" },
            "roles": { "dataType": "array", "array": { "dataType": "refObject", "ref": "RoleDefinition" }, "required": true },
            "steps": { "dataType": "array", "array": { "dataType": "refObject", "ref": "StepDefinition" }, "required": true },
            "parameters": { "ref": "Record_string.unknown_" },
        },
        "additionalProperties": false,
    },
    // WARNING: This file was auto-generated with tsoa. Please do not modify it. Re-run tsoa to re-generate this file: https://github.com/lukeautry/tsoa
    "WorkflowListResponse": {
        "dataType": "refObject",
        "properties": {
            "workflows": { "dataType": "array", "array": { "dataType": "refObject", "ref": "WorkflowDefinitionDTO" }, "required": true },
            "total": { "dataType": "double", "required": true },
            "page": { "dataType": "double", "required": true },
            "pageSize": { "dataType": "double", "required": true },
        },
        "additionalProperties": false,
    },
    // WARNING: This file was auto-generated with tsoa. Please do not modify it. Re-run tsoa to re-generate this file: https://github.com/lukeautry/tsoa
    "UpdateWorkflowRequest": {
        "dataType": "refObject",
        "properties": {
            "name": { "dataType": "string" },
            "description": { "dataType": "string" },
            "yaml": { "dataType": "string" },
            "roles": { "dataType": "array", "array": { "dataType": "refObject", "ref": "RoleDefinition" } },
            "steps": { "dataType": "array", "array": { "dataType": "refObject", "ref": "StepDefinition" } },
            "parameters": { "ref": "Record_string.unknown_" },
        },
        "additionalProperties": false,
    },
    // WARNING: This file was auto-generated with tsoa. Please do not modify it. Re-run tsoa to re-generate this file: https://github.com/lukeautry/tsoa
    "HealthResponse": {
        "dataType": "refObject",
        "properties": {
            "service": { "dataType": "string", "required": true },
            "status": { "dataType": "enum", "enums": ["ok"], "required": true },
        },
        "additionalProperties": false,
    },
    // WARNING: This file was auto-generated with tsoa. Please do not modify it. Re-run tsoa to re-generate this file: https://github.com/lukeautry/tsoa
    "AuthConfig": {
        "dataType": "refObject",
        "properties": {
            "type": { "dataType": "string", "required": true },
        },
        "additionalProperties": { "dataType": "any" },
    },
    // WARNING: This file was auto-generated with tsoa. Please do not modify it. Re-run tsoa to re-generate this file: https://github.com/lukeautry/tsoa
    "HttpEndpoint": {
        "dataType": "refObject",
        "properties": {
            "name": { "dataType": "string", "required": true },
            "method": { "dataType": "string", "required": true },
            "path": { "dataType": "string", "required": true },
        },
        "additionalProperties": false,
    },
    // WARNING: This file was auto-generated with tsoa. Please do not modify it. Re-run tsoa to re-generate this file: https://github.com/lukeautry/tsoa
    "ConnectorDefinitionDTO": {
        "dataType": "refObject",
        "properties": {
            "id": { "dataType": "string", "required": true },
            "name": { "dataType": "string", "required": true },
            "description": { "dataType": "string", "required": true },
            "type": { "dataType": "union", "subSchemas": [{ "dataType": "enum", "enums": ["http"] }, { "dataType": "enum", "enums": ["mcp"] }], "required": true },
            "baseUrl": { "dataType": "string" },
            "authConfig": { "ref": "AuthConfig" },
            "endpoints": { "dataType": "array", "array": { "dataType": "refObject", "ref": "HttpEndpoint" } },
            "mcpConfig": { "ref": "Record_string.unknown_" },
            "createdAt": { "dataType": "string", "required": true },
            "updatedAt": { "dataType": "string", "required": true },
        },
        "additionalProperties": false,
    },
    // WARNING: This file was auto-generated with tsoa. Please do not modify it. Re-run tsoa to re-generate this file: https://github.com/lukeautry/tsoa
    "CreateConnectorRequest": {
        "dataType": "refObject",
        "properties": {
            "name": { "dataType": "string", "required": true },
            "description": { "dataType": "string", "required": true },
            "type": { "dataType": "union", "subSchemas": [{ "dataType": "enum", "enums": ["http"] }, { "dataType": "enum", "enums": ["mcp"] }], "required": true },
            "baseUrl": { "dataType": "string" },
            "authConfig": { "ref": "AuthConfig" },
            "endpoints": { "dataType": "array", "array": { "dataType": "refObject", "ref": "HttpEndpoint" } },
            "mcpConfig": { "ref": "Record_string.unknown_" },
        },
        "additionalProperties": false,
    },
    // WARNING: This file was auto-generated with tsoa. Please do not modify it. Re-run tsoa to re-generate this file: https://github.com/lukeautry/tsoa
    "ConnectorListResponse": {
        "dataType": "refObject",
        "properties": {
            "connectors": { "dataType": "array", "array": { "dataType": "refObject", "ref": "ConnectorDefinitionDTO" }, "required": true },
            "total": { "dataType": "double", "required": true },
            "page": { "dataType": "double", "required": true },
            "pageSize": { "dataType": "double", "required": true },
        },
        "additionalProperties": false,
    },
    // WARNING: This file was auto-generated with tsoa. Please do not modify it. Re-run tsoa to re-generate this file: https://github.com/lukeautry/tsoa
    "UpdateConnectorRequest": {
        "dataType": "refObject",
        "properties": {
            "name": { "dataType": "string" },
            "description": { "dataType": "string" },
            "type": { "dataType": "union", "subSchemas": [{ "dataType": "enum", "enums": ["http"] }, { "dataType": "enum", "enums": ["mcp"] }] },
            "baseUrl": { "dataType": "string" },
            "authConfig": { "ref": "AuthConfig" },
            "endpoints": { "dataType": "array", "array": { "dataType": "refObject", "ref": "HttpEndpoint" } },
            "mcpConfig": { "ref": "Record_string.unknown_" },
        },
        "additionalProperties": false,
    },
    // WARNING: This file was auto-generated with tsoa. Please do not modify it. Re-run tsoa to re-generate this file: https://github.com/lukeautry/tsoa
    "Record_string.string_": {
        "dataType": "refAlias",
        "type": { "dataType": "nestedObjectLiteral", "nestedProperties": {}, "additionalProperties": { "dataType": "string" }, "validators": {} },
    },
    // WARNING: This file was auto-generated with tsoa. Please do not modify it. Re-run tsoa to re-generate this file: https://github.com/lukeautry/tsoa
    "MainnetConnectorDto": {
        "dataType": "refObject",
        "properties": {
            "Name": { "dataType": "string", "required": true },
            "Type": { "dataType": "string", "required": true },
            "Enabled": { "dataType": "boolean", "required": true },
            "TimeoutMs": { "dataType": "double", "required": true },
            "Retry": { "dataType": "double", "required": true },
            "Http": { "dataType": "nestedObjectLiteral", "nestedProperties": { "DefaultHeaders": { "ref": "Record_string.string_", "required": true }, "AllowedInputKeys": { "dataType": "array", "array": { "dataType": "string" }, "required": true }, "AllowedPaths": { "dataType": "array", "array": { "dataType": "string" }, "required": true }, "AllowedMethods": { "dataType": "array", "array": { "dataType": "string" }, "required": true }, "BaseUrl": { "dataType": "string", "required": true } }, "required": true },
            "Cli": { "dataType": "nestedObjectLiteral", "nestedProperties": { "Environment": { "ref": "Record_string.string_", "required": true }, "WorkingDirectory": { "dataType": "string", "required": true }, "AllowedInputKeys": { "dataType": "array", "array": { "dataType": "string" }, "required": true }, "AllowedOperations": { "dataType": "array", "array": { "dataType": "string" }, "required": true }, "FixedArguments": { "dataType": "array", "array": { "dataType": "string" }, "required": true }, "Command": { "dataType": "string", "required": true } }, "required": true },
            "Mcp": { "dataType": "nestedObjectLiteral", "nestedProperties": { "AllowedInputKeys": { "dataType": "array", "array": { "dataType": "string" }, "required": true }, "AllowedTools": { "dataType": "array", "array": { "dataType": "string" }, "required": true }, "DefaultTool": { "dataType": "string", "required": true }, "Environment": { "ref": "Record_string.string_", "required": true }, "Arguments": { "dataType": "array", "array": { "dataType": "string" }, "required": true }, "Command": { "dataType": "string", "required": true }, "ServerName": { "dataType": "string", "required": true } }, "required": true },
        },
        "additionalProperties": false,
    },
    // WARNING: This file was auto-generated with tsoa. Please do not modify it. Re-run tsoa to re-generate this file: https://github.com/lukeautry/tsoa
    "CompiledOutput": {
        "dataType": "refObject",
        "properties": {
            "workflowYaml": { "dataType": "string", "required": true },
            "connectorJson": { "dataType": "array", "array": { "dataType": "refAlias", "ref": "Record_string.unknown_" }, "required": true },
        },
        "additionalProperties": false,
    },
    // WARNING: This file was auto-generated with tsoa. Please do not modify it. Re-run tsoa to re-generate this file: https://github.com/lukeautry/tsoa
    "DeployResult": {
        "dataType": "refObject",
        "properties": {
            "success": { "dataType": "boolean", "required": true },
            "workflowId": { "dataType": "string", "required": true },
            "revisionId": { "dataType": "string" },
            "result": { "ref": "Record_string.unknown_" },
        },
        "additionalProperties": false,
    },
    // WARNING: This file was auto-generated with tsoa. Please do not modify it. Re-run tsoa to re-generate this file: https://github.com/lukeautry/tsoa
};
const templateService = new ExpressTemplateService(models, { "noImplicitAdditionalProperties": "throw-on-extras", "bodyCoercion": true });
// WARNING: This file was auto-generated with tsoa. Please do not modify it. Re-run tsoa to re-generate this file: https://github.com/lukeautry/tsoa
export function RegisterRoutes(app) {
    // ###########################################################################################################
    //  NOTE: If you do not see routes for all of your controllers in this file, then you might not have informed tsoa of where to look
    //      Please look into the "controllerPathGlobs" config option described in the readme: https://github.com/lukeautry/tsoa
    // ###########################################################################################################
    const argsWorkflowController_createWorkflow = {
        body: { "in": "body", "name": "body", "required": true, "ref": "CreateWorkflowRequest" },
    };
    app.post('/workflows', ...(fetchMiddlewares(WorkflowController)), ...(fetchMiddlewares(WorkflowController.prototype.createWorkflow)), async function WorkflowController_createWorkflow(request, response, next) {
        // WARNING: This file was auto-generated with tsoa. Please do not modify it. Re-run tsoa to re-generate this file: https://github.com/lukeautry/tsoa
        let validatedArgs = [];
        try {
            validatedArgs = templateService.getValidatedArgs({ args: argsWorkflowController_createWorkflow, request, response });
            const controller = new WorkflowController();
            await templateService.apiHandler({
                methodName: 'createWorkflow',
                controller,
                response,
                next,
                validatedArgs,
                successStatus: 201,
            });
        }
        catch (err) {
            return next(err);
        }
    });
    // WARNING: This file was auto-generated with tsoa. Please do not modify it. Re-run tsoa to re-generate this file: https://github.com/lukeautry/tsoa
    const argsWorkflowController_listWorkflows = {
        page: { "default": 1, "in": "query", "name": "page", "dataType": "double" },
        pageSize: { "default": 20, "in": "query", "name": "pageSize", "dataType": "double" },
    };
    app.get('/workflows', ...(fetchMiddlewares(WorkflowController)), ...(fetchMiddlewares(WorkflowController.prototype.listWorkflows)), async function WorkflowController_listWorkflows(request, response, next) {
        // WARNING: This file was auto-generated with tsoa. Please do not modify it. Re-run tsoa to re-generate this file: https://github.com/lukeautry/tsoa
        let validatedArgs = [];
        try {
            validatedArgs = templateService.getValidatedArgs({ args: argsWorkflowController_listWorkflows, request, response });
            const controller = new WorkflowController();
            await templateService.apiHandler({
                methodName: 'listWorkflows',
                controller,
                response,
                next,
                validatedArgs,
                successStatus: undefined,
            });
        }
        catch (err) {
            return next(err);
        }
    });
    // WARNING: This file was auto-generated with tsoa. Please do not modify it. Re-run tsoa to re-generate this file: https://github.com/lukeautry/tsoa
    const argsWorkflowController_getWorkflow = {
        workflowId: { "in": "path", "name": "workflowId", "required": true, "dataType": "string" },
    };
    app.get('/workflows/:workflowId', ...(fetchMiddlewares(WorkflowController)), ...(fetchMiddlewares(WorkflowController.prototype.getWorkflow)), async function WorkflowController_getWorkflow(request, response, next) {
        // WARNING: This file was auto-generated with tsoa. Please do not modify it. Re-run tsoa to re-generate this file: https://github.com/lukeautry/tsoa
        let validatedArgs = [];
        try {
            validatedArgs = templateService.getValidatedArgs({ args: argsWorkflowController_getWorkflow, request, response });
            const controller = new WorkflowController();
            await templateService.apiHandler({
                methodName: 'getWorkflow',
                controller,
                response,
                next,
                validatedArgs,
                successStatus: undefined,
            });
        }
        catch (err) {
            return next(err);
        }
    });
    // WARNING: This file was auto-generated with tsoa. Please do not modify it. Re-run tsoa to re-generate this file: https://github.com/lukeautry/tsoa
    const argsWorkflowController_updateWorkflow = {
        workflowId: { "in": "path", "name": "workflowId", "required": true, "dataType": "string" },
        body: { "in": "body", "name": "body", "required": true, "ref": "UpdateWorkflowRequest" },
    };
    app.put('/workflows/:workflowId', ...(fetchMiddlewares(WorkflowController)), ...(fetchMiddlewares(WorkflowController.prototype.updateWorkflow)), async function WorkflowController_updateWorkflow(request, response, next) {
        // WARNING: This file was auto-generated with tsoa. Please do not modify it. Re-run tsoa to re-generate this file: https://github.com/lukeautry/tsoa
        let validatedArgs = [];
        try {
            validatedArgs = templateService.getValidatedArgs({ args: argsWorkflowController_updateWorkflow, request, response });
            const controller = new WorkflowController();
            await templateService.apiHandler({
                methodName: 'updateWorkflow',
                controller,
                response,
                next,
                validatedArgs,
                successStatus: undefined,
            });
        }
        catch (err) {
            return next(err);
        }
    });
    // WARNING: This file was auto-generated with tsoa. Please do not modify it. Re-run tsoa to re-generate this file: https://github.com/lukeautry/tsoa
    const argsWorkflowController_deleteWorkflow = {
        workflowId: { "in": "path", "name": "workflowId", "required": true, "dataType": "string" },
    };
    app.delete('/workflows/:workflowId', ...(fetchMiddlewares(WorkflowController)), ...(fetchMiddlewares(WorkflowController.prototype.deleteWorkflow)), async function WorkflowController_deleteWorkflow(request, response, next) {
        // WARNING: This file was auto-generated with tsoa. Please do not modify it. Re-run tsoa to re-generate this file: https://github.com/lukeautry/tsoa
        let validatedArgs = [];
        try {
            validatedArgs = templateService.getValidatedArgs({ args: argsWorkflowController_deleteWorkflow, request, response });
            const controller = new WorkflowController();
            await templateService.apiHandler({
                methodName: 'deleteWorkflow',
                controller,
                response,
                next,
                validatedArgs,
                successStatus: 204,
            });
        }
        catch (err) {
            return next(err);
        }
    });
    // WARNING: This file was auto-generated with tsoa. Please do not modify it. Re-run tsoa to re-generate this file: https://github.com/lukeautry/tsoa
    const argsWorkflowController_getDeploymentStatus = {
        workflowId: { "in": "path", "name": "workflowId", "required": true, "dataType": "string" },
    };
    app.get('/workflows/:workflowId/deployment-status', ...(fetchMiddlewares(WorkflowController)), ...(fetchMiddlewares(WorkflowController.prototype.getDeploymentStatus)), async function WorkflowController_getDeploymentStatus(request, response, next) {
        // WARNING: This file was auto-generated with tsoa. Please do not modify it. Re-run tsoa to re-generate this file: https://github.com/lukeautry/tsoa
        let validatedArgs = [];
        try {
            validatedArgs = templateService.getValidatedArgs({ args: argsWorkflowController_getDeploymentStatus, request, response });
            const controller = new WorkflowController();
            await templateService.apiHandler({
                methodName: 'getDeploymentStatus',
                controller,
                response,
                next,
                validatedArgs,
                successStatus: undefined,
            });
        }
        catch (err) {
            return next(err);
        }
    });
    // WARNING: This file was auto-generated with tsoa. Please do not modify it. Re-run tsoa to re-generate this file: https://github.com/lukeautry/tsoa
    const argsHealthController_getHealth = {};
    app.get('/health', ...(fetchMiddlewares(HealthController)), ...(fetchMiddlewares(HealthController.prototype.getHealth)), async function HealthController_getHealth(request, response, next) {
        // WARNING: This file was auto-generated with tsoa. Please do not modify it. Re-run tsoa to re-generate this file: https://github.com/lukeautry/tsoa
        let validatedArgs = [];
        try {
            validatedArgs = templateService.getValidatedArgs({ args: argsHealthController_getHealth, request, response });
            const controller = new HealthController();
            await templateService.apiHandler({
                methodName: 'getHealth',
                controller,
                response,
                next,
                validatedArgs,
                successStatus: undefined,
            });
        }
        catch (err) {
            return next(err);
        }
    });
    // WARNING: This file was auto-generated with tsoa. Please do not modify it. Re-run tsoa to re-generate this file: https://github.com/lukeautry/tsoa
    const argsConnectorController_createConnector = {
        body: { "in": "body", "name": "body", "required": true, "ref": "CreateConnectorRequest" },
    };
    app.post('/connectors', ...(fetchMiddlewares(ConnectorController)), ...(fetchMiddlewares(ConnectorController.prototype.createConnector)), async function ConnectorController_createConnector(request, response, next) {
        // WARNING: This file was auto-generated with tsoa. Please do not modify it. Re-run tsoa to re-generate this file: https://github.com/lukeautry/tsoa
        let validatedArgs = [];
        try {
            validatedArgs = templateService.getValidatedArgs({ args: argsConnectorController_createConnector, request, response });
            const controller = new ConnectorController();
            await templateService.apiHandler({
                methodName: 'createConnector',
                controller,
                response,
                next,
                validatedArgs,
                successStatus: 201,
            });
        }
        catch (err) {
            return next(err);
        }
    });
    // WARNING: This file was auto-generated with tsoa. Please do not modify it. Re-run tsoa to re-generate this file: https://github.com/lukeautry/tsoa
    const argsConnectorController_listConnectors = {
        page: { "default": 1, "in": "query", "name": "page", "dataType": "double" },
        pageSize: { "default": 20, "in": "query", "name": "pageSize", "dataType": "double" },
    };
    app.get('/connectors', ...(fetchMiddlewares(ConnectorController)), ...(fetchMiddlewares(ConnectorController.prototype.listConnectors)), async function ConnectorController_listConnectors(request, response, next) {
        // WARNING: This file was auto-generated with tsoa. Please do not modify it. Re-run tsoa to re-generate this file: https://github.com/lukeautry/tsoa
        let validatedArgs = [];
        try {
            validatedArgs = templateService.getValidatedArgs({ args: argsConnectorController_listConnectors, request, response });
            const controller = new ConnectorController();
            await templateService.apiHandler({
                methodName: 'listConnectors',
                controller,
                response,
                next,
                validatedArgs,
                successStatus: undefined,
            });
        }
        catch (err) {
            return next(err);
        }
    });
    // WARNING: This file was auto-generated with tsoa. Please do not modify it. Re-run tsoa to re-generate this file: https://github.com/lukeautry/tsoa
    const argsConnectorController_getConnector = {
        connectorId: { "in": "path", "name": "connectorId", "required": true, "dataType": "string" },
    };
    app.get('/connectors/:connectorId', ...(fetchMiddlewares(ConnectorController)), ...(fetchMiddlewares(ConnectorController.prototype.getConnector)), async function ConnectorController_getConnector(request, response, next) {
        // WARNING: This file was auto-generated with tsoa. Please do not modify it. Re-run tsoa to re-generate this file: https://github.com/lukeautry/tsoa
        let validatedArgs = [];
        try {
            validatedArgs = templateService.getValidatedArgs({ args: argsConnectorController_getConnector, request, response });
            const controller = new ConnectorController();
            await templateService.apiHandler({
                methodName: 'getConnector',
                controller,
                response,
                next,
                validatedArgs,
                successStatus: undefined,
            });
        }
        catch (err) {
            return next(err);
        }
    });
    // WARNING: This file was auto-generated with tsoa. Please do not modify it. Re-run tsoa to re-generate this file: https://github.com/lukeautry/tsoa
    const argsConnectorController_updateConnector = {
        connectorId: { "in": "path", "name": "connectorId", "required": true, "dataType": "string" },
        body: { "in": "body", "name": "body", "required": true, "ref": "UpdateConnectorRequest" },
    };
    app.put('/connectors/:connectorId', ...(fetchMiddlewares(ConnectorController)), ...(fetchMiddlewares(ConnectorController.prototype.updateConnector)), async function ConnectorController_updateConnector(request, response, next) {
        // WARNING: This file was auto-generated with tsoa. Please do not modify it. Re-run tsoa to re-generate this file: https://github.com/lukeautry/tsoa
        let validatedArgs = [];
        try {
            validatedArgs = templateService.getValidatedArgs({ args: argsConnectorController_updateConnector, request, response });
            const controller = new ConnectorController();
            await templateService.apiHandler({
                methodName: 'updateConnector',
                controller,
                response,
                next,
                validatedArgs,
                successStatus: undefined,
            });
        }
        catch (err) {
            return next(err);
        }
    });
    // WARNING: This file was auto-generated with tsoa. Please do not modify it. Re-run tsoa to re-generate this file: https://github.com/lukeautry/tsoa
    const argsConnectorController_deleteConnector = {
        connectorId: { "in": "path", "name": "connectorId", "required": true, "dataType": "string" },
    };
    app.delete('/connectors/:connectorId', ...(fetchMiddlewares(ConnectorController)), ...(fetchMiddlewares(ConnectorController.prototype.deleteConnector)), async function ConnectorController_deleteConnector(request, response, next) {
        // WARNING: This file was auto-generated with tsoa. Please do not modify it. Re-run tsoa to re-generate this file: https://github.com/lukeautry/tsoa
        let validatedArgs = [];
        try {
            validatedArgs = templateService.getValidatedArgs({ args: argsConnectorController_deleteConnector, request, response });
            const controller = new ConnectorController();
            await templateService.apiHandler({
                methodName: 'deleteConnector',
                controller,
                response,
                next,
                validatedArgs,
                successStatus: 204,
            });
        }
        catch (err) {
            return next(err);
        }
    });
    // WARNING: This file was auto-generated with tsoa. Please do not modify it. Re-run tsoa to re-generate this file: https://github.com/lukeautry/tsoa
    const argsConnectorController_compileConnector = {
        connectorId: { "in": "path", "name": "connectorId", "required": true, "dataType": "string" },
    };
    app.get('/connectors/:connectorId/compile', ...(fetchMiddlewares(ConnectorController)), ...(fetchMiddlewares(ConnectorController.prototype.compileConnector)), async function ConnectorController_compileConnector(request, response, next) {
        // WARNING: This file was auto-generated with tsoa. Please do not modify it. Re-run tsoa to re-generate this file: https://github.com/lukeautry/tsoa
        let validatedArgs = [];
        try {
            validatedArgs = templateService.getValidatedArgs({ args: argsConnectorController_compileConnector, request, response });
            const controller = new ConnectorController();
            await templateService.apiHandler({
                methodName: 'compileConnector',
                controller,
                response,
                next,
                validatedArgs,
                successStatus: undefined,
            });
        }
        catch (err) {
            return next(err);
        }
    });
    // WARNING: This file was auto-generated with tsoa. Please do not modify it. Re-run tsoa to re-generate this file: https://github.com/lukeautry/tsoa
    const argsConnectorController_syncConnectors = {
        authorization: { "in": "header", "name": "Authorization", "dataType": "string" },
    };
    app.post('/connectors/sync', ...(fetchMiddlewares(ConnectorController)), ...(fetchMiddlewares(ConnectorController.prototype.syncConnectors)), async function ConnectorController_syncConnectors(request, response, next) {
        // WARNING: This file was auto-generated with tsoa. Please do not modify it. Re-run tsoa to re-generate this file: https://github.com/lukeautry/tsoa
        let validatedArgs = [];
        try {
            validatedArgs = templateService.getValidatedArgs({ args: argsConnectorController_syncConnectors, request, response });
            const controller = new ConnectorController();
            await templateService.apiHandler({
                methodName: 'syncConnectors',
                controller,
                response,
                next,
                validatedArgs,
                successStatus: undefined,
            });
        }
        catch (err) {
            return next(err);
        }
    });
    // WARNING: This file was auto-generated with tsoa. Please do not modify it. Re-run tsoa to re-generate this file: https://github.com/lukeautry/tsoa
    const argsCompileController_compile = {
        workflowId: { "in": "path", "name": "workflowId", "required": true, "dataType": "string" },
        body: { "in": "body", "name": "body", "dataType": "nestedObjectLiteral", "nestedProperties": { "userToken": { "dataType": "string" } } },
    };
    app.post('/compile/:workflowId', ...(fetchMiddlewares(CompileController)), ...(fetchMiddlewares(CompileController.prototype.compile)), async function CompileController_compile(request, response, next) {
        // WARNING: This file was auto-generated with tsoa. Please do not modify it. Re-run tsoa to re-generate this file: https://github.com/lukeautry/tsoa
        let validatedArgs = [];
        try {
            validatedArgs = templateService.getValidatedArgs({ args: argsCompileController_compile, request, response });
            const controller = new CompileController();
            await templateService.apiHandler({
                methodName: 'compile',
                controller,
                response,
                next,
                validatedArgs,
                successStatus: undefined,
            });
        }
        catch (err) {
            return next(err);
        }
    });
    // WARNING: This file was auto-generated with tsoa. Please do not modify it. Re-run tsoa to re-generate this file: https://github.com/lukeautry/tsoa
    const argsDeployController_deploy = {
        workflowId: { "in": "path", "name": "workflowId", "required": true, "dataType": "string" },
        authorization: { "in": "header", "name": "Authorization", "dataType": "string" },
        body: { "in": "body", "name": "body", "dataType": "nestedObjectLiteral", "nestedProperties": { "userToken": { "dataType": "string" } } },
    };
    app.post('/deploy/:workflowId', ...(fetchMiddlewares(DeployController)), ...(fetchMiddlewares(DeployController.prototype.deploy)), async function DeployController_deploy(request, response, next) {
        // WARNING: This file was auto-generated with tsoa. Please do not modify it. Re-run tsoa to re-generate this file: https://github.com/lukeautry/tsoa
        let validatedArgs = [];
        try {
            validatedArgs = templateService.getValidatedArgs({ args: argsDeployController_deploy, request, response });
            const controller = new DeployController();
            await templateService.apiHandler({
                methodName: 'deploy',
                controller,
                response,
                next,
                validatedArgs,
                successStatus: undefined,
            });
        }
        catch (err) {
            return next(err);
        }
    });
    // WARNING: This file was auto-generated with tsoa. Please do not modify it. Re-run tsoa to re-generate this file: https://github.com/lukeautry/tsoa
    // WARNING: This file was auto-generated with tsoa. Please do not modify it. Re-run tsoa to re-generate this file: https://github.com/lukeautry/tsoa
    // WARNING: This file was auto-generated with tsoa. Please do not modify it. Re-run tsoa to re-generate this file: https://github.com/lukeautry/tsoa
}
// WARNING: This file was auto-generated with tsoa. Please do not modify it. Re-run tsoa to re-generate this file: https://github.com/lukeautry/tsoa
//# sourceMappingURL=routes.js.map