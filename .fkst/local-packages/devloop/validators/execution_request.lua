local devloop_base = require("devloop.base")
local base_ids = require("devloop.base_ids")
local strings = require("contract.strings")
local execution_start = require("devloop.execution_start")
local source_refs = require("contract.source_ref")

local C = {}
function C.is_supported_execution_request(M, payload)
  if type(payload) ~= "table"
    or payload.schema ~= "github-devloop.execution-request.v1"
    or not devloop_base.is_safe_proposal_ref(payload.proposal_id, payload.dedup_key)
    or not source_refs.has_bounded_source_ref(payload.source_ref, M._max_key_len)
    or (payload.service_class ~= nil and not execution_start.is_execution_service_class(payload.service_class))
    or (payload.framing ~= nil and not strings.is_bounded_string(payload.framing, M._max_framing_len))
    or (payload.origin ~= nil and type(payload.origin) ~= "table")
    or (payload.context ~= nil and type(payload.context) ~= "table") then
    return false
  end
  local repo, issue_number = devloop_base.parse_issue_source_ref(payload.source_ref)
  return repo ~= nil
    and issue_number ~= nil
    and tostring(payload.proposal_id) == base_ids.proposal_id(repo, issue_number)
end

return C
