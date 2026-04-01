import { Controller } from "tsoa";
import { type SettingsDTO } from "../models/settings.js";
interface UpdateSettingsRequest {
    graphId?: string;
    verifyCronIntervalHours?: number;
    eventRetentionDays?: number;
    defaultResearchMode?: "graph_based" | "exploration";
    graphViewNodeLimit?: number;
}
export declare class SettingsController extends Controller {
    /**
     * Get global settings. Returns defaults if never configured.
     * Other Sisyphus services call this to retrieve graphId, cron intervals, etc.
     * @summary Get settings
     */
    getSettings(): Promise<SettingsDTO>;
    /**
     * Update global settings. Merges provided fields with existing values.
     * @summary Update settings
     */
    updateSettings(body: UpdateSettingsRequest): Promise<SettingsDTO>;
}
export {};
//# sourceMappingURL=SettingsController.d.ts.map