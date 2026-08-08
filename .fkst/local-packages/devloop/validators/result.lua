local devloop_base = require("devloop.base")
local strings = require("contract.strings")
local source_refs = require("contract.source_ref")

local C = {}
function C.is_supported_result(payload)
  return type(payload) == "table"
    and payload.schema == "consensus.consensus_reached.v1"
    and (payload.decision == "approve"
      or payload.decision == "reject" and payload.decision_reason == "premise-refuted")
    and (payload.decision ~= "approve" or payload.decision_reason == nil)
    and devloop_base.is_safe_consensus_result_ref(payload.proposal_id, payload.dedup_key)
    and (payload.effect_version == nil or devloop_base.is_safe_consensus_result_ref(payload.proposal_id, payload.effect_version))
    and strings.is_bounded_string(payload.body, devloop_base._max_body_len)
    and (payload.framing == nil or strings.is_bounded_string(payload.framing, devloop_base._max_framing_len))
    and source_refs.has_bounded_source_ref(payload.source_ref, devloop_base._max_key_len)
end

return C
