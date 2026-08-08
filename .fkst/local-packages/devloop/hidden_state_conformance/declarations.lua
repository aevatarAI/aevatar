local D = {}

local ALLOWLIST_PATH = "migration/hidden-state.allowlist"

local function package_name(core)
  return tostring(core.restart_package_name or "github-devloop")
end

local function key(core, row, fact_family, successor)
  return table.concat({
    package_name(core),
    tostring(row.from_state or "?"),
    tostring(fact_family or "?"),
    tostring(successor or "?"),
  }, "|")
end
D.key = key

local function key_prefix(core, row)
  return table.concat({
    package_name(core),
    tostring(row.from_state or "?"),
    "",
  }, "|")
end
D.key_prefix = key_prefix

local function parse_allowlist_line(line)
  local text = tostring(line or "")
  if text == "" or text:match("^%s*#") then
    return nil
  end
  local parts = {}
  for part in text:gmatch("[^|]+") do
    table.insert(parts, part)
  end
  if #parts < 6 then
    return nil, "invalid hidden-state allowlist line: " .. text
  end
  if not tostring(parts[5]):match("^issue=#?%d+$") or tostring(parts[6]) == "why=" or not tostring(parts[6]):match("^why=") then
    return nil, "invalid hidden-state allowlist metadata: " .. text
  end
  return table.concat({ parts[1], parts[2], parts[3], parts[4] }, "|")
end

function D.load_allowlist()
  local out = {}
  local ok, text = pcall(file.read, ALLOWLIST_PATH)
  if not ok then
    return out
  end
  for line in tostring(text or ""):gmatch("[^\n]+") do
    local parsed, err = parse_allowlist_line(line)
    if err ~= nil then
      table.insert(out, "__ERROR__|" .. err)
    elseif parsed ~= nil then
      out[parsed] = true
    end
  end
  return out
end

local function has_poll_surface(fact)
  local surfaces = fact.observe_surfaces or {}
  return surfaces.issue == true or surfaces.pr == true or surfaces.liveness_scan == true
end

local function row_has_declared_surface(row, fact)
  local row_surfaces = row.observe_surfaces or {}
  for surface, enabled in pairs(fact.observe_surfaces or {}) do
    if enabled == true and row_surfaces[surface] == true then
      return true
    end
  end
  return false
end

local function declared_by_key(core, rows)
  local declared = {}
  for _, row in ipairs(rows or {}) do
    for _, fact in ipairs(row.advancing_facts or {}) do
      declared[D.key(core, row, fact.fact_family, fact.successor)] = true
    end
  end
  return declared
end

function D.global_advancing_fact_variants(rows)
  local variants = {}
  local seen = {}
  for _, row in ipairs(rows or {}) do
    for _, fact in ipairs(row.advancing_facts or {}) do
      local family = tostring(fact.fact_family or "")
      if family ~= "" then
        local variant_key = family .. "\0" .. tostring(fact.successor or "")
        if seen[variant_key] ~= true then
          seen[variant_key] = true
          table.insert(variants, fact)
        end
      end
    end
  end
  return variants
end

local function first_successor(row)
  for _, successor in ipairs(row.to_states or {}) do
    local value = tostring(successor or "")
    if value ~= "" and value ~= tostring(row.from_state or "") then
      return value
    end
  end
  return nil
end

function D.remember_fact_family(by_family, ordered, declared, overwrite)
  local family = tostring((declared or {}).fact_family or "")
  if family == "" then
    return
  end
  if by_family[family] == nil then
    table.insert(ordered, family)
  end
  if overwrite == true or by_family[family] == nil then
    by_family[family] = declared
  end
end

function D.required_fact_variant(row, required)
  local family = tostring((required or {}).family or "")
  if family == "" then
    return nil
  end
  return {
    fact_family = family,
    successor = first_successor(row),
    synthetic_required_fact = true,
  }
end

local function has_declared_advancing_facts(row)
  return type(row.advancing_facts) == "table" and #row.advancing_facts > 0
end

function D.exemption_reason(row)
  local exemption = row.non_durable_advance
  if type(exemption) ~= "table" then
    return nil
  end
  -- Exempt only rows with no autonomous poll-derived durable-fact successor:
  -- pure operator-command reentry or terminal/recovery holds.
  local category = tostring(exemption.category or "")
  if category ~= "operator-reentry" and category ~= "terminal-hold" then
    return nil
  end
  local reason = tostring(exemption.reason or "")
  if reason == "" then
    return nil
  end
  return reason
end

local function has_allowlisted_row(core, allowlist, row)
  local prefix = D.key_prefix(core, row)
  for item in pairs(allowlist or {}) do
    if item:sub(1, #prefix) == prefix then
      return true
    end
  end
  return false
end

local function declaration_errors(core, rows, allowlist)
  local messages = {}
  local allowed_derivations = {
    ["source_ref:entity"] = true,
    ["source_ref:issue"] = true,
    ["source_ref:pr"] = true,
  }
  for _, row in ipairs(rows or {}) do
    local successors = {}
    for _, successor in ipairs(row.to_states or {}) do
      successors[successor] = true
    end
    if row.terminal ~= true
      and not has_declared_advancing_facts(row)
      and D.exemption_reason(row) == nil
      and not has_allowlisted_row(core, allowlist, row) then
      table.insert(messages, key_prefix(core, row) .. "*: non-terminal row must declare advancing_facts, non_durable_advance, or a shrink-only allowlist entry")
    end
    for _, fact in ipairs(row.advancing_facts or {}) do
      local label = key(core, row, fact.fact_family, fact.successor)
      if type(fact.fact_family) ~= "string" or fact.fact_family == "" then
        table.insert(messages, label .. ": advancing_facts entry must declare fact_family")
      end
      if type(fact.successor) ~= "string" or fact.successor == "" then
        table.insert(messages, label .. ": advancing_facts entry must declare successor")
      elseif successors[fact.successor] ~= true and tostring(fact.successor or "") ~= tostring(row.from_state or "") then
        table.insert(messages, label .. ": advancing_facts successor is not in to_states")
      end
      if type(fact.observe_surfaces) ~= "table" or next(fact.observe_surfaces) == nil then
        table.insert(messages, label .. ": advancing_facts entry must declare observe_surfaces")
      elseif not row_has_declared_surface(row, fact) then
        table.insert(messages, label .. ": advancing_facts observe_surfaces are not declared on row")
      elseif not has_poll_surface(fact) then
        table.insert(messages, label .. ": advancing fact must be re-derivable on a poll observe surface")
      end
      if allowed_derivations[tostring(fact.source_ref_derivation or "")] ~= true then
        table.insert(messages, label .. ": advancing_facts entry must declare source_ref_derivation")
      end
    end
  end
  local declared = declared_by_key(core, rows)
  local current_package_prefix = package_name(core) .. "|"
  for item in pairs(allowlist or {}) do
    if item:match("^__ERROR__|") then
      table.insert(messages, item:gsub("^__ERROR__|", ""))
    elseif item:sub(1, #current_package_prefix) == current_package_prefix and declared[item] == nil then
      table.insert(messages, item .. ": hidden-state allowlist entry has no matching advancing_facts row")
    end
  end
  return messages
end
D.declaration_errors = declaration_errors

return D
