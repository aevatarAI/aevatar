local S = {}

function S.install(M)
local function command_result_exit_code(result)
  if type(result) ~= "table" then
    return nil
  end
  return tonumber(result.exit_code)
end

local github_handle = nil
local git_handle = nil

local function production_github()
  if github_handle == nil then
    if type(exec_argv) ~= "function" then
      error("github-proxy: gh adapter requires exec_argv")
    end
    github_handle = require("forge.github").new(exec_argv)
  end
  return github_handle
end

local function production_git()
  if git_handle == nil then
    if type(exec_argv) ~= "function" then
      error("github-proxy: git adapter requires exec_argv")
    end
    git_handle = require("forge.git").new(exec_argv)
  end
  return git_handle
end

local function shell_words(command)
  local words = {}
  local current = {}
  local quote = nil
  local index = 1
  local text = tostring(command or "")
  while index <= #text do
    local char = text:sub(index, index)
    if quote == "'" then
      if char == "'" then
        quote = nil
      else
        table.insert(current, char)
      end
    elseif quote == '"' then
      if char == '"' then
        quote = nil
      elseif char == "\\" then
        index = index + 1
        if index <= #text then
          table.insert(current, text:sub(index, index))
        end
      else
        table.insert(current, char)
      end
    elseif char == "'" or char == '"' then
      quote = char
    elseif char == "\\" then
      index = index + 1
      if index <= #text then
        table.insert(current, text:sub(index, index))
      end
    elseif char:match("%s") ~= nil then
      if #current > 0 then
        table.insert(words, table.concat(current))
        current = {}
      end
    else
      table.insert(current, char)
    end
    index = index + 1
  end
  if quote ~= nil then
    error("github-proxy: unterminated command quote")
  end
  if #current > 0 then
    table.insert(words, table.concat(current))
  end
  return words
end

function M.github()
  return production_github()
end

function M.git()
  return production_git()
end

function M.gh_adapter_result(fn, context)
  local ok, result_or_error = pcall(fn, production_github())
  if ok then
    if command_result_exit_code(result_or_error) ~= 0 then
      return false, M.gh_error(context or "github command", result_or_error)
    end
    return true, result_or_error
  end
  if type(result_or_error) == "table" and result_or_error.class ~= nil then
    local raw_result = result_or_error.result
    local normalized = M.gh_error(context or result_or_error.context or "github command", raw_result or {})
    normalized.class = result_or_error.class
    normalized.retryable = result_or_error.retryable == true
    normalized.result = raw_result
    normalized.cause = result_or_error
    return false, normalized
  end
  error(result_or_error)
end

function M.gh_exec_result(run_or_result, timeout, context)
  if type(run_or_result) == "function" then
    return M.gh_adapter_result(function()
      return run_or_result(timeout or 30)
    end, context)
  end
  if type(run_or_result) == "string" then
    local argv = shell_words(run_or_result)
    return M.gh_adapter_result(function(github)
      return github._exec(argv, timeout or 30, context or "github command")
    end, context)
  end
  local result = run_or_result
  if command_result_exit_code(result) ~= 0 then
    return false, M.gh_error(context or "github command", result)
  end
  return true, result
end

function M.gh_exec(run_or_result, timeout, context)
  local ok, result_or_error = M.gh_exec_result(run_or_result, timeout, context)
  if not ok then
    error(result_or_error.message)
  end
  return result_or_error
end
end

return S
