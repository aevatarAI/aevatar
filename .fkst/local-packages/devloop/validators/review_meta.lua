local devloop_base = require("devloop.base")
local entity_lib = require("devloop.entity")
local strings = require("contract.strings")
local source_refs = require("contract.source_ref")

local C = {}
function C.is_supported_review_meta(M, payload)
  if type(payload) ~= "table"
    or payload.schema ~= "github-devloop.review-meta.v1"
    or not devloop_base.is_safe_pr_review_result_ref(payload.review_proposal_id, payload.review_dedup_key) then
    return false
  end
  local has_valid_identity = payload.mode == "fix-reflection"
    and entity_lib.parse_entity_proposal_id(payload.proposal_id) ~= nil
    and strings.is_path_safe_key(payload.dedup_key, M._max_dedup_len)
  if payload.mode ~= "fix-reflection" then
    has_valid_identity = entity_lib.is_safe_entity_proposal_ref(payload.proposal_id, payload.dedup_key)
  end
  return has_valid_identity
    and strings.is_bounded_string(payload.version, M._max_dedup_len)
    and require("devloop.pr_safety").is_safe_pr_number(payload.pr_number)
    and tonumber(payload.n) ~= nil
    and (payload.mode == nil or payload.mode == "fix-reflection")
    and (payload.fix_round == nil or tonumber(payload.fix_round) ~= nil)
    and (payload.blocking_gap == nil or strings.is_bounded_string(payload.blocking_gap, M._max_blocking_gap_len))
    and source_refs.has_bounded_source_ref(payload.source_ref, M._max_key_len)
end

return C
