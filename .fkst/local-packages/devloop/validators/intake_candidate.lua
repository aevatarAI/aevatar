local devloop_base = require("devloop.base")
local base_ids = require("devloop.base_ids")
local strings = require("contract.strings")
local source_refs = require("contract.source_ref")

local C = {}
function C.is_supported_intake_candidate(M, payload)
  if type(payload) ~= "table"
    or payload.schema ~= "github-devloop.intake-candidate.v1"
    or not devloop_base.is_safe_proposal_ref(payload.proposal_id, payload.dedup_key)
    or (payload.effect_id ~= nil and not strings.is_path_safe_key(payload.effect_id, M._max_dedup_len))
    or (payload.reintake_command_created_at ~= nil and not strings.is_bounded_string(payload.reintake_command_created_at, 128))
    or not source_refs.has_bounded_source_ref(payload.source_ref, M._max_key_len) then
    return false
  end
  local repo, issue_number = devloop_base.parse_issue_source_ref(payload.source_ref)
  return repo ~= nil
    and issue_number ~= nil
    and tostring(repo) == tostring(payload.repo)
    and tostring(issue_number) == tostring(payload.issue_number)
    and tostring(payload.proposal_id) == base_ids.proposal_id(repo, issue_number)
end

return C
