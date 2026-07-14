local M = {}

local function stderr_of(result)
  return type(result) == "table" and tostring(result.stderr or "") or ""
end

function M.is_rate_limited(result)
  local stderr = stderr_of(result):lower()
  for _, needle in ipairs({
    -- Broad "api rate limit" covers both "API rate limit exceeded" and the
    -- dominant "API rate limit already exceeded for user ID <n>" wording, where
    -- the interposed "already" defeats a contiguous "api rate limit exceeded".
    "api rate limit",
    "secondary rate limit",
    "was submitted too quickly",
    "http 429",
    "status 429",
    "429 too many requests",
    "too many requests",
  }) do
    if stderr:find(needle, 1, true) then
      return true
    end
  end
  if stderr:find("abuse", 1, true) and stderr:find("rate", 1, true) then
    return true
  end
  return false
end

function M.is_issue_assign_permission_denied(result, context)
  return tostring(context or "") == "gh issue assign"
    and stderr_of(result):lower():find("permission%-denied", 1, false) ~= nil
end

function M.error_class(result, context)
  if M.is_rate_limited(result) then
    return "gh-rate-limited"
  end
  if M.is_issue_assign_permission_denied(result, context) then
    return "gh-issue-assign-permission-denied"
  end
  return "gh-command-failed"
end

local function misuse_error(argv, context)
  local bad_program
  if type(argv) == "table" then
    bad_program = argv[1]
  end
  local message = "forge.github: " .. tostring(context) .. " adapter misuse: expected gh argv, got "
    .. tostring(bad_program)
  error(setmetatable({
    class = "gh-adapter-misuse",
    expected_program = "gh",
    bad_program = bad_program,
    message = message,
  }, {
    __tostring = function(err)
      return err.message
    end,
  }))
end

function M.run(exec, argv, timeout, context)
  if type(argv) ~= "table" or #argv < 1 or argv[1] ~= "gh" then
    misuse_error(argv, context)
  end
  local result = exec({ argv = argv, timeout = timeout })
  if type(result) ~= "table" or tonumber(result.exit_code) ~= 0 then
    local class = M.error_class(result, context)
    local message = "forge.github: " .. tostring(context) .. " failed: " .. class .. ": " .. stderr_of(result)
    error(setmetatable({
      class = class,
      retryable = class == "gh-rate-limited",
      permanent = class == "gh-issue-assign-permission-denied",
      result = result,
      context = context,
      message = message,
    }, {
      __tostring = function(err)
        return err.message
      end,
    }))
  end
  return result
end

return M
