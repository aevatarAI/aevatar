import { Controller, Get, Route, Tags } from "tsoa";

/**
 * Response from the health check endpoint.
 */
interface HealthResponse {
  /** Service status — always `"ok"` when the service is running. */
  status: "ok";
}

/**
 * Health check endpoint for liveness and readiness probes.
 *
 * Returns a simple `{ status: "ok" }` response to indicate the service is running.
 * Used by Kubernetes liveness/readiness probes and load balancer health checks.
 */
@Route("health")
@Tags("Health")
export class HealthController extends Controller {
  /**
   * Check service health.
   *
   * Returns `{ status: "ok" }` if the service is running and ready to accept requests.
   *
   * @summary Health check
   */
  @Get()
  public async getHealth(): Promise<HealthResponse> {
    return { status: "ok" };
  }
}
