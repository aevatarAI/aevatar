local M = {}

local function is_nonempty_string(value)
  return type(value) == "string" and value ~= ""
end

local function build_inventory_edge_id(owner, row_id, kind, semantic_variant)
  return owner .. "/" .. row_id .. "/" .. kind .. "/" .. semantic_variant
end

local successor_kinds = {
  autonomous = true,
  guard_boundary = true,
  timeout = true,
}

local timeout_policy_by_actionable_source = {
  ["state_entry:v1"] = "timeout.state_entry_legacy_v1",
  ["codex_run:v1"] = "timeout.codex_run_legacy_v1",
  ["live_defer_heartbeat:v1"] = "timeout.heartbeat_legacy_v1",
  ["live_defer_epoch:v1"] = "timeout.durable_clear_legacy_v1",
  ["child_workflow_wait:v1"] = "timeout.child_workflow_legacy_v1",
}

local function row_id(row)
  local value = type(row) == "table" and row.from_state or nil
  if not is_nonempty_string(value) then
    error("devloop.restart_edges: row.from_state must be a non-empty string")
  end
  return value
end

local function responsibility_successors(row, required)
  local signature = type(row) == "table" and row.responsibility_signature or nil
  if signature == nil and not required then
    return {}
  end
  local successors = type(signature) == "table" and signature.successors or nil
  if type(successors) ~= "table" then
    error("devloop.restart_edges: responsibility_signature.successors must be a table")
  end
  return successors
end

local function validate_responsibility_successor(successor)
  if type(successor) ~= "table" or not is_nonempty_string(successor.state) then
    error("devloop.restart_edges: successor.state must be a non-empty string")
  end
  if not is_nonempty_string(successor.output_variant) then
    error("devloop.restart_edges: successor.output_variant must be a non-empty string")
  end
  if successor_kinds[successor.kind] ~= true then
    error("devloop.restart_edges: successor.kind must be autonomous, guard_boundary, or timeout")
  end
end

local function attach_cas_metadata(edge, declaration, context)
  if declaration.cas_policy_id ~= nil then
    if not is_nonempty_string(declaration.cas_policy_id) then
      error("devloop.restart_edges: " .. context .. ".cas_policy_id must be a non-empty string")
    end
    edge.cas_policy_id = declaration.cas_policy_id
  end
  if declaration.cas_variant ~= nil then
    if not is_nonempty_string(declaration.cas_variant) then
      error("devloop.restart_edges: " .. context .. ".cas_variant must be a non-empty string")
    end
    edge.cas_variant = declaration.cas_variant
  end
end

local function validate_effect_ids(effect_ids, context)
  if type(effect_ids) ~= "table" then
    error("devloop.restart_edges: " .. context .. ".effect_ids must be an array of strings")
  end
  local count = 0
  for key, effect_id in pairs(effect_ids) do
    if type(key) ~= "number" or key < 1 or key % 1 ~= 0 or type(effect_id) ~= "string" then
      error("devloop.restart_edges: " .. context .. ".effect_ids must be an array of strings")
    end
    count = count + 1
  end
  if count ~= #effect_ids then
    error("devloop.restart_edges: " .. context .. ".effect_ids must be a dense array")
  end
end

local function validate_effect_entitlement(entry, context)
  if type(entry) ~= "table" then
    error("devloop.restart_edges: " .. context .. " must be a table")
  end
  if not is_nonempty_string(entry.id) then
    error("devloop.restart_edges: " .. context .. ".id must be a non-empty string")
  end
  validate_effect_ids(entry.effect_ids, context)
end

local function attach_effect_entitlements(edge, declaration, context)
  local entitlements = declaration.transition_effect_entitlements
  if entitlements == nil then
    return
  end
  if type(entitlements) ~= "table" then
    error("devloop.restart_edges: " .. context .. ".transition_effect_entitlements must be a table")
  end
  validate_effect_entitlement(
    entitlements.apply,
    context .. ".transition_effect_entitlements.apply"
  )
  validate_effect_entitlement(
    entitlements.idempotent,
    context .. ".transition_effect_entitlements.idempotent"
  )
  edge.transition_effect_entitlements = entitlements
end

