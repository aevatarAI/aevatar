local source_refs = require("contract.source_ref")

local C = {}
function C.is_supported_pr(M, payload)
  return type(payload) == "table"
    and payload.schema == "github-proxy.v1"
    and payload.type == "pr"
    and payload.repo ~= nil
    and require("devloop.pr_safety").is_safe_pr_number(payload.number)
    and source_refs.has_bounded_source_ref(payload.source_ref, M._max_key_len)
end

return C
