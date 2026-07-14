local devloop_base = require("devloop.base")
local source_refs = require("contract.source_ref")

local C = {}
function C.is_supported_pr_review_unresolved(M, payload)
  return type(payload) == "table"
    and payload.schema == "consensus.consensus_converge.v1"
    and devloop_base.is_safe_pr_review_result_ref(payload.proposal_id, payload.dedup_key)
    and payload.body == nil
    and payload.angle_results == nil
    and payload.decision == nil
    and source_refs.has_bounded_source_ref(payload.source_ref, M._max_key_len)
end

return C
