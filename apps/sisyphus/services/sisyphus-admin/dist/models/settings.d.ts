import { type Collection, type Db } from "mongodb";
/**
 * Singleton settings document. There is exactly one document in this collection
 * with id = "global". All Sisyphus services can read these values via the admin API.
 */
export interface Settings {
    id: "global";
    graphId: string;
    verifyCronIntervalHours: number;
    eventRetentionDays: number;
    defaultResearchMode: "graph_based" | "exploration";
    graphViewNodeLimit: number;
    updatedAt: string;
    updatedBy?: string;
}
export type SettingsDTO = Omit<Settings, "_id">;
export declare const DEFAULT_SETTINGS: Settings;
export declare function getSettingsCollection(db: Db): Collection<Settings>;
export declare function ensureSettingsIndexes(db: Db): Promise<void>;
export declare function toDTO(doc: Settings & {
    _id?: unknown;
}): SettingsDTO;
//# sourceMappingURL=settings.d.ts.map