local function attach_pending_order(edge, declaration, context)
  local pending_order = declaration.pending_order
  if pending_order == nil then
    return
  end
  if type(pending_order) ~= "table" then
    error("devloop.restart_edges: " .. context .. ".pending_order must be a table")
  end
  if type(pending_order.participates) ~= "boolean" then
    error("devloop.restart_edges: " .. context .. ".pending_order.participates must be a boolean")
  end
  if pending_order.participates and not is_nonempty_string(pending_order.predecessor_state) then
    error("devloop.restart_edges: " .. context .. ".pending_order.predecessor_state must be a non-empty string when participating")
  end
  edge.pending_order = pending_order
end

local function receiver_activations(row)
  local activations = type(row) == "table" and row.receiver_activations or nil
  if activations == nil then
    return {}
  end
  if type(activations) ~= "table" then
    error("devloop.restart_edges: receiver_activations must be a table")
  end
  return activations
end

local function validate_receiver_activation(activation)
  if type(activation) ~= "table" then
    error("devloop.restart_edges: receiver activation must be a table")
  end
  if activation.kind ~= "entry" then
    error("devloop.restart_edges: receiver activation kind must be entry")
  end
  if activation.boundary ~= nil and not is_nonempty_string(activation.boundary) then
    error("devloop.restart_edges: receiver activation boundary must be nil or a non-empty string")
  end
  if not is_nonempty_string(activation.target) then
    error("devloop.restart_edges: receiver activation target must be a non-empty string")
  end
  if not is_nonempty_string(activation.output_variant) then
    error("devloop.restart_edges: receiver activation output_variant must be a non-empty string")
  end
end

local function timeout_policy_id(row)
  local actionable_epoch = type(row) == "table" and row.actionable_epoch or nil
  local source = type(actionable_epoch) == "table" and actionable_epoch.source or nil
  local policy_id = timeout_policy_by_actionable_source[source]
  if policy_id == nil then
    error("devloop.restart_edges: timeout edge actionable_epoch.source must select a closed timeout evidence policy")
  end
  return policy_id
end

function M.extract_entry_edges(owner, inventory, rows)
  if not is_nonempty_string(owner) then
    error("devloop.restart_edges: owner must be a non-empty string")
  end
  if type(inventory) ~= "table" then
    error("devloop.restart_edges: entry inventory must be a table")
  end
  if type(rows) ~= "table" then
    error("devloop.restart_edges: rows must be a table")
  end

  local edges = {}
  local seen_ids = {}
  for _, authored in ipairs(inventory) do
    if type(authored) ~= "table" then
      error("devloop.restart_edges: entry edge must be a table")
    end
    if authored.owner ~= owner then
      error("devloop.restart_edges: entry edge owner must match extractor owner")
    end
    if not is_nonempty_string(authored.row_id) then
      error("devloop.restart_edges: entry edge row_id must be a non-empty string")
    end
    if authored.kind ~= "entry" then
      error("devloop.restart_edges: entry edge kind must be entry")
    end
    if not is_nonempty_string(authored.semantic_variant) then
      error("devloop.restart_edges: entry edge semantic_variant must be a non-empty string")
    end
    if authored.semantic_variant:find("/", 1, true) ~= nil then
      error("devloop.restart_edges: entry edge semantic_variant must not contain /")
    end

    local source = authored.source
    if type(source) ~= "table" then
      error("devloop.restart_edges: entry edge source must be a table")
    end
    if source.state ~= nil then
      error("devloop.restart_edges: entry edge source.state must be nil")
    end
    if not is_nonempty_string(source.boundary) then
      error("devloop.restart_edges: entry edge source.boundary must be a non-empty string")
    end
    if not is_nonempty_string(authored.target) then
      error("devloop.restart_edges: entry edge target must be a non-empty string")
    end

    local provenance = authored.provenance
    if type(provenance) ~= "table" then
      error("devloop.restart_edges: entry edge provenance must be a table")
    end
    if provenance.owner ~= owner then
      error("devloop.restart_edges: entry edge provenance.owner must match extractor owner")
    end
    if not is_nonempty_string(provenance.row) then
      error("devloop.restart_edges: entry edge provenance.row must be a non-empty string")
    end
    if not is_nonempty_string(provenance.field) then
      error("devloop.restart_edges: entry edge provenance.field must be a non-empty string")
    end
    local id = build_inventory_edge_id(
      authored.owner,
      authored.row_id,
      authored.kind,
      authored.semantic_variant
    )
    if seen_ids[id] then
      error("devloop.restart_edges: duplicate edge id " .. id)
    end
    seen_ids[id] = true

    local edge = {
      id = id,
      owner = authored.owner,
      row_id = authored.row_id,
      kind = authored.kind,
      source = {
        state = nil,
        boundary = source.boundary,
      },
      target = authored.target,
      semantic_variant = authored.semantic_variant,
      provenance = {
        owner = provenance.owner,
        row = provenance.row,
        field = provenance.field,
      },
    }
    attach_cas_metadata(edge, authored, "entry edge")
    attach_pending_order(edge, authored, "entry edge")
    table.insert(edges, edge)
  end

  for _, row in ipairs(rows) do
    local current_row_id = row_id(row)
    for _, activation in ipairs(receiver_activations(row)) do
      validate_receiver_activation(activation)
      local id = owner .. "/" .. current_row_id .. "/entry/" .. activation.output_variant
      if seen_ids[id] then
        error("devloop.restart_edges: duplicate edge id " .. id)
      end
      seen_ids[id] = true
      local edge = {
        id = id,
        owner = owner,
        row_id = current_row_id,
        kind = "entry",
        source = { state = current_row_id, boundary = activation.boundary },
        target = activation.target,
        semantic_variant = activation.output_variant,
        provenance = {
          owner = owner,
          row = current_row_id,
          field = "receiver_activations",
        },
      }
      attach_cas_metadata(edge, activation, "entry receiver activation")
      attach_pending_order(edge, activation, "entry receiver activation")
      table.insert(edges, edge)
    end
  end
  return edges
