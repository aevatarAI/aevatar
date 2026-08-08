local L = {}

function L.log_line(prefix, level, dept, proposal_id, tag, fields)
  local parts = {
    tostring(prefix),
    "dept=" .. tostring(dept or "unknown"),
    "proposal_id=" .. tostring(proposal_id or "unknown"),
    "tag=" .. tostring(tag or "event"),
  }
  for _, field in ipairs(fields or {}) do
    table.insert(parts, tostring(field))
  end
  log[level or "info"](table.concat(parts, " "))
end

function L.log_entry(prefix, dept, event, proposal_id, dedup_key)
  L.log_line(prefix, "info", dept, proposal_id, "ENTRY", {
    "queue=" .. tostring(event and event.queue or "unknown"),
    "payload_type=" .. type(event and event.payload),
    "version=" .. tostring(dedup_key or ""),
    "dedup_key=" .. tostring(dedup_key or ""),
  })
end

function L.payload_field(payload, key)
  if type(payload) ~= "table" then
    return nil
  end
  return payload[key]
end

return L
