local base_ids = require("devloop.base_ids")
local S = {}
local strings = require("contract.strings")
local error_facts = require("contract.error_facts")
local decimal_checksum = strings.decimal_checksum

function S.install(M)
local max_error_class_len = 80

local function collapse_dash(value)
  return tostring(value or "")
    :gsub("[^%w%-]+", "-")
    :gsub("_", "-")
    :gsub("%-+", "-")
    :gsub("^%-+", "")
    :gsub("%-+$", "")
end

local function bounded_string(value, limit)
  return type(value) == "string" and value ~= "" and #value <= limit
end

local function normalize_error_class(value)
  local class = collapse_dash(tostring(value or ""):lower())
  if class == "" then
    return "unknown-error"
  end
  if #class > max_error_class_len then
    class = class:sub(1, max_error_class_len):gsub("%-+$", "")
  end
  if class == "" then
    return "unknown-error"
  end
  return class
end

local function wrapped_error_class(message)
  local lower = tostring(message or ""):lower()
  if lower:find("stale_generation_context", 1, true) ~= nil
    or lower:find("context bundle manifest cache miss", 1, true) ~= nil
    or lower:find("context bundle manifest files are unreadable", 1, true) ~= nil
    or lower:find("runtime context cache miss", 1, true) ~= nil
    or lower:find("runtime context manifest file is unreadable", 1, true) ~= nil then
    return "stale-generation-context"
  end
  if lower:find("codex failed", 1, true) ~= nil
    or lower:find("spawn_codex", 1, true) ~= nil
    or lower:find("codex exec", 1, true) ~= nil then
    return "codex-failed"
  end
  if lower:find("rate limit", 1, true) ~= nil
    or lower:find("too many requests", 1, true) ~= nil
    or lower:find("secondary rate limit", 1, true) ~= nil then
    return "gh-rate-limited"
  end
  if lower:find("github command", 1, true) ~= nil and lower:find(" failed", 1, true) ~= nil then
    return "gh-command-failed"
  end
  if lower:find("version-control command", 1, true) ~= nil and lower:find(" failed", 1, true) ~= nil then
    return "git-command-failed"
  end
  if (lower:find("json", 1, true) ~= nil or lower:find("parse", 1, true) ~= nil)
    and lower:find(" failed", 1, true) ~= nil then
    return "parse-failed"
  end
  if lower:find("timed out", 1, true) ~= nil or lower:find("timeout", 1, true) ~= nil then
    return "timeout"
  end
  return nil
end

local function normalize_fingerprint_text(value)
  local text = error_facts.one_line(value):lower()
  text = text:gsub("%d%d%d%d%-%d%d%-%d%dt%d%d[:%-]%d%d[:%-]%d%d%.?%d*z?", "<timestamp>")
  text = text:gsub("%d%d%d%d%-%d%d%-%d%d %d%d:%d%d:%d%d%.?%d*", "<timestamp>")
  text = text:gsub("/private/tmp/[^%s'\"%)%]]+", "<tmp-path>")
  text = text:gsub("/tmp/[^%s'\"%)%]]+", "<tmp-path>")
  text = text:gsub("/var/folders/[^%s'\"%)%]]+", "<tmp-path>")
  text = text:gsub("%f[%x](%x%x%x%x%x%x%x%x%-%x%x%x%x%-%x%x%x%x%-%x%x%x%x%-%x%x%x%x%x%x%x%x%x%x%x%x)%f[^%x]", "<uuid>")
  text = text:gsub("([^%x])(%x%x%x%x%x%x%x+)([^%x])", function(before, hex, after)
    if #hex <= 64 then
      return before .. "<sha>" .. after
    end
    return before .. hex .. after
  end)
  text = text:gsub("^(%x%x%x%x%x%x%x+)([^%x])", function(hex, after)
    if #hex <= 64 then
      return "<sha>" .. after
    end
    return hex .. after
  end)
  text = text:gsub("([^%x])(%x%x%x%x%x%x%x+)$", function(before, hex)
    if #hex <= 64 then
      return before .. "<sha>"
    end
    return before .. hex
  end)
  local normalized = text:gsub("%s+", " "):gsub("^%s+", ""):gsub("%s+$", "")
  return normalized