end

function M.extract_operator_reentry_edges(owner, inventory)
  if not is_nonempty_string(owner) then
    error("devloop.restart_edges: owner must be a non-empty string")
  end
  if type(inventory) ~= "table" then
    error("devloop.restart_edges: operator reentry inventory must be a table")
  end

  local edges = {}
  local seen_ids = {}
  for _, authored in ipairs(inventory) do
    if type(authored) ~= "table" then
      error("devloop.restart_edges: operator reentry edge must be a table")
    end
    if authored.owner ~= owner then
      error("devloop.restart_edges: operator reentry edge owner must match extractor owner")
    end
    if not is_nonempty_string(authored.row_id) then
      error("devloop.restart_edges: operator reentry edge row_id must be a non-empty string")
    end
    if authored.kind ~= "operator_reentry" then
      error("devloop.restart_edges: operator reentry edge kind must be operator_reentry")
    end
    if not is_nonempty_string(authored.semantic_variant) then
      error("devloop.restart_edges: operator reentry edge semantic_variant must be a non-empty string")
    end
    if authored.semantic_variant:find("/", 1, true) ~= nil then
      error("devloop.restart_edges: operator reentry edge semantic_variant must not contain /")
    end
    local source = authored.source
    if type(source) ~= "table" then
      error("devloop.restart_edges: operator reentry edge source must be a table")
    end
    if not is_nonempty_string(source.state) then
      error("devloop.restart_edges: operator reentry edge source.state must be a non-empty string")
    end
    if source.boundary ~= nil and not is_nonempty_string(source.boundary) then
      error("devloop.restart_edges: operator reentry edge source.boundary must be nil or a non-empty string")
    end
    if not is_nonempty_string(authored.target) then
      error("devloop.restart_edges: operator reentry edge target must be a non-empty string")
    end

    local cause_evidence = authored.cause_evidence
    if type(cause_evidence) ~= "table" then
      error("devloop.restart_edges: operator reentry edge cause_evidence must be a table")
    end
    if not is_nonempty_string(cause_evidence.command) then
      error("devloop.restart_edges: operator reentry edge cause_evidence.command must be a non-empty string")
    end
    if cause_evidence.requires_applied_certificate ~= true then
      error("devloop.restart_edges: operator reentry edge cause_evidence.requires_applied_certificate must be true")
    end
    if cause_evidence.resolver ~= "operator_commands" then
      error("devloop.restart_edges: operator reentry edge cause_evidence.resolver must be operator_commands")
    end

    local provenance = authored.provenance
    if type(provenance) ~= "table" then
      error("devloop.restart_edges: operator reentry edge provenance must be a table")
    end
    if provenance.owner ~= owner then
      error("devloop.restart_edges: operator reentry edge provenance.owner must match extractor owner")
    end
    if not is_nonempty_string(provenance.row) then
      error("devloop.restart_edges: operator reentry edge provenance.row must be a non-empty string")
    end
    if not is_nonempty_string(provenance.field) then
      error("devloop.restart_edges: operator reentry edge provenance.field must be a non-empty string")
    end
    local id = build_inventory_edge_id(
      authored.owner,
      authored.row_id,
      authored.kind,
      authored.semantic_variant
    )
    if seen_ids[id] then
      error("devloop.restart_edges: duplicate edge id " .. id)
    end
    seen_ids[id] = true

    local edge = {
      id = id,
      owner = authored.owner,
      row_id = authored.row_id,
      kind = authored.kind,
      source = {
        state = source.state,
        boundary = source.boundary,
      },
      target = authored.target,
      semantic_variant = authored.semantic_variant,
      cause_evidence = {
        command = cause_evidence.command,
        requires_applied_certificate = cause_evidence.requires_applied_certificate,
        resolver = cause_evidence.resolver,
      },
      provenance = {
        owner = provenance.owner,
        row = provenance.row,
        field = provenance.field,
      },
    }
    attach_cas_metadata(edge, authored, "operator_reentry edge")
    attach_pending_order(edge, authored, "operator_reentry edge")
    table.insert(edges, edge)
  end
  return edges
