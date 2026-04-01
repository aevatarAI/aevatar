import { MongoClient } from "mongodb";
import pino from "pino";
const logger = pino({ name: "admin:db" });
const MONGO_URI = process.env["MONGO_URI"] ?? "mongodb://localhost:27017";
const DB_NAME = process.env["DB_NAME"] ?? "sisyphus";
function safeHost(uri) {
    try {
        return new URL(uri).host;
    }
    catch {
        return "(invalid-uri)";
    }
}
let client = null;
let db = null;
export async function connectDb() {
    if (db)
        return db;
    client = new MongoClient(MONGO_URI);
    await client.connect();
    db = client.db(DB_NAME);
    logger.info({ host: safeHost(MONGO_URI), db: DB_NAME }, "Connected to MongoDB");
    return db;
}
export function getDb() {
    if (!db)
        throw new Error("Database not connected. Call connectDb() first.");
    return db;
}
export async function closeDb() {
    if (client) {
        await client.close();
        client = null;
        db = null;
        logger.info("MongoDB connection closed");
    }
}
//# sourceMappingURL=db.js.map