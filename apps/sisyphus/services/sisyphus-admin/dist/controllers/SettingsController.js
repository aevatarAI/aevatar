var __decorate = (this && this.__decorate) || function (decorators, target, key, desc) {
    var c = arguments.length, r = c < 3 ? target : desc === null ? desc = Object.getOwnPropertyDescriptor(target, key) : desc, d;
    if (typeof Reflect === "object" && typeof Reflect.decorate === "function") r = Reflect.decorate(decorators, target, key, desc);
    else for (var i = decorators.length - 1; i >= 0; i--) if (d = decorators[i]) r = (c < 3 ? d(r) : c > 3 ? d(target, key, r) : d(target, key)) || r;
    return c > 3 && r && Object.defineProperty(target, key, r), r;
};
var __metadata = (this && this.__metadata) || function (k, v) {
    if (typeof Reflect === "object" && typeof Reflect.metadata === "function") return Reflect.metadata(k, v);
};
var __param = (this && this.__param) || function (paramIndex, decorator) {
    return function (target, key) { decorator(target, key, paramIndex); }
};
import { Body, Controller, Get, Put, Route, Response, Tags, } from "tsoa";
import pino from "pino";
import { getDb } from "../db.js";
import { getSettingsCollection, toDTO, DEFAULT_SETTINGS, } from "../models/settings.js";
const logger = pino({ name: "admin:settings-controller" });
let SettingsController = class SettingsController extends Controller {
    /**
     * Get global settings. Returns defaults if never configured.
     * Other Sisyphus services call this to retrieve graphId, cron intervals, etc.
     * @summary Get settings
     */
    async getSettings() {
        const db = getDb();
        const col = getSettingsCollection(db);
        const doc = await col.findOne({ id: "global" });
        if (!doc) {
            logger.info("No settings found, returning defaults");
            return { ...DEFAULT_SETTINGS };
        }
        return toDTO(doc);
    }
    /**
     * Update global settings. Merges provided fields with existing values.
     * @summary Update settings
     */
    async updateSettings(body) {
        const db = getDb();
        const col = getSettingsCollection(db);
        const updateFields = {
            updatedAt: new Date().toISOString(),
        };
        if (body.graphId !== undefined)
            updateFields.graphId = body.graphId;
        if (body.verifyCronIntervalHours !== undefined)
            updateFields.verifyCronIntervalHours = body.verifyCronIntervalHours;
        if (body.eventRetentionDays !== undefined)
            updateFields.eventRetentionDays = body.eventRetentionDays;
        if (body.defaultResearchMode !== undefined)
            updateFields.defaultResearchMode = body.defaultResearchMode;
        if (body.graphViewNodeLimit !== undefined)
            updateFields.graphViewNodeLimit = body.graphViewNodeLimit;
        await col.updateOne({ id: "global" }, {
            $set: updateFields,
            $setOnInsert: {
                id: "global",
                graphId: DEFAULT_SETTINGS.graphId,
                verifyCronIntervalHours: DEFAULT_SETTINGS.verifyCronIntervalHours,
                eventRetentionDays: DEFAULT_SETTINGS.eventRetentionDays,
                defaultResearchMode: DEFAULT_SETTINGS.defaultResearchMode,
                graphViewNodeLimit: DEFAULT_SETTINGS.graphViewNodeLimit,
            },
        }, { upsert: true });
        const updated = await col.findOne({ id: "global" });
        logger.info({ fields: Object.keys(body) }, "Settings updated");
        return toDTO(updated);
    }
};
__decorate([
    Get(),
    __metadata("design:type", Function),
    __metadata("design:paramtypes", []),
    __metadata("design:returntype", Promise)
], SettingsController.prototype, "getSettings", null);
__decorate([
    Put(),
    Response(400, "Bad request"),
    __param(0, Body()),
    __metadata("design:type", Function),
    __metadata("design:paramtypes", [Object]),
    __metadata("design:returntype", Promise)
], SettingsController.prototype, "updateSettings", null);
SettingsController = __decorate([
    Route("settings"),
    Tags("Settings")
], SettingsController);
export { SettingsController };
//# sourceMappingURL=SettingsController.js.map