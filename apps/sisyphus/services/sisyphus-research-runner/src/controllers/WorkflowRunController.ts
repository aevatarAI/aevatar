import {
  Body,
  Controller,
  Get,
  Path,
  Post,
  Route,
  Response,
  Tags,
} from "tsoa";
import pino from "pino";
import {
  startSession,
  stopSession,
  getSessionStatus,
  getAllSessionStatus,
} from "../session-manager.js";
import type { WorkflowType, ErrorResponse } from "../types.js";
import type { SessionDTO } from "../models/session.js";

const logger = pino({ name: "runner:workflow-run-controller" });

interface StartRequest {
  mode?: string;
  direction?: string;
  /** "deployed" (default) requires workflow to be deployed; "draft" compiles on-the-fly */
  runMode?: "draft" | "deployed";
  /** User's NyxID access token for aevatar mainnet auth */
  userToken?: string;
}

interface StartResponse {
  sessionId: string;
  runId: string;
  workflowType: string;
  status: string;
}

interface StopResponse {
  sessionId: string;
  workflowType: string;
  status: string;
}

interface StatusResponse {
  running: boolean;
  session: SessionDTO | null;
}

@Route("workflows")
@Tags("Workflow Runs")
export class WorkflowRunController extends Controller {
  /**
   * Start a workflow session (research, translate, purify, verify).
   * For research type, accepts optional mode (graph_based/exploration) and direction.
   * @summary Start workflow
   */
  @Post("{workflowType}/start")
  @Response<ErrorResponse>(409, "Workflow already running")
  @Response<ErrorResponse>(500, "Failed to start")
  public async start(
    @Path() workflowType: WorkflowType,
    @Body() body: StartRequest
  ): Promise<StartResponse> {
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
    } catch (err) {
      const message = (err as Error).message;
      if (message.includes("already running")) {
        this.setStatus(409);
      } else {
        this.setStatus(500);
      }
      return { error: message } as unknown as StartResponse;
    }
  }

  /**
   * Stop a running workflow session.
   * @summary Stop workflow
   */
  @Post("{workflowType}/stop")
  @Response<ErrorResponse>(404, "No running session")
  public async stop(
    @Path() workflowType: WorkflowType
  ): Promise<StopResponse> {
    const session = await stopSession(workflowType);
    if (!session) {
      this.setStatus(404);
      return { error: `No running '${workflowType}' session` } as unknown as StopResponse;
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
  @Get("{workflowType}/status")
  public async status(
    @Path() workflowType: WorkflowType
  ): Promise<StatusResponse> {
    return getSessionStatus(workflowType);
  }

  /**
   * Get status of all workflow types.
   * @summary Get all workflow statuses
   */
  @Get("status")
  public async allStatus(): Promise<Record<string, StatusResponse>> {
    return getAllSessionStatus();
  }
}