end

function M.extract_canonicalization_edges(owner, inventory)
  if not is_nonempty_string(owner) then
    error("devloop.restart_edges: owner must be a non-empty string")
  end
  if type(inventory) ~= "table" then
    error("devloop.restart_edges: canonicalization inventory must be a table")
  end

  local edges = {}
  local seen_ids = {}
  for _, authored in ipairs(inventory) do
    if type(authored) ~= "table" then
      error("devloop.restart_edges: canonicalization edge must be a table")
    end
    if authored.owner ~= owner then
      error("devloop.restart_edges: canonicalization edge owner must match extractor owner")
    end
    if not is_nonempty_string(authored.row_id) then
      error("devloop.restart_edges: canonicalization edge row_id must be a non-empty string")
    end
    if authored.kind ~= "canonicalization" then
      error("devloop.restart_edges: canonicalization edge kind must be canonicalization")
    end
    if not is_nonempty_string(authored.semantic_variant) then
      error("devloop.restart_edges: canonicalization edge semantic_variant must be a non-empty string")
    end
    if authored.semantic_variant:find("/", 1, true) ~= nil then
      error("devloop.restart_edges: canonicalization edge semantic_variant must not contain /")
    end

    local source = authored.source
    if type(source) ~= "table" then
      error("devloop.restart_edges: canonicalization edge source must be a table")
    end
    if not is_nonempty_string(source.state) then
      error("devloop.restart_edges: canonicalization edge source.state must be a non-empty string")
    end
    if source.boundary ~= nil then
      error("devloop.restart_edges: canonicalization edge source.boundary must be nil")
    end
    if not is_nonempty_string(authored.target) then
      error("devloop.restart_edges: canonicalization edge target must be a non-empty string")
    end

    local cause_evidence = authored.cause_evidence
    if type(cause_evidence) ~= "table" then
      error("devloop.restart_edges: canonicalization edge cause_evidence must be a table")
    end
    if not is_nonempty_string(cause_evidence.marker) then
      error("devloop.restart_edges: canonicalization edge cause_evidence.marker must be a non-empty string")
    end
    if not is_nonempty_string(cause_evidence.resolver) then
      error("devloop.restart_edges: canonicalization edge cause_evidence.resolver must be a non-empty string")
    end

    local provenance = authored.provenance
    if type(provenance) ~= "table" then
      error("devloop.restart_edges: canonicalization edge provenance must be a table")
    end
    if provenance.owner ~= owner then
      error("devloop.restart_edges: canonicalization edge provenance.owner must match extractor owner")
    end
    if not is_nonempty_string(provenance.row) then
      error("devloop.restart_edges: canonicalization edge provenance.row must be a non-empty string")
    end
    if not is_nonempty_string(provenance.field) then
      error("devloop.restart_edges: canonicalization edge provenance.field must be a non-empty string")
    end
    local id = build_inventory_edge_id(
      authored.owner,
      authored.row_id,
      authored.kind,
      authored.semantic_variant
    )
    if seen_ids[id] then
      error("devloop.restart_edges: duplicate edge id " .. id)
    end
    seen_ids[id] = true

    local edge = {
      id = id,
      owner = authored.owner,
      row_id = authored.row_id,
      kind = authored.kind,
      source = {
        state = source.state,
        boundary = nil,
      },
      target = authored.target,
      semantic_variant = authored.semantic_variant,
      cause_evidence = {
        marker = cause_evidence.marker,
        resolver = cause_evidence.resolver,
      },
      provenance = {
        owner = provenance.owner,
        row = provenance.row,
        field = provenance.field,
      },
    }
    attach_cas_metadata(edge, authored, "canonicalization edge")
    attach_pending_order(edge, authored, "canonicalization edge")
    table.insert(edges, edge)
  end
  return edges
