-- contract.error_facts: dependency-free primitives for stable failure fingerprints.
local F = {}

function F.one_line(value)
  return tostring(value or ""):gsub("%s+", " ")
end

function F.normalized_message(value)
  local text = F.one_line(value):lower()
  text = text:gsub("%d%d%d%d%-%d%d%-%d%d[tT ]%d%d:%d%d:%d%d%.?%d*Z?", "<time>")
  text = text:gsub("%f[%x]%x%x%x%x%x%x[%x]+%f[^%x]", "<sha>")
  text = text:gsub("/tmp/[^%s]+", "<path>")
  text = text:gsub("/var/folders/[^%s]+", "<path>")
  text = text:gsub("%s+", " ")
  return text
end

F.normalized_error_message = F.normalized_message

function F.stable_hash(value)
  local hash = 5381
  for index = 1, #value do
    hash = (hash * 33 + value:byte(index)) % 2147483647
  end
  return "fp-" .. tostring(hash)
end

function F.source_ref_field(source_ref)
  if type(source_ref) == "table" then
    return F.one_line(source_ref.kind) .. ":" .. F.one_line(source_ref.ref)
  end
  if source_ref ~= nil then
    return F.one_line(source_ref)
  end
  return nil
end

function F.error_fingerprint(error_class, queue, dept, message)
  return F.stable_hash(table.concat({
    tostring(error_class or "unknown-error"),
    tostring(queue or ""),
    tostring(dept or ""),
    F.normalized_message(message),
  }, "|"))
end

function F.error_fact_fields(error_class, queue, dept, message, context)
  local fields = {
    "error_class=" .. F.one_line(error_class or "unknown-error"),
    "fingerprint=" .. F.error_fingerprint(error_class, queue, dept, message),
  }
  local source_ref = F.source_ref_field(context and context.source_ref)
  if source_ref ~= nil and source_ref ~= "" then
    table.insert(fields, "source_ref=" .. source_ref)
  end
  if context and context.attempt ~= nil then
    table.insert(fields, "attempt=" .. F.one_line(context.attempt))
  end
  if context and context.terminal ~= nil then
    table.insert(fields, "terminal=" .. tostring(context.terminal == true))
  end
  return fields
end

function F.event_source_ref(event)
  if type(event) == "table" and event.source_ref ~= nil then
    return event.source_ref
  end
  local payload = type(event) == "table" and event.payload or nil
  if type(payload) == "table" then
    return payload.source_ref
  end
  return nil
end

return F
