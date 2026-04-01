import { Controller, Get, Route, Tags } from "tsoa";

interface HealthResponse {
  service: string;
  status: "ok";
}

@Route("health")
@Tags("Health")
export class HealthController extends Controller {
  /**
   * Check service health.
   * @summary Health check
   */
  @Get()
  public async getHealth(): Promise<HealthResponse> {
    return { service: "sisyphus-runner", status: "ok" };
  }
}