end

function M.extract_autonomous_edges(owner, rows)
  if not is_nonempty_string(owner) then
    error("devloop.restart_edges: owner must be a non-empty string")
  end
  if type(rows) ~= "table" then
    error("devloop.restart_edges: rows must be a table")
  end

  local edges = {}
  local seen_ids = {}
  for _, row in ipairs(rows) do
    local current_row_id = row_id(row)
    local successors = responsibility_successors(row, true)

    for _, successor in ipairs(successors) do
      validate_responsibility_successor(successor)
      if successor.kind == "autonomous" then
        local id = owner .. "/" .. current_row_id .. "/autonomous/" .. successor.output_variant
        if seen_ids[id] then
          error("devloop.restart_edges: duplicate edge id " .. id)
        end
        seen_ids[id] = true
        local edge = {
          id = id,
          owner = owner,
          row_id = current_row_id,
          kind = "autonomous",
          source = { state = current_row_id, boundary = nil },
          target = successor.state,
          semantic_variant = successor.output_variant,
          provenance = {
            owner = owner,
            row = current_row_id,
            field = "responsibility_signature.successors",
          },
        }
        attach_cas_metadata(edge, successor, "autonomous successor")
        attach_effect_entitlements(edge, successor, "autonomous successor")
        attach_pending_order(edge, successor, "autonomous successor")
        table.insert(edges, edge)
      end
    end
  end
  return edges
end

function M.extract_guard_boundary_edges(owner, rows)
  if not is_nonempty_string(owner) then
    error("devloop.restart_edges: owner must be a non-empty string")
  end
  if type(rows) ~= "table" then
    error("devloop.restart_edges: rows must be a table")
  end

  local edges = {}
  local seen_ids = {}
  for _, row in ipairs(rows) do
    local current_row_id = row_id(row)
    for _, successor in ipairs(responsibility_successors(row, false)) do
      validate_responsibility_successor(successor)
      if successor.kind == "guard_boundary" then
        local id = owner .. "/" .. current_row_id .. "/guard_boundary/" .. successor.output_variant
        if seen_ids[id] then
          error("devloop.restart_edges: duplicate edge id " .. id)
        end
        seen_ids[id] = true
        local edge = {
          id = id,
          owner = owner,
          row_id = current_row_id,
          kind = "guard_boundary",
          source = { state = current_row_id, boundary = nil },
          target = successor.state,
          semantic_variant = successor.output_variant,
          provenance = {
            owner = owner,
            row = current_row_id,
            field = "responsibility_signature.successors",
          },
        }
        attach_cas_metadata(edge, successor, "guard_boundary edge")
        attach_pending_order(edge, successor, "guard_boundary edge")
        table.insert(edges, edge)
      end
    end

    local guard_boundaries = row.guard_boundaries
    if guard_boundaries ~= nil then
      if type(guard_boundaries) ~= "table" then
        error("devloop.restart_edges: guard_boundaries must be a table")
      end

      for _, guard_boundary in ipairs(guard_boundaries) do
        if type(guard_boundary) ~= "table" or not is_nonempty_string(guard_boundary.name) then
          error("devloop.restart_edges: guard_boundary.name must be a non-empty string")
        end

        local successors = guard_boundary.successors
        if type(successors) ~= "table" then
          error("devloop.restart_edges: guard_boundary.successors must be a table")
        end

        for _, successor in ipairs(successors) do
          if type(successor) ~= "table" or not is_nonempty_string(successor.state) then
            error("devloop.restart_edges: successor.state must be a non-empty string")
          end
          if not is_nonempty_string(successor.output_variant) then
            error("devloop.restart_edges: successor.output_variant must be a non-empty string")
          end
          if successor.kind ~= nil and successor.kind ~= "guard_boundary" and successor.kind ~= "timeout" then
            error("devloop.restart_edges: guard boundary successor.kind must be guard_boundary, timeout, or nil")
          end
          if successor.kind ~= "timeout" then
            local id = owner .. "/" .. current_row_id .. "/guard_boundary/" .. guard_boundary.name .. "/" .. successor.output_variant
            if seen_ids[id] then
              error("devloop.restart_edges: duplicate edge id " .. id)
            end
            seen_ids[id] = true
            local edge = {
              id = id,
              owner = owner,
              row_id = current_row_id,
              kind = "guard_boundary",
              source = { state = current_row_id, boundary = guard_boundary.name },
              target = successor.state,
              semantic_variant = successor.output_variant,
              provenance = {
                owner = owner,
                row = current_row_id,
                field = "guard_boundaries",
              },
            }
            attach_cas_metadata(edge, successor, "guard_boundary edge")
            attach_pending_order(edge, successor, "guard_boundary edge")
            table.insert(edges, edge)
          end
        end
      end
    end
  end
  return edges
