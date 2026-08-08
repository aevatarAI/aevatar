local strings = require("contract.strings")

local M = {}

M.MARKER_PREFIX = "[fkst:blocked-github-content:v1"
M.REDACTION_REASON = "non-whitelisted-author"

local known_array_fields = {
  assignees = true,
  check_runs = true,
  comments = true,
  labels = true,
  nodes = true,
  pullRequests = true,
  reviews = true,
  statusCheckRollup = true,
  sub_issues = true,
}

local prose_fields = {
  body = true,
  bodyText = true,
  title = true,
}

local function lua_is_array(value, key_hint)
  local value_type = type(value)
  if value_type ~= "table" then
    return false
  end
  local count = 0
  local saw_key = false
  for key, _ in pairs(value) do
    saw_key = true
    if type(key) ~= "number" or key < 1 or key % 1 ~= 0 then
      return false
    end
    if key > count then
      count = key
    end
  end
  for i = 1, count do
    if value[i] == nil then
      return false
    end
  end
  if not saw_key then
    return known_array_fields[tostring(key_hint or "")] == true
  end
  return true
end

local function lua_json_value(value, key_hint)
  local value_type = type(value)
  if value == nil or value_type == "userdata" then
    return "null"
  end
  if value_type == "string" then
    return strings.json_string(value)
  end
  if value_type == "number" then
    return tostring(value)
  end
  if value_type == "boolean" then
    return value and "true" or "false"
  end
  if value_type ~= "table" then
    error("forge.github.content_filter: cannot encode " .. value_type)
  end
  if lua_is_array(value, key_hint) then
    local parts = {}
    for i = 1, #value do
      parts[#parts + 1] = lua_json_value(value[i])
    end
    return "[" .. table.concat(parts, ",") .. "]"
  end
  local keys = {}
  for key, _ in pairs(value) do
    keys[#keys + 1] = tostring(key)
  end
  table.sort(keys)
  local parts = {}
  for _, key in ipairs(keys) do
    parts[#parts + 1] = strings.json_string(key) .. ":" .. lua_json_value(value[key], key)
  end
  return "{" .. table.concat(parts, ",") .. "}"
end

local function utf8_char(codepoint)
  if codepoint < 0x80 then
    return string.char(codepoint)
  elseif codepoint < 0x800 then
    return string.char(
      0xC0 + math.floor(codepoint / 0x40),
      0x80 + (codepoint % 0x40)
    )
  elseif codepoint < 0x10000 then
    return string.char(
      0xE0 + math.floor(codepoint / 0x1000),
      0x80 + (math.floor(codepoint / 0x40) % 0x40),
      0x80 + (codepoint % 0x40)
    )
  end
  return string.char(
    0xF0 + math.floor(codepoint / 0x40000),
    0x80 + (math.floor(codepoint / 0x1000) % 0x40),
    0x80 + (math.floor(codepoint / 0x40) % 0x40),
    0x80 + (codepoint % 0x40)
  )
end

local Parser = {}
Parser.__index = Parser

function Parser:new(source)
  return setmetatable({ source = tostring(source or ""), index = 1 }, self)
end

function Parser:peek()
  return self.source:sub(self.index, self.index)
end

function Parser:skip_ws()
  while self.index <= #self.source do
    local char = self:peek()
    if char ~= " " and char ~= "\n" and char ~= "\r" and char ~= "\t" then
      return
    end
    self.index = self.index + 1
  end
end

function Parser:expect(text)
  if self.source:sub(self.index, self.index + #text - 1) ~= text then
    error("expected " .. text)
  end
  self.index = self.index + #text
end

function Parser:parse_string()
  self:expect('"')
  local parts = {}
  while self.index <= #self.source do
    local char = self:peek()
    if char == '"' then
      self.index = self.index + 1
      return { kind = "string", value = table.concat(parts) }
    end
    if char == "\\" then
      self.index = self.index + 1
      local esc = self:peek()
      if esc == '"' or esc == "\\" or esc == "/" then
        parts[#parts + 1] = esc
        self.index = self.index + 1
      elseif esc == "b" then
        parts[#parts + 1] = "\b"
        self.index = self.index + 1
      elseif esc == "f" then
        parts[#parts + 1] = "\f"
        self.index = self.index + 1
      elseif esc == "n" then
        parts[#parts + 1] = "\n"
        self.index = self.index + 1
      elseif esc == "r" then
        parts[#parts + 1] = "\r"
        self.index = self.index + 1
      elseif esc == "t" then
        parts[#parts + 1] = "\t"
        self.index = self.index + 1
      elseif esc == "u" then
        self.index = self.index + 1
        local raw = self.source:sub(self.index, self.index + 3)
        if not raw:match("^[0-9A-Fa-f][0-9A-Fa-f][0-9A-Fa-f][0-9A-Fa-f]$") then
          error("invalid unicode escape")
        end
        self.index = self.index + 4
        local codepoint = tonumber(raw, 16)
        if codepoint >= 0xD800 and codepoint <= 0xDBFF and self.source:sub(self.index, self.index + 1) == "\\u" then
          self.index = self.index + 2
          local low_raw = self.source:sub(self.index, self.index + 3)
          if not low_raw:match("^[0-9A-Fa-f][0-9A-Fa-f][0-9A-Fa-f][0-9A-Fa-f]$") then
            error("invalid unicode surrogate escape")
          end
          self.index = self.index + 4
          local low = tonumber(low_raw, 16)
          if low < 0xDC00 or low > 0xDFFF then
            error("invalid unicode surrogate pair")
          end
          codepoint = 0x10000 + ((codepoint - 0xD800) * 0x400) + (low - 0xDC00)
        end
        parts[#parts + 1] = utf8_char(codepoint)
      else
        error("invalid string escape")
      end
    else
      if string.byte(char) < 0x20 then
        error("invalid raw control character in string")
      end
      parts[#parts + 1] = char
      self.index = self.index + 1
    end
  end
  error("unterminated string")
end

function Parser:parse_number()
  local start = self.index
  if self:peek() == "-" then
    self.index = self.index + 1
  end
  local first = self:peek()
  if first == "0" then
    self.index = self.index + 1
  elseif first:match("%d") ~= nil then
    repeat
      self.index = self.index + 1
    until self.index > #self.source or self:peek():match("%d") == nil
  else
    error("invalid number")
  end
  if self:peek() == "." then
    self.index = self.index + 1
    if self:peek():match("%d") == nil then
      error("invalid number fraction")
    end
    repeat
      self.index = self.index + 1
    until self.index > #self.source or self:peek():match("%d") == nil
  end
  local exp = self:peek()
  if exp == "e" or exp == "E" then
    self.index = self.index + 1
    local sign = self:peek()
    if sign == "+" or sign == "-" then
      self.index = self.index + 1
    end
    if self:peek():match("%d") == nil then
      error("invalid number exponent")
    end
    repeat
      self.index = self.index + 1
    until self.index > #self.source or self:peek():match("%d") == nil
  end
  return { kind = "number", raw = self.source:sub(start, self.index - 1) }
end

function Parser:parse_array()
  self:expect("[")
  local items = {}
  self:skip_ws()
  if self:peek() == "]" then
    self.index = self.index + 1
    return { kind = "array", items = items }
  end
  while true do
    items[#items + 1] = self:parse_value()
    self:skip_ws()
    local char = self:peek()
    if char == "]" then
      self.index = self.index + 1
      return { kind = "array", items = items }
    end
    if char ~= "," then
      error("expected array comma")
    end
    self.index = self.index + 1
  end
end

function Parser:parse_object()
  self:expect("{")
  local members = {}
  self:skip_ws()
  if self:peek() == "}" then
    self.index = self.index + 1
    return { kind = "object", members = members }
  end
  while true do
    self:skip_ws()
    local key = self:parse_string()
    self:skip_ws()
    self:expect(":")
    members[#members + 1] = { key = key.value, value = self:parse_value() }
    self:skip_ws()
    local char = self:peek()
    if char == "}" then
      self.index = self.index + 1
      return { kind = "object", members = members }
    end
    if char ~= "," then
      error("expected object comma")
    end
    self.index = self.index + 1
  end
end

function Parser:parse_value()
  self:skip_ws()
  local char = self:peek()
  if char == "{" then
    return self:parse_object()
  end
  if char == "[" then
    return self:parse_array()
  end
  if char == '"' then
    return self:parse_string()
  end
  if char == "t" then
    self:expect("true")
    return { kind = "boolean", value = true }
  end
  if char == "f" then
    self:expect("false")
    return { kind = "boolean", value = false }
  end
  if char == "n" then
    self:expect("null")
    return { kind = "null" }
  end
  return self:parse_number()
end

local function parse_json_document(source)
  local parser = Parser:new(source)
  local root = parser:parse_value()
  parser:skip_ws()
  if parser.index <= #parser.source then
    error("trailing content")
  end
  return root
end

local function object_field(object, name)
  if type(object) ~= "table" or object.kind ~= "object" then
    return nil
  end
  local found_value = nil
  local found_member = nil
  for _, member in ipairs(object.members) do
    if member.key == name then
      found_value = member.value
      found_member = member
    end
  end
  return found_value, found_member
end

local function string_value(node)
  if type(node) == "table" and node.kind == "string" then
    return node.value
  end
  return nil
end

local function author_login(value)
  local author = object_field(value, "author")
  local login = object_field(author, "login")
  if string_value(login) ~= nil then
    return login.value
  end
  local user = object_field(value, "user")
  login = object_field(user, "login")
  if string_value(login) ~= nil then
    return login.value
  end
  return nil
end

function M.canon_login(login)
  if login == nil then
    return nil
  end
  local value = strings.trim(login):lower():gsub("%[bot%]$", "")
  value = strings.trim(value)
  if value == "" then
    return nil
  end
  return value
end

function M.build_whitelist(logins)
  local set = {}
  for _, login in ipairs(logins or {}) do
    local canonical = M.canon_login(login)
    if canonical ~= nil then
      set[canonical] = true
    end
  end
  return set
end

function M.author_policy_from_logins(logins)
  return {
    kind = "forge.github.author_policy.v1",
    whitelist = M.build_whitelist(logins),
  }
end

function M.author_policy_from_whitelist(whitelist)
  return {
    kind = "forge.github.author_policy.v1",
    whitelist = whitelist or {},
  }
end

function M.test_disabled_author_policy()
  return {
    kind = "forge.github.author_policy.v1",
    disabled = true,
    whitelist = {},
  }
end

function M.policy_whitelist(policy)
  if type(policy) == "function" then
    policy = policy()
  end
  if type(policy) ~= "table" or policy.kind ~= "forge.github.author_policy.v1" or type(policy.whitelist) ~= "table" then
    error("forge.github.content_filter: missing trusted author policy")
  end
  if policy.disabled == true then
    return nil
  end
  return policy.whitelist
end

function M.is_authorized(author_login_value, whitelist)
  local canonical = M.canon_login(author_login_value)
  if canonical == nil then
    return false
  end
  return type(whitelist) == "table" and whitelist[canonical] == true
end

function M.redaction_marker(field, author_login_value, bytes_removed)
  local login = M.canon_login(author_login_value) or "unknown"
  return M.MARKER_PREFIX
    .. ' field="' .. tostring(field) .. '"'
    .. ' existed="true"'
    .. ' author_login="' .. login .. '"'
    .. ' why="' .. M.REDACTION_REASON .. '"]'
end

local function is_blocked_marker(value)
  return type(value) == "string" and value:find(M.MARKER_PREFIX, 1, true) == 1
end

function M.filter_cell(body, author_login_value, field, whitelist)
  if M.is_authorized(author_login_value, whitelist) then
    return body, nil
  end
  local bytes_removed = (type(body) == "userdata" or body == nil) and 0 or #tostring(body)
  local marker = M.redaction_marker(field, author_login_value)
  return marker, {
    field = field,
    author_login = M.canon_login(author_login_value) or "unknown",
    bytes_removed = bytes_removed,
    reason = M.REDACTION_REASON,
  }
end

local function record_redaction(records, record)
  if record ~= nil and type(records) == "table" then
    records[#records + 1] = record
  end
end

local function redact_authored_object(value, whitelist, records)
  local login = author_login(value)
  for _, member in ipairs(value.members) do
    if prose_fields[member.key] and member.value.kind == "string" then
      local filtered, record = M.filter_cell(member.value.value, login, member.key, whitelist)
      member.value = { kind = "string", value = filtered }
      record_redaction(records, record)
    end
  end
end

local function walk(value, whitelist, records)
  if type(value) ~= "table" then
    return
  end
  if value.kind == "object" then
    redact_authored_object(value, whitelist, records)
    for _, member in ipairs(value.members) do
      walk(member.value, whitelist, records)
    end
  elseif value.kind == "array" then
    for _, child in ipairs(value.items) do
      walk(child, whitelist, records)
    end
  end
end

local function node_json_value(node)
  if node.kind == "null" then
    return "null"
  end
  if node.kind == "boolean" then
    return node.value and "true" or "false"
  end
  if node.kind == "number" then
    return node.raw
  end
  if node.kind == "string" then
    return strings.json_string(node.value)
  end
  if node.kind == "array" then
    local parts = {}
    for _, item in ipairs(node.items) do
      parts[#parts + 1] = node_json_value(item)
    end
    return "[" .. table.concat(parts, ",") .. "]"
  end
  if node.kind == "object" then
    local parts = {}
    for _, member in ipairs(node.members) do
      parts[#parts + 1] = strings.json_string(member.key) .. ":" .. node_json_value(member.value)
    end
    return "{" .. table.concat(parts, ",") .. "}"
  end
  error("forge.github.content_filter: cannot encode JSON node")
end

function M.filter_gh_content_json(json_string, kind_or_whitelist, whitelist_or_records, maybe_records)
  local whitelist = whitelist_or_records
  local records = maybe_records
  if type(kind_or_whitelist) == "table" then
    whitelist = kind_or_whitelist
    records = whitelist_or_records
  elseif type(kind_or_whitelist) == "string" and kind_or_whitelist ~= "" then
    local kind = kind_or_whitelist
    if kind ~= "issue" and kind ~= "pr" and kind ~= "content" then
      error("forge.github.content_filter: invalid content kind")
    end
  end

  local ok, decoded = pcall(parse_json_document, json_string or "")
  if not ok or type(decoded) ~= "table" then
    error("forge.github.content_filter: JSON decode failed")
  end
  local found = {}
  walk(decoded, whitelist, found)
  if #found == 0 then
    return json_string
  end
  if type(records) == "table" then
    for _, record in ipairs(found) do
      records[#records + 1] = record
    end
  end
  return node_json_value(decoded) .. "\n"
end

local function split_included_headers(stdout)
  local text = tostring(stdout or "")
  if text:find("HTTP/", 1, true) ~= 1 then
    return "", text
  end
  local start_pos, end_pos = text:find("\r\n\r\n", 1, true)
  if start_pos == nil then
    start_pos, end_pos = text:find("\n\n", 1, true)
  end
  if start_pos == nil then
    error("forge.github.content_filter: included HTTP response is missing a JSON body separator")
  end
  return text:sub(1, end_pos), text:sub(end_pos + 1)
end

function M.apply_gh_content_filter(result, context, policy, author_policy, stdout_policy)
  stdout_policy.validate(policy)
  if type(result) ~= "table" or tonumber(result.exit_code) ~= 0 then
    return result
  end
  if not stdout_policy.is_content_json(policy) then
    return result
  end
  local whitelist = M.policy_whitelist(author_policy)
  if whitelist == nil then
    return result
  end
  local headers, body = split_included_headers(type(result) == "table" and result.stdout or "")
  local records = {}
  local filtered_body = M.filter_gh_content_json(body, whitelist, records)
  if filtered_body == body then
    return result
  end
  local copy = {}
  for key, value in pairs(result or {}) do
    copy[key] = value
  end
  copy.stdout = headers .. filtered_body
  copy.content_redacted = true
  copy.content_redaction_records = records
  copy.stdout_policy = policy
  copy.context = context
  return copy
end

M._json_value = lua_json_value
M._parse_json_document = parse_json_document
M._is_blocked_marker = is_blocked_marker

return M
