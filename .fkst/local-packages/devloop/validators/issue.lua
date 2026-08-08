local base_ids = require("devloop.base_ids")
local devloop_base = require("devloop.base")
local source_refs = require("contract.source_ref")

local C = {}
function C.is_supported_issue(payload)
  return type(payload) == "table"
    and payload.schema == "github-proxy.v1"
    and payload.type == "issue"
    and payload.repo ~= nil
    and payload.number ~= nil
    and payload.title ~= nil
    and payload.updated_at ~= nil
    and base_ids.issue_ref_round_trips(payload.repo, payload.number)
    and source_refs.has_bounded_source_ref(payload.source_ref, devloop_base._max_key_len)
end

return C
