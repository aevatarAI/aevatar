-- workflow.registry: sorted/unique indexed registry builders for workflow-sized registries.
local S = {}

local function index_name(index_entry)
  if type(index_entry) == "string" then
    return index_entry, nil
  end
  if type(index_entry) ~= "table" then
    return nil, nil
  end
  return index_entry.module, index_entry.key
end

local function assert_sorted_unique(index_module, index, owner)
  if type(index) ~= "table" then
    error(tostring(owner) .. ": registry index must be a table: " .. tostring(index_module))
  end
  local previous = nil
  local seen = {}
  for position, index_entry in ipairs(index) do
    local name = index_name(index_entry)
    if type(name) ~= "string" or name == "" then
      error(tostring(owner) .. ": registry index entry must be a non-empty string: " .. tostring(index_module))
    end
    if seen[name] then
      error(tostring(owner) .. ": duplicate registry index entry " .. name .. " in " .. tostring(index_module))
    end
    if previous ~= nil and previous > name then
      error(tostring(owner) .. ": registry index is not sorted: " .. tostring(index_module))
    end
    seen[name] = true
    previous = name
    if index[position + 1] == nil then
      break
    end
  end
end

S.assert_sorted_unique = assert_sorted_unique

local function entry_name(entry, module_name, key_field, owner)
  if type(entry) ~= "table" then
    error(tostring(owner) .. ": registry entry must return a table: " .. tostring(module_name))
  end
  local name = entry[key_field]
  if type(name) ~= "string" or name == "" then
    error(tostring(owner) .. ": registry entry missing " .. tostring(key_field) .. ": " .. tostring(module_name))
  end
  return name
end

local function resolve_entry(loaded, M, helpers)
  if type(loaded) == "function" then
    return loaded(M, helpers or {})
  end
  return loaded
end

function S.build_indexed_array(index_module, index, entries, key_field, M, helpers, owner)
  owner = owner or "registry"
  assert_sorted_unique(index_module, index, owner)
  if type(entries) ~= "table" then
    error(tostring(owner) .. ": registry entries must be a table: " .. tostring(index_module))
  end
  local rows = {}
  local seen = {}
  for position, index_entry in ipairs(index) do
    local name, expected_key = index_name(index_entry)
    if type(name) ~= "string" or name == "" then
      error(tostring(owner) .. ": registry index entry must declare a module: " .. tostring(index_module))
    end
    local entry = resolve_entry(entries[position], M, helpers)
    local key = entry_name(entry, name, key_field, owner)
    if expected_key == nil then
      expected_key = name
    end
    if key ~= expected_key then
      error(tostring(owner) .. ": registry entry key " .. key .. " does not match index entry " .. expected_key)
    end
    if seen[key] then
      error(tostring(owner) .. ": duplicate registry entry key " .. key)
    end
    seen[key] = true
    table.insert(rows, entry)
  end
  if entries[#index + 1] ~= nil then
    error(tostring(owner) .. ": registry entries exceed index length: " .. tostring(index_module))
  end
  return rows
end

function S.build_indexed_map(index_module, index, entries, key_field, M, helpers, owner)
  owner = owner or "registry"
  local rows = S.build_indexed_array(index_module, index, entries, key_field, M, helpers, owner)
  local map = {}
  for _, row in ipairs(rows) do
    local key = row[key_field]
    local value = {}
    for field, field_value in pairs(row) do
      if field ~= key_field then
        value[field] = field_value
      end
    end
    map[key] = value
  end
  return map
end

function S.install_indexed_installers(index_module, index, installers, M, owner)
  owner = owner or "registry"
  assert_sorted_unique(index_module, index, owner)
  if type(installers) ~= "table" then
    error(tostring(owner) .. ": registry installers must be a table: " .. tostring(index_module))
  end
  for position, index_entry in ipairs(index) do
    local name = index_name(index_entry)
    if type(name) ~= "string" or name == "" then
      error(tostring(owner) .. ": registry index entry must declare a module: " .. tostring(index_module))
    end
    local installer = installers[position]
    if type(installer) ~= "function" then
      error(tostring(owner) .. ": registry installer must be a function: " .. tostring(name))
    end
    installer(M)
  end
  if installers[#index + 1] ~= nil then
    error(tostring(owner) .. ": registry installers exceed index length: " .. tostring(index_module))
  end
end

return S
