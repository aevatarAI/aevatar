local C = {}

local DEFAULT_PACKAGE_NAMESPACE = "github-devloop"

local function consumed_queue_set(consumes)
  local set = {}
  for _, queue in ipairs(consumes or {}) do
    if type(queue) == "string" and queue ~= "" then
      set[queue] = true
    end
  end
  return set
end

function C.queue_bare_name(queue, package_namespace)
  if type(queue) ~= "string" then
    return nil
  end
  package_namespace = package_namespace or DEFAULT_PACKAGE_NAMESPACE
  local prefix = package_namespace .. "."
  if queue:sub(1, #prefix) == prefix then
    return queue:sub(#prefix + 1)
  end
  return queue
end

function C.event_queue_matches(event, bare_queue, package_namespace)
  return C.queue_bare_name(type(event) == "table" and event.queue or nil, package_namespace) == bare_queue
end

function C.dispatch_consumed_queue(dept, spec, event, handlers, package_namespace)
  local queue = type(event) == "table" and event.queue or nil
  local bare_queue = C.queue_bare_name(queue, package_namespace)
  local consumed = consumed_queue_set((spec or {}).consumes)
  if bare_queue == nil or not consumed[bare_queue] then
    return false, "foreign"
  end
  local handler = type(handlers) == "table" and handlers[bare_queue] or nil
  if type(handler) ~= "function" then
    error("github-devloop: consumed-queue-unrouted: dept=" .. tostring(dept) .. " queue=" .. tostring(queue))
  end
  handler(event, bare_queue)
  return true, bare_queue
end

return C
