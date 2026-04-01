import {
  Body,
  Controller,
  Get,
  Put,
  Route,
  Response,
  Tags,
} from "tsoa";
import pino from "pino";
import { getDb } from "../db.js";
import {
  getSettingsCollection,
  toDTO,
  DEFAULT_SETTINGS,
  type Settings,
  type SettingsDTO,
} from "../models/settings.js";
import type { ErrorResponse } from "../types.js";

const logger = pino({ name: "admin:settings-controller" });

interface UpdateSettingsRequest {
  graphId?: string;
  verifyCronIntervalHours?: number;
  eventRetentionDays?: number;
  defaultResearchMode?: "graph_based" | "exploration";
  graphViewNodeLimit?: number;
}

@Route("settings")
@Tags("Settings")
export class SettingsController extends Controller {
  /**
   * Get global settings. Returns defaults if never configured.
   * Other Sisyphus services call this to retrieve graphId, cron intervals, etc.
   * @summary Get settings
   */
  @Get()
  public async getSettings(): Promise<SettingsDTO> {
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
  @Put()
  @Response<ErrorResponse>(400, "Bad request")
  public async updateSettings(
    @Body() body: UpdateSettingsRequest,
  ): Promise<SettingsDTO> {
    const db = getDb();
    const col = getSettingsCollection(db);

    const updateFields: Record<string, unknown> = {
      updatedAt: new Date().toISOString(),
    };

    if (body.graphId !== undefined) updateFields.graphId = body.graphId;
    if (body.verifyCronIntervalHours !== undefined) updateFields.verifyCronIntervalHours = body.verifyCronIntervalHours;
    if (body.eventRetentionDays !== undefined) updateFields.eventRetentionDays = body.eventRetentionDays;
    if (body.defaultResearchMode !== undefined) updateFields.defaultResearchMode = body.defaultResearchMode;
    if (body.graphViewNodeLimit !== undefined) updateFields.graphViewNodeLimit = body.graphViewNodeLimit;

    await col.updateOne(
      { id: "global" },
      {
        $set: updateFields,
        $setOnInsert: {
          id: "global" as const,
          graphId: DEFAULT_SETTINGS.graphId,
          verifyCronIntervalHours: DEFAULT_SETTINGS.verifyCronIntervalHours,
          eventRetentionDays: DEFAULT_SETTINGS.eventRetentionDays,
          defaultResearchMode: DEFAULT_SETTINGS.defaultResearchMode,
          graphViewNodeLimit: DEFAULT_SETTINGS.graphViewNodeLimit,
        },
      },
      { upsert: true },
    );

    const updated = await col.findOne({ id: "global" });
    logger.info({ fields: Object.keys(body) }, "Settings updated");

    return toDTO(updated!);
  }
}
