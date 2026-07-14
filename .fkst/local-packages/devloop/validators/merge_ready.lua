local devloop_base = require("devloop.base")
local strings = require("contract.strings")
local source_refs = require("contract.source_ref")
local forge_validators = require("devloop.forge_validators")
local entity_lib = require("devloop.entity")

local C = {}
function C.is_supported_merge_ready(M, payload)
  return type(payload) == "table"
    and payload.schema == "github-devloop.merge-ready.v1"
    and entity_lib.is_safe_entity_proposal_ref(payload.proposal_id, payload.dedup_key)
    and require("devloop.pr_safety").is_safe_pr_number(payload.pr_number)
    and strings.is_bounded_string(payload.version, M._max_dedup_len)
    and devloop_base.is_safe_pr_review_result_ref(payload.review_proposal_id, payload.review_dedup_key)
    and forge_validators.is_git_sha(payload.reviewed_head_sha)
    and source_refs.has_bounded_source_ref(payload.source_ref, M._max_key_len)
end

return C
