local strings = require("contract.strings")
local source_refs = require("contract.source_ref")

local payloads_predicates = require("devloop.payloads.predicates")
local entity_lib = require("devloop.entity")
local C = {}
function C.is_supported_reviewing(M, payload)
  return type(payload) == "table"
    and payload.schema == "github-devloop.reviewing.v1"
    and entity_lib.is_safe_entity_proposal_ref(payload.proposal_id, payload.dedup_key)
    and require("devloop.pr_safety").is_safe_pr_number(payload.pr_number)
    and strings.is_bounded_string(payload.version, M._max_dedup_len)
    and (payload.reviewing_hand_off == nil
      or payloads_predicates.is_own_state_marker_hand_off(M, payload.reviewing_hand_off, {
        proposal_id = payload.proposal_id,
        state = "reviewing",
        marker_version = payload.version,
        event_version = payload.version,
      }))
    and source_refs.has_bounded_source_ref(payload.source_ref, M._max_key_len)
end

return C
