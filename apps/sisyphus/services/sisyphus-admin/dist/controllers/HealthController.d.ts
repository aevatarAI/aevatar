import { Controller } from "tsoa";
export declare class HealthController extends Controller {
    /** @summary Health check */
    health(): Promise<{
        service: string;
        status: string;
    }>;
}
//# sourceMappingURL=HealthController.d.ts.map