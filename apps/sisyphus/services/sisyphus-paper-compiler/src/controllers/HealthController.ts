import { Controller, Get, Route, Tags } from "tsoa";

interface HealthResponse {
  status: string;
}

/**
 * Health check endpoint for the paper compiler service.
 * Used by container orchestrators and load balancers to verify the service is running.
 */
@Route("health")
@Tags("Health")
export class HealthController extends Controller {
  /**
   * Check service health.
   *
   * @summary Health check
   */
  @Get()
  public async getHealth(): Promise<HealthResponse> {
    return { status: "ok" };
  }
}
