/* tslint:disable */
/* eslint-disable */
// WARNING: This file was auto-generated with tsoa. Please do not modify it. Re-run tsoa to re-generate this file: https://github.com/lukeautry/tsoa
import type { TsoaRoute } from '@tsoa/runtime';
import {  fetchMiddlewares, ExpressTemplateService } from '@tsoa/runtime';
// WARNING: This file was auto-generated with tsoa. Please do not modify it. Re-run tsoa to re-generate this file: https://github.com/lukeautry/tsoa
import { WorkflowRunController } from './../controllers/WorkflowRunController.js';
// WARNING: This file was auto-generated with tsoa. Please do not modify it. Re-run tsoa to re-generate this file: https://github.com/lukeautry/tsoa
import { HistoryController } from './../controllers/HistoryController.js';
// WARNING: This file was auto-generated with tsoa. Please do not modify it. Re-run tsoa to re-generate this file: https://github.com/lukeautry/tsoa
import { HealthController } from './../controllers/HealthController.js';
import type { Request as ExRequest, Response as ExResponse, RequestHandler, Router } from 'express';



// WARNING: This file was auto-generated with tsoa. Please do not modify it. Re-run tsoa to re-generate this file: https://github.com/lukeautry/tsoa

const models: TsoaRoute.Models = {
    "StartResponse": {
        "dataType": "refObject",
        "properties": {
            "sessionId": {"dataType":"string","required":true},
            "runId": {"dataType":"string","required":true},
            "workflowType": {"dataType":"string","required":true},
            "status": {"dataType":"string","required":true},
        },
        "additionalProperties": false,
    },
    // WARNING: This file was auto-generated with tsoa. Please do not modify it. Re-run tsoa to re-generate this file: https://github.com/lukeautry/tsoa
    "ErrorResponse": {
        "dataType": "refObject",
        "properties": {
            "error": {"dataType":"string","required":true},
        },
        "additionalProperties": false,
    },
    // WARNING: This file was auto-generated with tsoa. Please do not modify it. Re-run tsoa to re-generate this file: https://github.com/lukeautry/tsoa
    "WorkflowType": {
        "dataType": "refAlias",
        "type": {"dataType":"string","validators":{}},
    },
    // WARNING: This file was auto-generated with tsoa. Please do not modify it. Re-run tsoa to re-generate this file: https://github.com/lukeautry/tsoa
    "StartRequest": {
        "dataType": "refObject",
        "properties": {
            "mode": {"dataType":"string"},
            "direction": {"dataType":"string"},
            "runMode": {"dataType":"union","subSchemas":[{"dataType":"enum","enums":["draft"]},{"dataType":"enum","enums":["deployed"]}]},
            "userToken": {"dataType":"string"},
        },
        "additionalProperties": false,
    },
    // WARNING: This file was auto-generated with tsoa. Please do not modify it. Re-run tsoa to re-generate this file: https://github.com/lukeautry/tsoa
    "StopResponse": {
        "dataType": "refObject",
        "properties": {
            "sessionId": {"dataType":"string","required":true},
            "workflowType": {"dataType":"string","required":true},
            "status": {"dataType":"string","required":true},
        },
        "additionalProperties": false,
    },
    // WARNING: This file was auto-generated with tsoa. Please do not modify it. Re-run tsoa to re-generate this file: https://github.com/lukeautry/tsoa
    "SessionStatus": {
        "dataType": "refAlias",
        "type": {"dataType":"union","subSchemas":[{"dataType":"enum","enums":["running"]},{"dataType":"enum","enums":["stopped"]},{"dataType":"enum","enums":["completed"]},{"dataType":"enum","enums":["failed"]}],"validators":{}},
    },
    // WARNING: This file was auto-generated with tsoa. Please do not modify it. Re-run tsoa to re-generate this file: https://github.com/lukeautry/tsoa
    "SessionDTO": {
        "dataType": "refObject",
        "properties": {
            "id": {"dataType":"string","required":true},
            "runId": {"dataType":"string","required":true},
            "workflowType": {"ref":"WorkflowType","required":true},
            "status": {"ref":"SessionStatus","required":true},
            "triggeredBy": {"dataType":"string","required":true},
            "startedAt": {"dataType":"string","required":true},
            "stoppedAt": {"dataType":"string"},
            "duration": {"dataType":"double"},
            "error": {"dataType":"string"},
            "mode": {"dataType":"string"},
            "direction": {"dataType":"string"},
        },
        "additionalProperties": false,
    },
    // WARNING: This file was auto-generated with tsoa. Please do not modify it. Re-run tsoa to re-generate this file: https://github.com/lukeautry/tsoa
    "StatusResponse": {
        "dataType": "refObject",
        "properties": {
            "running": {"dataType":"boolean","required":true},
            "session": {"dataType":"union","subSchemas":[{"ref":"SessionDTO"},{"dataType":"enum","enums":[null]}],"required":true},
        },
        "additionalProperties": false,
    },
    // WARNING: This file was auto-generated with tsoa. Please do not modify it. Re-run tsoa to re-generate this file: https://github.com/lukeautry/tsoa
    "Record_string.StatusResponse_": {
        "dataType": "refAlias",
        "type": {"dataType":"nestedObjectLiteral","nestedProperties":{},"additionalProperties":{"ref":"StatusResponse"},"validators":{}},
    },
    // WARNING: This file was auto-generated with tsoa. Please do not modify it. Re-run tsoa to re-generate this file: https://github.com/lukeautry/tsoa
    "HistoryListResponse": {
        "dataType": "refObject",
        "properties": {
            "records": {"dataType":"array","array":{"dataType":"refObject","ref":"SessionDTO"},"required":true},
            "total": {"dataType":"double","required":true},
            "page": {"dataType":"double","required":true},
            "pageSize": {"dataType":"double","required":true},
        },
        "additionalProperties": false,
    },
    // WARNING: This file was auto-generated with tsoa. Please do not modify it. Re-run tsoa to re-generate this file: https://github.com/lukeautry/tsoa
    "Record_string.unknown_": {
        "dataType": "refAlias",
        "type": {"dataType":"nestedObjectLiteral","nestedProperties":{},"additionalProperties":{"dataType":"any"},"validators":{}},
    },
    // WARNING: This file was auto-generated with tsoa. Please do not modify it. Re-run tsoa to re-generate this file: https://github.com/lukeautry/tsoa
    "AgUiEvent": {
        "dataType": "refObject",
        "properties": {
            "sessionId": {"dataType":"string","required":true},
            "timestamp": {"dataType":"string","required":true},
            "eventType": {"dataType":"string","required":true},
            "payload": {"ref":"Record_string.unknown_","required":true},
            "createdAt": {"dataType":"datetime","required":true},
        },
        "additionalProperties": false,
    },
    // WARNING: This file was auto-generated with tsoa. Please do not modify it. Re-run tsoa to re-generate this file: https://github.com/lukeautry/tsoa
    "SessionDetailResponse": {
        "dataType": "refObject",
        "properties": {
            "session": {"ref":"SessionDTO","required":true},
            "events": {"dataType":"array","array":{"dataType":"refObject","ref":"AgUiEvent"},"required":true},
        },
        "additionalProperties": false,
    },
    // WARNING: This file was auto-generated with tsoa. Please do not modify it. Re-run tsoa to re-generate this file: https://github.com/lukeautry/tsoa
    "HealthResponse": {
        "dataType": "refObject",
        "properties": {
            "service": {"dataType":"string","required":true},
            "status": {"dataType":"enum","enums":["ok"],"required":true},
        },
        "additionalProperties": false,
    },
    // WARNING: This file was auto-generated with tsoa. Please do not modify it. Re-run tsoa to re-generate this file: https://github.com/lukeautry/tsoa
};
const templateService = new ExpressTemplateService(models, {"noImplicitAdditionalProperties":"throw-on-extras","bodyCoercion":true});

