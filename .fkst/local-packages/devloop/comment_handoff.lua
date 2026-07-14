local S = {}

function S.acceptor(supported_handoff)
  return function(event)
    return supported_handoff((event and event.payload) or {}) ~= nil
  end
end

function S.log_unsupported(M, supported_handoff, event)
  local payload = event.payload or {}
  local handoff = supported_handoff(payload)
  if handoff == nil then
    local proposal_id = type(payload.handoff) == "table" and tostring(payload.handoff.proposal_id or "unknown") or "unknown"
    M.log_entry("comment_handoff", event, proposal_id, M.payload_field(payload, "dedup_key"))
    M.log_cas_decision("comment_handoff", proposal_id, { state = nil, version = nil }, "comment-written", "handoff", "skip-foreign(payload)", "unsupported comment-written handoff")
    return
  end
end

return S
