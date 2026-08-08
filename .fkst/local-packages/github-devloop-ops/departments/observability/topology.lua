local strings = require("contract.strings")

local M = {}

local renderable_kinds = {
  department = true,
  raiser = true,
}

local function require_graph(graph)
  if type(graph) ~= "table" then
    error("github-devloop: topology-graph-invalid: graph must be a table")
  end
  if graph.schema ~= "fkst.graph.v1" then
    error("github-devloop: topology-schema-invalid: graph schema must be fkst.graph.v1")
  end
  if type(graph.nodes) ~= "table" or type(graph.edges) ~= "table" then
    error("github-devloop: topology-graph-invalid: graph requires nodes and edges")
  end
end

local function canonical_from_node(node)
  local package_name = tostring(node and node.package or "")
  local name = tostring(node and node.name or "")
  if package_name ~= "" and name ~= "" then
    return package_name .. "." .. name
  end

  local id = tostring(node and node.id or "")
  local parsed_package, parsed_name = id:match("^[^:]+:([^%.]+)%.(.+)$")
  if parsed_package ~= nil and parsed_package ~= "" and parsed_name ~= nil and parsed_name ~= "" then
    return parsed_package .. "." .. parsed_name
  end

  return nil
end

local function node_package(node)
  local package_name = tostring(node and node.package or "")
  if package_name ~= "" then
    return package_name
  end
  local canonical = canonical_from_node(node)
  if canonical == nil then
    return nil
  end
  local parsed = canonical:match("^([^%.]+)%.")
  if parsed == "" then
    return nil
  end
  return parsed
end

local function node_label(node)
  local name = tostring(node and node.name or "")
  if name ~= "" then
    return name
  end
  local canonical = canonical_from_node(node)
  if canonical ~= nil then
    local _package_name, parsed_name = canonical:match("^([^%.]+)%.(.+)$")
    if parsed_name ~= nil and parsed_name ~= "" then
      return parsed_name
    end
  end
  return tostring(node and node.id or "unknown")
end

local function mermaid_label(value)
  return tostring(value or ""):gsub("\\", "\\\\"):gsub('"', '\\"'):gsub("\r", " "):gsub("\n", " ")
end

local function mermaid_id(prefix, raw, used)
  local base = tostring(raw or ""):gsub("[^%w_]", "_"):gsub("_+", "_"):gsub("^_+", ""):gsub("_+$", "")
  if base == "" then
    base = "node"
  end
  if base:match("^[%d]") then
    base = "n_" .. base
  end
  local candidate = tostring(prefix) .. "_" .. base
  if used[candidate] == nil or used[candidate] == raw then
    used[candidate] = raw
    return candidate
  end

  local suffixed = candidate .. "_" .. strings.decimal_checksum(raw)
  used[suffixed] = raw
  return suffixed
end

function M.validate_graph(graph)
  require_graph(graph)

  for _, node in ipairs(graph.nodes) do
    if renderable_kinds[node.kind] then
      if node_package(node) == nil then
        error("github-devloop: topology-node-invalid: renderable node missing package: " .. tostring(node.id or ""))
      end
      if canonical_from_node(node) == nil then
        error("github-devloop: topology-node-invalid: renderable node missing canonical name: " .. tostring(node.id or ""))
      end
    elseif node.kind == "queue" then
      if tostring(node.id or "") == "" then
        error("github-devloop: topology-node-invalid: queue node missing id")
      end
    end
  end

  return true
end

local function sorted_values(set)
  local values = {}
  for value in pairs(set or {}) do
    table.insert(values, value)
  end
  table.sort(values)
  return values
end