end

function M.extract_timeout_edges(owner, rows)
  if not is_nonempty_string(owner) then
    error("devloop.restart_edges: owner must be a non-empty string")
  end
  if type(rows) ~= "table" then
    error("devloop.restart_edges: rows must be a table")
  end

  local edges = {}
  local seen_ids = {}
  for _, row in ipairs(rows) do
    local current_row_id = row_id(row)
    local function insert_timeout_edge(successor, boundary, provenance_field)
      local policy_id = timeout_policy_id(row)
      local id_segments = { owner, current_row_id, "timeout" }
      if boundary ~= nil then
        table.insert(id_segments, boundary)
      end
      table.insert(id_segments, successor.output_variant)
      local id = table.concat(id_segments, "/")
      if seen_ids[id] then
        error("devloop.restart_edges: duplicate edge id " .. id)
      end
      seen_ids[id] = true
      local edge = {
        id = id,
        owner = owner,
        row_id = current_row_id,
        kind = "timeout",
        source = { state = current_row_id, boundary = boundary },
        target = successor.state,
        semantic_variant = successor.output_variant,
        timeout_evidence_policy_id = policy_id,
        provenance = {
          owner = owner,
          row = current_row_id,
          field = provenance_field,
        },
      }
      attach_cas_metadata(edge, successor, "timeout edge")
      attach_pending_order(edge, successor, "timeout edge")
      table.insert(edges, edge)
    end

    for _, successor in ipairs(responsibility_successors(row, false)) do
      validate_responsibility_successor(successor)
      if successor.kind == "timeout" then
        insert_timeout_edge(successor, nil, "responsibility_signature.successors")
      end
    end

    local guard_boundaries = row.guard_boundaries
    if guard_boundaries ~= nil then
      if type(guard_boundaries) ~= "table" then
        error("devloop.restart_edges: guard_boundaries must be a table")
      end
      for _, guard_boundary in ipairs(guard_boundaries) do
        if type(guard_boundary) ~= "table" or not is_nonempty_string(guard_boundary.name) then
          error("devloop.restart_edges: guard_boundary.name must be a non-empty string")
        end
        if type(guard_boundary.successors) ~= "table" then
          error("devloop.restart_edges: guard_boundary.successors must be a table")
        end
        for _, successor in ipairs(guard_boundary.successors) do
          if type(successor) ~= "table" or not is_nonempty_string(successor.state) then
            error("devloop.restart_edges: successor.state must be a non-empty string")
          end
          if not is_nonempty_string(successor.output_variant) then
            error("devloop.restart_edges: successor.output_variant must be a non-empty string")
          end
          if successor.kind ~= nil and successor.kind ~= "guard_boundary" and successor.kind ~= "timeout" then
            error("devloop.restart_edges: guard boundary successor.kind must be guard_boundary, timeout, or nil")
          end
          if successor.kind == "timeout" then
            insert_timeout_edge(successor, guard_boundary.name, "guard_boundaries")
          end
        end
      end
    end
  end
  return edges
end

function M.schema()
  return {
    structural_fields = { "id", "owner", "row_id", "kind", "source", "target", "provenance" },
    -- Typed edge kinds are authored by each lifecycle owner.
    extracted_kinds = {
      autonomous = true,
      canonicalization = true,
      entry = true,
      guard_boundary = true,
      operator_reentry = true,
      timeout = true,
    },
    deferred_kinds = {},
  }
end

return M
