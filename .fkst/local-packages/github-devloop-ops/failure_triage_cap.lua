-- Standalone failure-triage capability for departments that consume it without the
-- ambient composed core. The failure-triage decision is install-based (it closes over
-- base-constant bounds and the error-fact classifier), so this module builds that
-- coupled chain once against an isolated table and exposes just the decision entry
-- point. A department receives `decide` by requiring this module directly instead of
-- reading the triage decision off the ambient composed core.
local base = require("devloop.base")

local cap = {
  _max_key_len = base._max_key_len,
  _max_title_len = base._max_title_len,
  _max_body_len = base._max_body_len,
}
require("core.error_facts").install(cap)
require("core.failure_triage").install(cap)

return {
  blocked_obligation_patrol_once = cap.blocked_obligation_patrol_once,
  decide = cap.failure_triage_decision,
}
