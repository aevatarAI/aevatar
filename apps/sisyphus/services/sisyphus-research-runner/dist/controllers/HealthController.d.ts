import { Controller } from "tsoa";
interface HealthResponse {
    service: string;
    status: "ok";
}
export declare class HealthController extends Controller {
    /**
     * Check service health.
     * @summary Health check
     */
    getHealth(): Promise<HealthResponse>;
}
export {};
//# sourceMappingURL=HealthController.d.ts.map