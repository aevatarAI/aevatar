local M = {}

local authority_classes = {
  ["grantless-non-lifecycle"] = true,
  ["grantless-published-intent"] = true,
  ["grantless-telemetry"] = true,
  ["lifecycle-authoritative"] = true,
}

local effect_kinds = {
  adapter = true,
  codex = true,
  comment = true,
  git = true,
  label = true,
  merge = true,
  queue = true,
}

local id_patterns = {
  adapter = "^adapter:[%w%._%-%/]+$",
  codex = "^codex%.dispatch:[%w%._%-%/]+$",
  git = "^git%.push:[%w%._%-%/]+$",
  queue = "^queue:[%w%._%-%/]+$",
}

local record_fields = {
  authority_class = true,
  callsite = true,
  dedup_marker_family = true,
  effect_kind = true,
  id = true,
  owner = true,
}

local callsite_fields = {
  department = true,
  site = true,
}

local function is_nonempty_string(value)
  return type(value) == "string" and value ~= ""
end

local function assert_exact_fields(value, expected, subject)
  for key in pairs(value) do
    if expected[key] ~= true then
      error("devloop.restart_sinks: " .. subject .. " has unknown field " .. tostring(key))
    end
  end
  for key in pairs(expected) do
    if value[key] == nil then
      error("devloop.restart_sinks: " .. subject .. " is missing field " .. tostring(key))
    end
  end
end

local function array_length(value, subject)
  local count = 0
  local maximum = 0
  for key in pairs(value) do
    if type(key) ~= "number" or key < 1 or key % 1 ~= 0 then
      error("devloop.restart_sinks: " .. subject .. " must be an array")
    end
    count = count + 1
    maximum = math.max(maximum, key)
  end
  if maximum ~= count then
    error("devloop.restart_sinks: " .. subject .. " must be a dense array")
  end
  return count
end

local function id_matches_kind(id, kind)
  if kind == "merge" then
    return id == "github.merge:verified-pr"
  end
  if kind == "comment" or kind == "label" then
    return id:match("^" .. kind .. ":issue:[%w%._%-%/]+$") ~= nil
      or id:match("^" .. kind .. ":pr:[%w%._%-%/]+$") ~= nil
  end
  local pattern = id_patterns[kind]
  return pattern ~= nil and id:match(pattern) ~= nil
end

local function validate_record(owner, record)
  if type(record) ~= "table" then
    error("devloop.restart_sinks: sink record must be a table")
  end
  assert_exact_fields(record, record_fields, "sink record")
  if not is_nonempty_string(record.id) then
    error("devloop.restart_sinks: id must be a non-empty string")
  end
  if record.owner ~= owner then
    error("devloop.restart_sinks: sink owner must match extractor owner")
  end
  if type(record.callsite) ~= "table" then
    error("devloop.restart_sinks: callsite must be a table")
  end
  assert_exact_fields(record.callsite, callsite_fields, "callsite")
  if not is_nonempty_string(record.callsite.department) then
    error("devloop.restart_sinks: callsite.department must be a non-empty string")
  end
  if not is_nonempty_string(record.callsite.site) then
    error("devloop.restart_sinks: callsite.site must be a non-empty string")
  end
  if effect_kinds[record.effect_kind] ~= true then
    error("devloop.restart_sinks: unknown effect_kind " .. tostring(record.effect_kind))
  end
  if authority_classes[record.authority_class] ~= true then
    error("devloop.restart_sinks: unknown authority_class " .. tostring(record.authority_class))
  end
  if not is_nonempty_string(record.dedup_marker_family) then
    error("devloop.restart_sinks: dedup_marker_family must be a non-empty string")
  end
  if not id_matches_kind(record.id, record.effect_kind) then
    error("devloop.restart_sinks: id does not match effect_kind")
  end
end

local function copy_record(record)
  return {
    id = record.id,
    owner = record.owner,
    callsite = {
      department = record.callsite.department,
      site = record.callsite.site,
    },
    effect_kind = record.effect_kind,
    authority_class = record.authority_class,
    dedup_marker_family = record.dedup_marker_family,
  }
end

local function record_key(record)
  return table.concat({
    record.owner,
    record.callsite.department,
    record.callsite.site,
    record.id,
  }, "|")
end

local function records_equal(left, right)
  return left.id == right.id
    and left.owner == right.owner
    and left.callsite.department == right.callsite.department
    and left.callsite.site == right.callsite.site
    and left.effect_kind == right.effect_kind
    and left.authority_class == right.authority_class
    and left.dedup_marker_family == right.dedup_marker_family
end

function M.extract(owner, inventory)
  if not is_nonempty_string(owner) then
    error("devloop.restart_sinks: owner must be a non-empty string")
  end
  if type(inventory) ~= "table" then
    error("devloop.restart_sinks: inventory must be a table")
  end

  local count = array_length(inventory, "inventory")
  local extracted = {}
  local seen = {}
  for index = 1, count do
    local record = inventory[index]
    validate_record(owner, record)
    local key = record_key(record)
    if seen[key] then
      error("devloop.restart_sinks: duplicate sink record " .. key)
    end
    seen[key] = true
    table.insert(extracted, copy_record(record))
  end
  return extracted
end

function M.assert_coverage(owner, inventory, observed, options)
  local authored = M.extract(owner, inventory)
  local old_observations = M.extract(owner, observed)
  local authored_by_key = {}
  local observed_by_key = {}

  for _, record in ipairs(authored) do
    authored_by_key[record_key(record)] = record
  end
  for _, record in ipairs(old_observations) do
    local key = record_key(record)
    observed_by_key[key] = record
    local classified = authored_by_key[key]
    if classified == nil then
      error("devloop.restart_sinks: unclassified sink " .. key)
    end
    if not records_equal(classified, record) then
      error("devloop.restart_sinks: sink classification mismatch " .. key)
    end
  end

  if type(options) == "table" and options.symmetric == true then
    for key in pairs(authored_by_key) do
      if observed_by_key[key] == nil then
        error("devloop.restart_sinks: authored sink not observed " .. key)
      end
    end
  end
  return true
end

function M.schema()
  return {
    authority_classes = {
      "grantless-non-lifecycle",
      "grantless-published-intent",
      "grantless-telemetry",
      "lifecycle-authoritative",
    },
    effect_kinds = {
      "adapter",
      "codex",
      "comment",
      "git",
      "label",
      "merge",
      "queue",
    },
  }
end

return M
