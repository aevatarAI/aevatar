/* tslint:disable */
/* eslint-disable */
// WARNING: This file was auto-generated with tsoa. Please do not modify it. Re-run tsoa to re-generate this file: https://github.com/lukeautry/tsoa
import type { TsoaRoute } from '@tsoa/runtime';
import {  fetchMiddlewares, ExpressTemplateService } from '@tsoa/runtime';
// WARNING: This file was auto-generated with tsoa. Please do not modify it. Re-run tsoa to re-generate this file: https://github.com/lukeautry/tsoa
import { SettingsController } from './../controllers/SettingsController.js';
// WARNING: This file was auto-generated with tsoa. Please do not modify it. Re-run tsoa to re-generate this file: https://github.com/lukeautry/tsoa
import { HealthController } from './../controllers/HealthController.js';
// WARNING: This file was auto-generated with tsoa. Please do not modify it. Re-run tsoa to re-generate this file: https://github.com/lukeautry/tsoa
import { AuditController } from './../controllers/AuditController.js';
import type { Request as ExRequest, Response as ExResponse, RequestHandler, Router } from 'express';



// WARNING: This file was auto-generated with tsoa. Please do not modify it. Re-run tsoa to re-generate this file: https://github.com/lukeautry/tsoa

const models: TsoaRoute.Models = {
    "Pick_Settings.Exclude_keyofSettings._id__": {
        "dataType": "refAlias",
        "type": {"dataType":"nestedObjectLiteral","nestedProperties":{"id":{"dataType":"enum","enums":["global"],"required":true},"graphId":{"dataType":"string","required":true},"verifyCronIntervalHours":{"dataType":"double","required":true},"eventRetentionDays":{"dataType":"double","required":true},"defaultResearchMode":{"dataType":"union","subSchemas":[{"dataType":"enum","enums":["graph_based"]},{"dataType":"enum","enums":["exploration"]}],"required":true},"graphViewNodeLimit":{"dataType":"double","required":true},"updatedAt":{"dataType":"string","required":true},"updatedBy":{"dataType":"string"}},"validators":{}},
    },
    // WARNING: This file was auto-generated with tsoa. Please do not modify it. Re-run tsoa to re-generate this file: https://github.com/lukeautry/tsoa
    "Omit_Settings._id_": {
        "dataType": "refAlias",
        "type": {"ref":"Pick_Settings.Exclude_keyofSettings._id__","validators":{}},
    },
    // WARNING: This file was auto-generated with tsoa. Please do not modify it. Re-run tsoa to re-generate this file: https://github.com/lukeautry/tsoa
    "SettingsDTO": {
        "dataType": "refAlias",
        "type": {"ref":"Omit_Settings._id_","validators":{}},
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
    "UpdateSettingsRequest": {
        "dataType": "refObject",
        "properties": {
            "graphId": {"dataType":"string"},
            "verifyCronIntervalHours": {"dataType":"double"},
            "eventRetentionDays": {"dataType":"double"},
            "defaultResearchMode": {"dataType":"union","subSchemas":[{"dataType":"enum","enums":["graph_based"]},{"dataType":"enum","enums":["exploration"]}]},
            "graphViewNodeLimit": {"dataType":"double"},
        },
        "additionalProperties": false,
    },
    // WARNING: This file was auto-generated with tsoa. Please do not modify it. Re-run tsoa to re-generate this file: https://github.com/lukeautry/tsoa
    "AuditAction": {
        "dataType": "refAlias",
        "type": {"dataType":"union","subSchemas":[{"dataType":"enum","enums":["create"]},{"dataType":"enum","enums":["update"]},{"dataType":"enum","enums":["delete"]},{"dataType":"enum","enums":["compile"]},{"dataType":"enum","enums":["deploy"]},{"dataType":"enum","enums":["trigger"]},{"dataType":"enum","enums":["stop"]},{"dataType":"enum","enums":["login"]},{"dataType":"enum","enums":["logout"]}],"validators":{}},
    },
    // WARNING: This file was auto-generated with tsoa. Please do not modify it. Re-run tsoa to re-generate this file: https://github.com/lukeautry/tsoa
    "AuditResource": {
        "dataType": "refAlias",
        "type": {"dataType":"union","subSchemas":[{"dataType":"enum","enums":["workflow"]},{"dataType":"enum","enums":["connector"]},{"dataType":"enum","enums":["schema"]},{"dataType":"enum","enums":["settings"]},{"dataType":"enum","enums":["session"]},{"dataType":"enum","enums":["user"]}],"validators":{}},
    },
    // WARNING: This file was auto-generated with tsoa. Please do not modify it. Re-run tsoa to re-generate this file: https://github.com/lukeautry/tsoa
    "Record_string.unknown_": {
        "dataType": "refAlias",
        "type": {"dataType":"nestedObjectLiteral","nestedProperties":{},"additionalProperties":{"dataType":"any"},"validators":{}},
    },
    // WARNING: This file was auto-generated with tsoa. Please do not modify it. Re-run tsoa to re-generate this file: https://github.com/lukeautry/tsoa
    "Pick_AuditEvent.Exclude_keyofAuditEvent._id-or-createdAt__": {
        "dataType": "refAlias",
        "type": {"dataType":"nestedObjectLiteral","nestedProperties":{"id":{"dataType":"string","required":true},"timestamp":{"dataType":"string","required":true},"userId":{"dataType":"string","required":true},"userName":{"dataType":"string"},"action":{"ref":"AuditAction","required":true},"resource":{"ref":"AuditResource","required":true},"resourceId":{"dataType":"string"},"resourceName":{"dataType":"string"},"service":{"dataType":"string","required":true},"details":{"ref":"Record_string.unknown_"}},"validators":{}},
    },
    // WARNING: This file was auto-generated with tsoa. Please do not modify it. Re-run tsoa to re-generate this file: https://github.com/lukeautry/tsoa
    "Omit_AuditEvent._id-or-createdAt_": {
        "dataType": "refAlias",
        "type": {"ref":"Pick_AuditEvent.Exclude_keyofAuditEvent._id-or-createdAt__","validators":{}},
    },
    // WARNING: This file was auto-generated with tsoa. Please do not modify it. Re-run tsoa to re-generate this file: https://github.com/lukeautry/tsoa
    "AuditEventDTO": {
        "dataType": "refAlias",
        "type": {"dataType":"intersection","subSchemas":[{"ref":"Omit_AuditEvent._id-or-createdAt_"},{"dataType":"nestedObjectLiteral","nestedProperties":{"createdAt":{"dataType":"string","required":true}}}],"validators":{}},
    },
    // WARNING: This file was auto-generated with tsoa. Please do not modify it. Re-run tsoa to re-generate this file: https://github.com/lukeautry/tsoa
    "CreateAuditEventRequest": {
        "dataType": "refObject",
        "properties": {
            "userId": {"dataType":"string","required":true},
            "userName": {"dataType":"string"},
            "action": {"ref":"AuditAction","required":true},
            "resource": {"ref":"AuditResource","required":true},
            "resourceId": {"dataType":"string"},
            "resourceName": {"dataType":"string"},
            "service": {"dataType":"string","required":true},
            "details": {"ref":"Record_string.unknown_"},
        },
        "additionalProperties": false,
    },
    // WARNING: This file was auto-generated with tsoa. Please do not modify it. Re-run tsoa to re-generate this file: https://github.com/lukeautry/tsoa
    "AuditListResponse": {
        "dataType": "refObject",
        "properties": {
            "events": {"dataType":"array","array":{"dataType":"refAlias","ref":"AuditEventDTO"},"required":true},
            "total": {"dataType":"double","required":true},
            "page": {"dataType":"double","required":true},
            "pageSize": {"dataType":"double","required":true},
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


    
        const argsSettingsController_getSettings: Record<string, TsoaRoute.ParameterSchema> = {
        };
        app.get('/settings',
            ...(fetchMiddlewares<RequestHandler>(SettingsController)),
            ...(fetchMiddlewares<RequestHandler>(SettingsController.prototype.getSettings)),

            async function SettingsController_getSettings(request: ExRequest, response: ExResponse, next: any) {

            // WARNING: This file was auto-generated with tsoa. Please do not modify it. Re-run tsoa to re-generate this file: https://github.com/lukeautry/tsoa

            let validatedArgs: any[] = [];
            try {
                validatedArgs = templateService.getValidatedArgs({ args: argsSettingsController_getSettings, request, response });

                const controller = new SettingsController();

              await templateService.apiHandler({
                methodName: 'getSettings',
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
        const argsSettingsController_updateSettings: Record<string, TsoaRoute.ParameterSchema> = {
                body: {"in":"body","name":"body","required":true,"ref":"UpdateSettingsRequest"},
        };
        app.put('/settings',
            ...(fetchMiddlewares<RequestHandler>(SettingsController)),
            ...(fetchMiddlewares<RequestHandler>(SettingsController.prototype.updateSettings)),

            async function SettingsController_updateSettings(request: ExRequest, response: ExResponse, next: any) {

            // WARNING: This file was auto-generated with tsoa. Please do not modify it. Re-run tsoa to re-generate this file: https://github.com/lukeautry/tsoa

            let validatedArgs: any[] = [];
            try {
                validatedArgs = templateService.getValidatedArgs({ args: argsSettingsController_updateSettings, request, response });

                const controller = new SettingsController();

              await templateService.apiHandler({
                methodName: 'updateSettings',
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
        const argsHealthController_health: Record<string, TsoaRoute.ParameterSchema> = {
        };
        app.get('/health',
            ...(fetchMiddlewares<RequestHandler>(HealthController)),
            ...(fetchMiddlewares<RequestHandler>(HealthController.prototype.health)),

            async function HealthController_health(request: ExRequest, response: ExResponse, next: any) {

            // WARNING: This file was auto-generated with tsoa. Please do not modify it. Re-run tsoa to re-generate this file: https://github.com/lukeautry/tsoa

            let validatedArgs: any[] = [];
            try {
                validatedArgs = templateService.getValidatedArgs({ args: argsHealthController_health, request, response });

                const controller = new HealthController();

              await templateService.apiHandler({
                methodName: 'health',
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
        const argsAuditController_recordEvent: Record<string, TsoaRoute.ParameterSchema> = {
                body: {"in":"body","name":"body","required":true,"ref":"CreateAuditEventRequest"},
        };
        app.post('/audit',
            ...(fetchMiddlewares<RequestHandler>(AuditController)),
            ...(fetchMiddlewares<RequestHandler>(AuditController.prototype.recordEvent)),

            async function AuditController_recordEvent(request: ExRequest, response: ExResponse, next: any) {

            // WARNING: This file was auto-generated with tsoa. Please do not modify it. Re-run tsoa to re-generate this file: https://github.com/lukeautry/tsoa

            let validatedArgs: any[] = [];
            try {
                validatedArgs = templateService.getValidatedArgs({ args: argsAuditController_recordEvent, request, response });

                const controller = new AuditController();

              await templateService.apiHandler({
                methodName: 'recordEvent',
                controller,
                response,
                next,
                validatedArgs,
                successStatus: 201,
              });
            } catch (err) {
                return next(err);
            }
        });
        // WARNING: This file was auto-generated with tsoa. Please do not modify it. Re-run tsoa to re-generate this file: https://github.com/lukeautry/tsoa
        const argsAuditController_listEvents: Record<string, TsoaRoute.ParameterSchema> = {
                page: {"default":1,"in":"query","name":"page","dataType":"double"},
                pageSize: {"default":50,"in":"query","name":"pageSize","dataType":"double"},
                userId: {"in":"query","name":"userId","dataType":"string"},
                action: {"in":"query","name":"action","ref":"AuditAction"},
                resource: {"in":"query","name":"resource","ref":"AuditResource"},
                service: {"in":"query","name":"service","dataType":"string"},
                since: {"in":"query","name":"since","dataType":"string"},
                until: {"in":"query","name":"until","dataType":"string"},
        };
        app.get('/audit',
            ...(fetchMiddlewares<RequestHandler>(AuditController)),
            ...(fetchMiddlewares<RequestHandler>(AuditController.prototype.listEvents)),

            async function AuditController_listEvents(request: ExRequest, response: ExResponse, next: any) {

            // WARNING: This file was auto-generated with tsoa. Please do not modify it. Re-run tsoa to re-generate this file: https://github.com/lukeautry/tsoa

            let validatedArgs: any[] = [];
            try {
                validatedArgs = templateService.getValidatedArgs({ args: argsAuditController_listEvents, request, response });

                const controller = new AuditController();

              await templateService.apiHandler({
                methodName: 'listEvents',
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
