import { type Collection, type Db } from "mongodb";
export interface AgUiEvent {
    sessionId: string;
    timestamp: string;
    eventType: string;
    payload: Record<string, unknown>;
    createdAt: Date;
}
export declare function getEventLogCollection(db: Db): Collection<AgUiEvent>;
export declare function ensureEventLogIndexes(db: Db): Promise<void>;
//# sourceMappingURL=event-log.d.ts.map