end

local function normalized_context(value)
  if type(value) == "table" then
    local parts = {}
    for key, field in pairs(value) do
      table.insert(parts, tostring(key) .. "=" .. tostring(field))
    end
    table.sort(parts)
    return table.concat(parts, " ")
  end
  return tostring(value or "")
end

local function normalize_queue(queue)
  if not bounded_string(queue, M._max_key_len) then
    error("github-devloop: error-fact-queue-missing: error fact queue is required")
  end
  return queue
end

local function normalize_attempt(attempt)
  local number = tonumber(attempt)
  if number == nil or number < 1 or number % 1 ~= 0 then
    error("github-devloop: error-fact-attempt-invalid: invalid error fact attempt")
  end
  return number
end

function M.error_fact_class(value)
  if type(value) == "table" then
    if value.error_class ~= nil then
      return normalize_error_class(value.error_class)
    end
    if value.class ~= nil then
      return normalize_error_class(value.class)
    end
    if value.message ~= nil then
      local wrapped = wrapped_error_class(value.message)
      if wrapped ~= nil then
        return wrapped
      end
      return "unknown-error"
    end
  end

  local wrapped = wrapped_error_class(value)
  if wrapped ~= nil then
    return wrapped
  end
  return "unknown-error"
end

function M.error_fact_source_ref_digest(source_ref)
  local normalized = base_ids.normalize_source_ref(source_ref)
  return normalized.kind .. ":" .. normalized.ref
end

function M.error_fact_fingerprint(fields)
  if type(fields) ~= "table" then
    error("github-devloop: error-fact-fields-missing: error fact fingerprint fields are required")
  end
  local queue = normalize_queue(fields.queue)
  local error_class
  if fields.error_class ~= nil or fields.class ~= nil then
    error_class = M.error_fact_class({ error_class = fields.error_class or fields.class })
  else
    error_class = M.error_fact_class({ message = fields.message or fields.error })
  end
  local material = table.concat({
    "queue=" .. normalize_fingerprint_text(queue),
    "error_class=" .. normalize_fingerprint_text(error_class),
    "message=" .. normalize_fingerprint_text(fields.message or fields.error or ""),
    "context=" .. normalize_fingerprint_text(normalized_context(fields.context)),
  }, "\n")
  return "efp-" .. decimal_checksum(material)
end

function M.build_error_fact(opts)
  if type(opts) ~= "table" then
    error("github-devloop: error-fact-options-missing: error fact options are required")
  end
  local queue = normalize_queue(opts.queue)
  local error_class
  if opts.error_class ~= nil or opts.class ~= nil then
    error_class = M.error_fact_class({ error_class = opts.error_class or opts.class })
  else
    error_class = M.error_fact_class({ message = opts.message or opts.error })
  end
  local fact = {
    schema = "github-devloop.error-fact.v1",
    queue = queue,
    error_class = error_class,
    fingerprint = M.error_fact_fingerprint({
      queue = queue,
      error_class = error_class,
      message = opts.message or opts.error or "",
      context = opts.context,
    }),
  }

  if opts.source_ref ~= nil then
    fact.source_ref = base_ids.normalize_source_ref(opts.source_ref)
  end
  if opts.attempt ~= nil then
    fact.attempt = normalize_attempt(opts.attempt)
  end
  if opts.terminal ~= nil then
    if type(opts.terminal) ~= "boolean" then
      error("github-devloop: error-fact-terminal-invalid: invalid error fact terminal")
    end
    fact.terminal = opts.terminal
  end

  return fact
end

M._normalize_error_fact_text = normalize_fingerprint_text

end

return S
