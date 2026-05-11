# workflow-yaml
- Omit provider/model from workflow YAML demos; use defaults and do not hardcode local-only configurations. Confidence: 0.85
- Implement automatic YAML correction loop (LLM-driven repair on parser errors) before surfacing validation failures to users. Confidence: 0.85
- All workflow YAML must pass schema validation before being returned to the user or executed; no optional validation mode. Confidence: 0.85

# workflow-demo-web
- Support light/dark theme toggle in workflow demo playground UI. Confidence: 0.75
- Workflow demo should be a thin presentation layer consuming framework APIs (/api/chat) without containing core orchestration logic. Confidence: 0.85

# cli
- `aevatar chat` command should auto-start the app if not running, then push chat into the UI; add persistent remote URL config with `chat config set-url/get-url/clear-url` subcommands. Confidence: 0.80

# sdk
- Use Aevatar.Workflow.Sdk (SSE-first) for App integration with `/api/chat` and workflow capabilities. Confidence: 0.80

# project-org
- Week plans go in `.plan/` at repo root and should be gitignored. Confidence: 0.70
- No legacy interfaces or backward compatibility shims; always clean-break forward. Confidence: 0.85
- PR titles and descriptions go into markdown files before creating the PR. Confidence: 0.70

# config
- Runtime configuration must be config-driven, not hardcoded; use AEVATAR_ prefix for environment variables. Do not silently fall back to InMemory when production config is missing. Confidence: 0.75
- Desktop app reads backend URL from config.json (like web frontend), never hardcodes remote endpoints. Confidence: 0.70

# deployment
- Prefer shell scripts for one-click local deployment and auto-update mechanisms. Confidence: 0.70
- Push to remote triggers automatic deployment; no manual deploy step needed after code is pushed. Confidence: 0.80

# code-review
- Perform strict, picky clean-code audits of all uncommitted code changes; score ruthlessly and write results to docs/audit-scorecard/. Confidence: 0.85
- After fixes are applied, re-audit and update the scorecard document; iterate toward score of 100 with explicit "往100分逼近" framing. Confidence: 0.85
- Audit scorecard documents must be self-contained final reports; do not reference predecessor drafts, "original findings", or "original reports"; write as if it is the only version. Confidence: 0.70

# planning
- Before implementing fixes, create detailed plans with specific file paths and verification steps; implement plan todos sequentially without skipping. Confidence: 0.80

# ui
- Execution logs must show full response text without truncation; use collapsible sections for long content. Confidence: 0.85
- LLM thinking/reasoning process should be displayed and collapsible after completion. Confidence: 0.75
