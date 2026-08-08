local pending_projection = require("devloop.restart_pending_projection")
local restart_edges = require("devloop.restart_edges")

local M = {}

local frozen_projection = {
  unmanaged = { thinking = true },
  thinking = { dependency_wait = true, ready = true, blocked = true },
  dependency_wait = { dependency_wait = true, ready = true, blocked = true },
  ready = { dependency_wait = true, implementing = true, blocked = true },
  implementing = { ["awaiting-pr"] = true, ["impl-failed"] = true },
  ["awaiting-pr"] = { merged = true, ready = true, blocked = true },
  ["pr-open"] = { reviewing = true, blocked = true },
  reviewing = { ["merge-ready"] = true, fixing = true, ["review-meta"] = true },
  ["merge-ready"] = { merging = true, blocked = true },
  merging = { merged = true, reviewing = true, fixing = true, blocked = true },
  fixing = { reviewing = true, ["review-meta"] = true },
  ["review-meta"] = { fixing = true, blocked = true },
  ["impl-failed"] = { implementing = true },
  merged = {},
  ["closed-unmerged"] = {},
  blocked = {},
}

local function deep_copy(value)
  if type(value) ~= "table" then
    return value
  end
  local copy = {}
  for key, nested in pairs(value) do
    copy[key] = deep_copy(nested)
  end
  return copy
end

local owner_sources = {
  ["github-devloop"] = {
    unmanaged = true,
    thinking = true,
    dependency_wait = true,
    ready = true,
    implementing = true,
    ["awaiting-pr"] = true,
    ["impl-failed"] = true,
  },
  ["github-devloop-pr"] = {
    ["pr-open"] = true,
    reviewing = true,
    ["merge-ready"] = true,
    merging = true,
    fixing = true,
    ["review-meta"] = true,
  },
}

local function append_all(target, values)
  for _, value in ipairs(values) do
    table.insert(target, value)
  end
end

function M.edges(owner, rows, inventories)
  local edges = {}
  append_all(edges, restart_edges.extract_autonomous_edges(owner, rows))
  append_all(edges, restart_edges.extract_guard_boundary_edges(owner, rows))
  append_all(edges, restart_edges.extract_timeout_edges(owner, rows))
  append_all(edges, restart_edges.extract_entry_edges(owner, inventories.entry, rows))
  append_all(edges, restart_edges.extract_operator_reentry_edges(owner, inventories.operator_reentry))
  append_all(edges, restart_edges.extract_canonicalization_edges(owner, inventories.canonicalization))
  return edges
end

function M.derive(owner, rows, inventories)
  return pending_projection.derive_pending_projection(M.edges(owner, rows, inventories))
end

function M.frozen_projection()
  return deep_copy(frozen_projection)
end

local function expected_owner_projection(owner)
  local projection = {}
  for predecessor in pairs(owner_sources[owner] or {}) do
    local targets = frozen_projection[predecessor] or {}
    for target in pairs(targets) do
      projection[predecessor] = projection[predecessor] or {}
      projection[predecessor][target] = true
    end
  end
  return projection
end

local function union(left, right)
  local projection = {}
  for _, candidate in ipairs({ left, right }) do
    for predecessor, targets in pairs(candidate) do
      projection[predecessor] = projection[predecessor] or {}
      for target in pairs(targets) do
        projection[predecessor][target] = true
      end
    end
  end
  return projection
end

local function projection_bytes(projection)
  local lines = {}
  for predecessor, targets in pairs(projection) do
    for target in pairs(targets) do
      table.insert(lines, predecessor .. "->" .. target)
    end
  end
  table.sort(lines)
  return table.concat(lines, "\n")
end

function M.owner_errors(owner, projection)
  local sources = owner_sources[owner]
  if sources == nil then
    return { "pending projection has unknown lifecycle owner: " .. tostring(owner) }
  end
  local expected = expected_owner_projection(owner)
  local errors = {}
  if projection_bytes(projection) ~= projection_bytes(expected) then
    table.insert(errors, owner .. " pending projection differs from its frozen source subset")
  end

  local peer = owner == "github-devloop" and "github-devloop-pr" or "github-devloop"
  local composed = union(projection, expected_owner_projection(peer))
  if projection_bytes(composed) ~= projection_bytes(frozen_projection) then
    table.insert(errors, owner .. " pending projection union does not equal the frozen projection")
  end
  return errors
end

return M
