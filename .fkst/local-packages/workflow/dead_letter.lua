-- contract.dead_letter: dependency-free extractors for dead-letter delivery payloads.
local error_facts = require("contract.error_facts")

local D = {}

function D.extract_source_ref(payload)
  local source_ref = payload.source_ref
  if source_ref == nil and type(payload.payload) == "table" then
    source_ref = payload.payload.source_ref
  end
  if type(source_ref) == "table" then
    return error_facts.source_ref_field(source_ref)
  end
  return error_facts.one_line(source_ref)
end

function D.extract_dedup_key(payload)
  if payload.dedup_key ~= nil then
    return payload.dedup_key
  end
  if type(payload.payload) == "table" then
    return payload.payload.dedup_key
  end
  return nil
end

return D
