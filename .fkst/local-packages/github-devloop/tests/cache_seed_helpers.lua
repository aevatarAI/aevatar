local M = {}

M.spec = {
  consumes = { "cache_seed" },
  produces = { "cache_seeded" },
}

function pipeline(event)
  local payload = event.payload or {}
  cache_set(payload.key, payload.value)
  raise("cache_seeded", { key = payload.key })
end

M.pipeline = pipeline

return M
