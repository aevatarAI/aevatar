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
import { Body, Controller, Get, Path, Post, Route, Response, Tags, } from "tsoa";
import pino from "pino";
import { startSession, stopSession, getSessionStatus, getAllSessionStatus, } from "../session-manager.js";
const logger = pino({ name: "runner:workflow-run-controller" });
let WorkflowRunController = class WorkflowRunController extends Controller {
    /**
     * Start a workflow session (research, translate, purify, verify).
     * For research type, accepts optional mode (graph_based/exploration) and direction.
     * @summary Start workflow
     */
    async start(workflowType, body) {
        try {
            const session = await startSession(workflowType, "user", {
                mode: body.mode,
                direction: body.direction,
                runMode: body.runMode,
                authorization: body.userToken ? `Bearer ${body.userToken}` : undefined,
            });
            logger.info({ sessionId: session.id, workflowType }, "Workflow started via API");
            return {
                sessionId: session.id,
                runId: session.runId,
                workflowType: session.workflowType,
                status: session.status,
            };
        }
        catch (err) {
            const message = err.message;
            if (message.includes("already running")) {
                this.setStatus(409);
            }
            else {
                this.setStatus(500);
            }
            return { error: message };
        }
    }
    /**
     * Stop a running workflow session.
     * @summary Stop workflow
     */
    async stop(workflowType) {
        const session = await stopSession(workflowType);
        if (!session) {
            this.setStatus(404);
            return { error: `No running '${workflowType}' session` };
        }
        logger.info({ sessionId: session.id, workflowType }, "Workflow stopped via API");
        return {
            sessionId: session.id,
            workflowType: session.workflowType,
            status: session.status,
        };
    }
    /**
     * Get current status of a specific workflow type.
     * @summary Get workflow status
     */
    async status(workflowType) {
        return getSessionStatus(workflowType);
    }
    /**
     * Get status of all workflow types.
     * @summary Get all workflow statuses
     */
    async allStatus() {
        return getAllSessionStatus();
    }
};
__decorate([
    Post("{workflowType}/start"),
    Response(409, "Workflow already running"),
    Response(500, "Failed to start"),
    __param(0, Path()),
    __param(1, Body()),
    __metadata("design:type", Function),
    __metadata("design:paramtypes", [String, Object]),
    __metadata("design:returntype", Promise)
], WorkflowRunController.prototype, "start", null);
__decorate([
    Post("{workflowType}/stop"),
    Response(404, "No running session"),
    __param(0, Path()),
    __metadata("design:type", Function),
    __metadata("design:paramtypes", [String]),
    __metadata("design:returntype", Promise)
], WorkflowRunController.prototype, "stop", null);
__decorate([
    Get("{workflowType}/status"),
    __param(0, Path()),
    __metadata("design:type", Function),
    __metadata("design:paramtypes", [String]),
    __metadata("design:returntype", Promise)
], WorkflowRunController.prototype, "status", null);
__decorate([
    Get("status"),
    __metadata("design:type", Function),
    __metadata("design:paramtypes", []),
    __metadata("design:returntype", Promise)
], WorkflowRunController.prototype, "allStatus", null);
WorkflowRunController = __decorate([
    Route("workflows"),
    Tags("Workflow Runs")
], WorkflowRunController);
export { WorkflowRunController };
//# sourceMappingURL=WorkflowRunController.js.map