// WARNING: This file was auto-generated with tsoa. Please do not modify it. Re-run tsoa to re-generate this file: https://github.com/lukeautry/tsoa




export function RegisterRoutes(app: Router) {

    // ###########################################################################################################
    //  NOTE: If you do not see routes for all of your controllers in this file, then you might not have informed tsoa of where to look
    //      Please look into the "controllerPathGlobs" config option described in the readme: https://github.com/lukeautry/tsoa
    // ###########################################################################################################


    
        const argsWorkflowRunController_start: Record<string, TsoaRoute.ParameterSchema> = {
                workflowType: {"in":"path","name":"workflowType","required":true,"ref":"WorkflowType"},
                body: {"in":"body","name":"body","required":true,"ref":"StartRequest"},
        };
        app.post('/workflows/:workflowType/start',
            ...(fetchMiddlewares<RequestHandler>(WorkflowRunController)),
            ...(fetchMiddlewares<RequestHandler>(WorkflowRunController.prototype.start)),

            async function WorkflowRunController_start(request: ExRequest, response: ExResponse, next: any) {

            // WARNING: This file was auto-generated with tsoa. Please do not modify it. Re-run tsoa to re-generate this file: https://github.com/lukeautry/tsoa

            let validatedArgs: any[] = [];
            try {
                validatedArgs = templateService.getValidatedArgs({ args: argsWorkflowRunController_start, request, response });

                const controller = new WorkflowRunController();

              await templateService.apiHandler({
                methodName: 'start',
                controller,
                response,
                next,
                validatedArgs,
                successStatus: undefined,
              });
            } catch (err) {
                return next(err);
            }
        });
        // WARNING: This file was auto-generated with tsoa. Please do not modify it. Re-run tsoa to re-generate this file: https://github.com/lukeautry/tsoa
        const argsWorkflowRunController_stop: Record<string, TsoaRoute.ParameterSchema> = {
                workflowType: {"in":"path","name":"workflowType","required":true,"ref":"WorkflowType"},
        };
        app.post('/workflows/:workflowType/stop',
            ...(fetchMiddlewares<RequestHandler>(WorkflowRunController)),
            ...(fetchMiddlewares<RequestHandler>(WorkflowRunController.prototype.stop)),

            async function WorkflowRunController_stop(request: ExRequest, response: ExResponse, next: any) {

            // WARNING: This file was auto-generated with tsoa. Please do not modify it. Re-run tsoa to re-generate this file: https://github.com/lukeautry/tsoa

            let validatedArgs: any[] = [];
            try {
                validatedArgs = templateService.getValidatedArgs({ args: argsWorkflowRunController_stop, request, response });

                const controller = new WorkflowRunController();

              await templateService.apiHandler({
                methodName: 'stop',
                controller,
                response,
                next,
                validatedArgs,
                successStatus: undefined,
              });
            } catch (err) {
                return next(err);
            }
        });
        // WARNING: This file was auto-generated with tsoa. Please do not modify it. Re-run tsoa to re-generate this file: https://github.com/lukeautry/tsoa
        const argsWorkflowRunController_status: Record<string, TsoaRoute.ParameterSchema> = {
                workflowType: {"in":"path","name":"workflowType","required":true,"ref":"WorkflowType"},
        };
        app.get('/workflows/:workflowType/status',
            ...(fetchMiddlewares<RequestHandler>(WorkflowRunController)),
            ...(fetchMiddlewares<RequestHandler>(WorkflowRunController.prototype.status)),

            async function WorkflowRunController_status(request: ExRequest, response: ExResponse, next: any) {

            // WARNING: This file was auto-generated with tsoa. Please do not modify it. Re-run tsoa to re-generate this file: https://github.com/lukeautry/tsoa

            let validatedArgs: any[] = [];
            try {
                validatedArgs = templateService.getValidatedArgs({ args: argsWorkflowRunController_status, request, response });

                const controller = new WorkflowRunController();

              await templateService.apiHandler({
                methodName: 'status',
                controller,
                response,
                next,
                validatedArgs,
                successStatus: undefined,
              });
            } catch (err) {
                return next(err);
            }
        });
        // WARNING: This file was auto-generated with tsoa. Please do not modify it. Re-run tsoa to re-generate this file: https://github.com/lukeautry/tsoa
        const argsWorkflowRunController_allStatus: Record<string, TsoaRoute.ParameterSchema> = {
        };
        app.get('/workflows/status',
            ...(fetchMiddlewares<RequestHandler>(WorkflowRunController)),
            ...(fetchMiddlewares<RequestHandler>(WorkflowRunController.prototype.allStatus)),

            async function WorkflowRunController_allStatus(request: ExRequest, response: ExResponse, next: any) {

            // WARNING: This file was auto-generated with tsoa. Please do not modify it. Re-run tsoa to re-generate this file: https://github.com/lukeautry/tsoa

            let validatedArgs: any[] = [];
            try {
                validatedArgs = templateService.getValidatedArgs({ args: argsWorkflowRunController_allStatus, request, response });

                const controller = new WorkflowRunController();

              await templateService.apiHandler({
                methodName: 'allStatus',
                controller,
                response,
                next,
                validatedArgs,
                successStatus: undefined,
              });
            } catch (err) {
                return next(err);
            }
        });
        // WARNING: This file was auto-generated with tsoa. Please do not modify it. Re-run tsoa to re-generate this file: https://github.com/lukeautry/tsoa
        const argsHistoryController_listHistory: Record<string, TsoaRoute.ParameterSchema> = {
                page: {"default":1,"in":"query","name":"page","dataType":"double"},
                pageSize: {"default":20,"in":"query","name":"pageSize","dataType":"double"},
                workflowType: {"in":"query","name":"workflowType","dataType":"string"},
                status: {"in":"query","name":"status","dataType":"string"},
                startDate: {"in":"query","name":"startDate","dataType":"string"},
                endDate: {"in":"query","name":"endDate","dataType":"string"},
        };
        app.get('/history',
            ...(fetchMiddlewares<RequestHandler>(HistoryController)),
            ...(fetchMiddlewares<RequestHandler>(HistoryController.prototype.listHistory)),

            async function HistoryController_listHistory(request: ExRequest, response: ExResponse, next: any) {

            // WARNING: This file was auto-generated with tsoa. Please do not modify it. Re-run tsoa to re-generate this file: https://github.com/lukeautry/tsoa

            let validatedArgs: any[] = [];
            try {
                validatedArgs = templateService.getValidatedArgs({ args: argsHistoryController_listHistory, request, response });

                const controller = new HistoryController();

              await templateService.apiHandler({
                methodName: 'listHistory',
                controller,
                response,
                next,
                validatedArgs,
                successStatus: undefined,
              });
            } catch (err) {
                return next(err);
            }
        });
        // WARNING: This file was auto-generated with tsoa. Please do not modify it. Re-run tsoa to re-generate this file: https://github.com/lukeautry/tsoa
        const argsHistoryController_getSessionDetail: Record<string, TsoaRoute.ParameterSchema> = {
                sessionId: {"in":"path","name":"sessionId","required":true,"dataType":"string"},
        };
        app.get('/history/:sessionId',
            ...(fetchMiddlewares<RequestHandler>(HistoryController)),
            ...(fetchMiddlewares<RequestHandler>(HistoryController.prototype.getSessionDetail)),

            async function HistoryController_getSessionDetail(request: ExRequest, response: ExResponse, next: any) {

            // WARNING: This file was auto-generated with tsoa. Please do not modify it. Re-run tsoa to re-generate this file: https://github.com/lukeautry/tsoa

            let validatedArgs: any[] = [];
            try {
                validatedArgs = templateService.getValidatedArgs({ args: argsHistoryController_getSessionDetail, request, response });

                const controller = new HistoryController();

              await templateService.apiHandler({
                methodName: 'getSessionDetail',
                controller,
                response,
                next,
                validatedArgs,
                successStatus: undefined,
              });
            } catch (err) {
                return next(err);
            }
        });
        // WARNING: This file was auto-generated with tsoa. Please do not modify it. Re-run tsoa to re-generate this file: https://github.com/lukeautry/tsoa
        const argsHealthController_getHealth: Record<string, TsoaRoute.ParameterSchema> = {
        };
        app.get('/health',
            ...(fetchMiddlewares<RequestHandler>(HealthController)),
            ...(fetchMiddlewares<RequestHandler>(HealthController.prototype.getHealth)),

            async function HealthController_getHealth(request: ExRequest, response: ExResponse, next: any) {

            // WARNING: This file was auto-generated with tsoa. Please do not modify it. Re-run tsoa to re-generate this file: https://github.com/lukeautry/tsoa

            let validatedArgs: any[] = [];
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
            } catch (err) {
                return next(err);
            }
        });
        // WARNING: This file was auto-generated with tsoa. Please do not modify it. Re-run tsoa to re-generate this file: https://github.com/lukeautry/tsoa

    // WARNING: This file was auto-generated with tsoa. Please do not modify it. Re-run tsoa to re-generate this file: https://github.com/lukeautry/tsoa


    // WARNING: This file was auto-generated with tsoa. Please do not modify it. Re-run tsoa to re-generate this file: https://github.com/lukeautry/tsoa
}

// WARNING: This file was auto-generated with tsoa. Please do not modify it. Re-run tsoa to re-generate this file: https://github.com/lukeautry/tsoa
