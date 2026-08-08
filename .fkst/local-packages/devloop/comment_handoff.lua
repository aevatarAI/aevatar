local devloop_logging = require("devloop.logging")
local S = {}

function S.acceptor(supported_handoff)
  return function(event)
    return supported_handoff((event and event.payload) or {}) ~= nil
  end
end

function S.log_unsupported(supported_handoff, event)
  local payload = event.payload or {}
  local handoff = supported_handoff(payload)
  if handoff == nil then
    local proposal_id = type(payload.handoff) == "table" and tostring(payload.handoff.proposal_id or "unknown") or "unknown"
    devloop_logging.log_entry("comment_handoff", event, proposal_id, devloop_logging.payload_field(payload, "dedup_key"))
    devloop_logging.log_cas_decision("comment_handoff", proposal_id, { state = nil, version = nil }, "comment-written", "handoff", "skip-foreign(payload)", "unsupported comment-written handoff")
    return
  end
end

return S
