local devloop_base = require("devloop.base")
local source_refs = require("contract.source_ref")
local convergence_shared = require("devloop.convergence.shared")
local strings = require("contract.strings")

local C = {}
function C.is_supported_pr_review_unresolved(payload)
  return type(payload) == "table"
    and payload.schema == "consensus.consensus_converge.v1"
    and devloop_base.is_safe_pr_review_result_ref(payload.proposal_id, payload.dedup_key)
    and payload.body == nil
    and payload.angle_results == nil
    and payload.decision == nil
    and (payload.findings_record == nil
      or strings.is_bounded_string(payload.findings_record, convergence_shared.findings_record_len))
    and (payload.essence_stall == nil or payload.essence_stall == true or payload.essence_stall == false)
    and source_refs.has_bounded_source_ref(payload.source_ref, devloop_base._max_key_len)
end

return C