local function graph_indexes(graph)
  local node_by_id = {}
  local package_set = {}
  local render_nodes = {}
  local id_used = {}
  local lane_id_by_package = {}

  for _, node in ipairs(graph.nodes) do
    local graph_id = tostring(node.id or "")
    if graph_id ~= "" then
      node_by_id[graph_id] = node
    end
    if renderable_kinds[node.kind] then
      local package_name = node_package(node)
      local canonical = canonical_from_node(node)
      package_set[package_name] = true
      table.insert(render_nodes, {
        graph_id = graph_id,
        canonical = canonical,
        kind = tostring(node.kind or ""),
        label = node_label(node),
        package = package_name,
      })
    end
  end

  local packages = sorted_values(package_set)
  for _, package_name in ipairs(packages) do
    lane_id_by_package[package_name] = mermaid_id("lane", package_name, id_used)
  end

  table.sort(render_nodes, function(left, right)
    if left.package ~= right.package then
      return left.package < right.package
    end
    if left.kind ~= right.kind then
      return left.kind < right.kind
    end
    if left.canonical ~= right.canonical then
      return left.canonical < right.canonical
    end
    return left.graph_id < right.graph_id
  end)

  local mermaid_id_by_graph_id = {}
  for _, node in ipairs(render_nodes) do
    node.mermaid_id = mermaid_id("node", node.graph_id ~= "" and node.graph_id or node.canonical, id_used)
    if node.graph_id ~= "" then
      mermaid_id_by_graph_id[node.graph_id] = node.mermaid_id
    end
  end

  return {
    node_by_id = node_by_id,
    lane_id_by_package = lane_id_by_package,
    packages = packages,
    render_nodes = render_nodes,
    mermaid_id_by_graph_id = mermaid_id_by_graph_id,
  }
end

local function collect_edges(graph, indexes)
  local queue_producers = {}
  local queue_consumers = {}

  for _, edge in ipairs(graph.edges) do
    local from = tostring(edge.from or "")
    local to = tostring(edge.to or "")
    local relation = tostring(edge.relation or "")
    if relation == "produces" or relation == "raises" then
      local producer = indexes.mermaid_id_by_graph_id[from]
      local target = indexes.node_by_id[to]
      if producer ~= nil and target ~= nil and target.kind == "queue" then
        queue_producers[to] = queue_producers[to] or {}
        queue_producers[to][producer] = true
      end
    elseif relation == "consumes" then
      local source = indexes.node_by_id[from]
      local consumer = indexes.mermaid_id_by_graph_id[to]
      if consumer ~= nil and source ~= nil and source.kind == "queue" then
        queue_consumers[from] = queue_consumers[from] or {}
        queue_consumers[from][consumer] = true
      end
    end
  end

  local edge_set = {}
  for queue_id, producers in pairs(queue_producers) do
    local consumers = queue_consumers[queue_id] or {}
    for _, producer in ipairs(sorted_values(producers)) do
      for _, consumer in ipairs(sorted_values(consumers)) do
        if producer ~= consumer then
          edge_set[producer .. "\t" .. consumer] = {
            from = producer,
            to = consumer,
          }
        end
      end
    end
  end

  local edges = {}
  for _, edge in pairs(edge_set) do
    table.insert(edges, edge)
  end
  table.sort(edges, function(left, right)
    if left.from ~= right.from then
      return left.from < right.from
    end
    return left.to < right.to
  end)
  return edges
end

local function append_lane(lines, indexes, package_name)
  table.insert(lines, "  subgraph " .. indexes.lane_id_by_package[package_name] .. "[\"" .. mermaid_label(package_name) .. "\"]")
  for _, node in ipairs(indexes.render_nodes) do
    if node.package == package_name then
      table.insert(lines, "    " .. node.mermaid_id .. "[\"" .. mermaid_label(node.label) .. "\"]")
    end
  end
  table.insert(lines, "  end")
end

function M.render_mermaid(graph)
  M.validate_graph(graph)
  local indexes = graph_indexes(graph)
  local lines = { "flowchart LR" }
  for _, package_name in ipairs(indexes.packages) do
    append_lane(lines, indexes, package_name)
  end
  for _, edge in ipairs(collect_edges(graph, indexes)) do
    table.insert(lines, "  " .. edge.from .. " --> " .. edge.to)
  end
  return table.concat(lines, "\n")
end

return M
