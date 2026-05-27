This plugin contains runnable MCP server entrypoints for product and visual design integrations.

Before treating both integrations as fully connected:

- Put local secrets in `plugins/design-orchestrator/.env`.
- ChatGPT product reasoning defaults to `CHATGPT_CHANNEL=browser`.
- For browser mode, log in at `https://chatgpt.com/` in the Codex in-app browser.
- For API fallback, set `CHATGPT_CHANNEL=api`, `OPENAI_API_KEY`, and optionally `OPENAI_BASE_URL` / `OPENAI_MODEL`.
- Stitch uses Google Cloud OAuth by default; complete `gcloud auth login`.
- Set `GOOGLE_CLOUD_PROJECT` before using Stitch MCP calls.
- Optionally set `GOOGLE_CLOUD_ACCOUNT`, `GCLOUD_BIN`, `CLOUDSDK_PYTHON`, or `STITCH_ACCESS_TOKEN`.

Run `npm install` in this plugin directory before first use.
Run `npm run check` to verify ChatGPT env config and live Stitch MCP connectivity.
