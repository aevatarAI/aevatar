import { Controller, Get, Route, Tags } from "tsoa";

@Route("health")
@Tags("Health")
export class HealthController extends Controller {
  /** @summary Health check */
  @Get()
  public async health(): Promise<{ service: string; status: string }> {
    return { service: "sisyphus-admin", status: "ok" };
  }
}
