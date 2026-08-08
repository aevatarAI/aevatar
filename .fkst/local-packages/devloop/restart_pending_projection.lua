local M = {}

local function is_nonempty_string(value)
  return type(value) == "string" and value ~= ""
end

function M.derive_pending_projection(edges)
  if type(edges) ~= "table" then
    error("devloop.restart_pending_projection: edges must be a table")
  end

  local projection = {}
  for _, edge in ipairs(edges) do
    if type(edge) ~= "table" then
      error("devloop.restart_pending_projection: edge must be a table")
    end
    local pending_order = edge.pending_order
    if type(pending_order) ~= "table" then
      error("devloop.restart_pending_projection: edge.pending_order must be a table")
    end
    if type(pending_order.participates) ~= "boolean" then
      error("devloop.restart_pending_projection: edge.pending_order.participates must be a boolean")
    end
    if pending_order.participates then
      local predecessor = pending_order.predecessor_state
      if not is_nonempty_string(predecessor) then
        error("devloop.restart_pending_projection: participating edge predecessor_state must be a non-empty string")
      end
      if not is_nonempty_string(edge.target) then
        error("devloop.restart_pending_projection: participating edge target must be a lifecycle state")
      end
      projection[predecessor] = projection[predecessor] or {}
      projection[predecessor][edge.target] = true
    end
  end
  return projection
end

function M.can_reach(projection, from_state, to_state)
  if type(projection) ~= "table" then
    error("devloop.restart_pending_projection: projection must be a table")
  end

  local function visit(current, seen)
    if current == to_state then
      return true
    end
    if seen[current] then
      return false
    end
    seen[current] = true
    for target in pairs(projection[current] or {}) do
      if visit(target, seen) then
        return true
      end
    end
    return false
  end

  return visit(from_state == nil and "unmanaged" or from_state, {})
end

return M
