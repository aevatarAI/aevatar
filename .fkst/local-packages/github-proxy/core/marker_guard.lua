local S = {}
local source_ref = require("contract.source_ref")

function S.install(M)
  local function safe_token(value)
    return type(value) == "string" and value:find("^[%w._-]+$") ~= nil
  end

  local function safe_attr_value(value)
    return type(value) == "string" and value ~= "" and value:find('"', 1, true) == nil
  end

  local function pattern_escape(value)
    return tostring(value):gsub("([^%w])", "%%%1")
  end

  local function marker_pattern(namespace, marker, version)
    return "<!%-%- fkst:" .. pattern_escape(namespace)
      .. ":" .. pattern_escape(marker)
      .. ":" .. pattern_escape(version)
      .. ".-%-%->"
  end

  local function marker_attrs(marker)
    local attrs = {}
    for key, value in tostring(marker or ""):gmatch('([%w._-]+)="([^"]*)"') do
      attrs[key] = value
    end
    return attrs
  end

  local function attrs_match(attrs, expected)
    for key, value in pairs(expected or {}) do
      if tostring(attrs[key] or "") ~= tostring(value) then
        return false
      end
    end
    return true
  end

  local function valid_attr_table(value)
    if type(value) ~= "table" then
      return false
    end
    for key, attr_value in pairs(value) do
      if not safe_token(tostring(key)) or not safe_attr_value(tostring(attr_value)) then
        return false
      end
    end
    return true
  end

  local function normalize_order_by(value)
    if value == nil then
      return {}
    end
    if type(value) ~= "table" then
      return nil
    end
    local order = {}
    for _, key in ipairs(value) do
      if not safe_token(tostring(key)) then
        return nil
      end
      table.insert(order, tostring(key))
    end
    return order
  end

  local function attr_order_value(attrs, key)
    if key == "version_order_key" then
      return source_ref.version_order_key(attrs.version)
    end
    return attrs[key]
  end

  local function compare_token(left, right)
    local left_missing = left == nil or tostring(left) == ""
    local right_missing = right == nil or tostring(right) == ""
    if left_missing ~= right_missing then
      return left_missing and -1 or 1
    end
    local left_number = tonumber(left)
    local right_number = tonumber(right)
    if left_number ~= nil and right_number ~= nil and left_number ~= right_number then
      return left_number > right_number and 1 or -1
    end
    local left_text = tostring(left or "")
    local right_text = tostring(right or "")
    if left_text == right_text then
      return 0
    end
    return left_text > right_text and 1 or -1
  end

  local function compare_attrs(left, right, order_by)
    for _, key in ipairs(order_by or {}) do
      local cmp = compare_token(attr_order_value(left, key), attr_order_value(right, key))
      if cmp ~= 0 then
        return cmp
      end
    end
    return 0
  end

  function M.normalize_marker_guard(guard)
    if guard == nil then
      return nil, nil
    end
    if type(guard) ~= "table" then
      return nil, "invalid-marker-guard"
    end
    local namespace = tostring(guard.namespace or "")
    local marker = tostring(guard.marker or "")
    local version = tostring(guard.version or "")
    if not safe_token(namespace) or not safe_token(marker) or not safe_token(version) then
      return nil, "invalid-marker-guard"
    end
    if not valid_attr_table(guard.match) or not valid_attr_table(guard.expected) then
      return nil, "invalid-marker-guard"
    end
    local order_by = normalize_order_by(guard.order_by)
    if order_by == nil then
      return nil, "invalid-marker-guard"
    end
    return {
      namespace = namespace,
      marker = marker,
      version = version,
      match = guard.match,
      expected = guard.expected,
      order_by = order_by,
    }, nil
  end

  function M.marker_guard_current(comments, guard, bot_login)
    local normalized, reason = M.normalize_marker_guard(guard)
    if normalized == nil then
      return false, reason
    end
    local current = nil
    local pattern = marker_pattern(normalized.namespace, normalized.marker, normalized.version)
    for _, comment in ipairs(comments or {}) do
      if M._comment_author_login(comment) == bot_login then
        for marker in M._comment_body(comment):gmatch(pattern) do
          local attrs = marker_attrs(marker)
          if attrs_match(attrs, normalized.match)
            and (current == nil or compare_attrs(attrs, current, normalized.order_by) > 0) then
            current = attrs
          end
        end
      end
    end
    if current == nil then
      return false, "marker-guard-missing"
    end
    if not attrs_match(current, normalized.expected) then
      return false, "marker-guard-superseded"
    end
    return true, nil
  end

  function M.fetch_marker_guard_comments(repo, kind, number)
    local view = M.gh_exec(function(timeout)
      if kind == "pr" then
        return M.github_issue_comments_api(repo, number, timeout)
      end
      return M.github_issue_comments_api(repo, number, timeout)
    end, 30, "GitHub marker guard comments")
    return M.parse_issue_comments(view.stdout)
  end
end

return S
