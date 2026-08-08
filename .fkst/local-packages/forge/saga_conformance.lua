-- forge.saga_conformance: GitHub/Git write-class classifier for saga tests.

local F = {}

local github_write_prefixes = {
  { "issue", "comment" },
  { "issue", "edit" },
  { "issue", "close" },
  { "issue", "create" },
  { "issue", "reopen" },
  { "pr", "merge" },
  { "pr", "comment" },
  { "pr", "edit" },
  { "pr", "create" },
  { "pr", "close" },
  { "pr", "ready" },
  { "pr", "reopen" },
  { "label", "add" },
  { "label", "remove" },
  { "label", "create" },
  { "workflow", "run" },
}

local github_write_flags = {
  ["--add-label"] = true,
  ["--remove-label"] = true,
}

local github_write_methods = {
  POST = true,
  PATCH = true,
  PUT = true,
  DELETE = true,
}

local option_arg_flags = {
  ["-C"] = true,
  ["-c"] = true,
  ["-R"] = true,
  ["--cwd"] = true,
  ["--git-dir"] = true,
  ["--hostname"] = true,
  ["--namespace"] = true,
  ["--repo"] = true,
  ["--work-tree"] = true,
}

local function starts_with(value, prefix)
  return value:sub(1, #prefix) == prefix
end

local function basename(program)
  return tostring(program or ""):match("([^/]+)$") or tostring(program or "")
end

local function is_github_program(program)
  return basename(program):lower():match("^g[h]$") ~= nil
end

local function is_git_program(program)
  return basename(program):lower():match("^g[i]t$") ~= nil
end

local function table_len(value)
  return type(value) == "table" and #value or 0
end

local function copy_argv(program, args)
  local argv = { basename(program) }
  for index = 1, table_len(args) do
    table.insert(argv, tostring(args[index]))
  end
  return argv
end

local function trim_shell_token(token)
  local text = tostring(token or "")
  local trimmed = text:gsub("^['\"]", ""):gsub("['\"]$", "")
  return trimmed
end

local function argv_from_string(command)
  local argv = {}
  for token in tostring(command or ""):gmatch("%S+") do
    table.insert(argv, trim_shell_token(token))
  end
  return argv
end

local function argv_from_evidence(evidence)
  if type(evidence) == "table" then
    if type(evidence.argv) == "table" then
      local argv = {}
      for index = 1, #evidence.argv do
        table.insert(argv, tostring(evidence.argv[index]))
      end
      return argv, tostring(evidence.stdin or "")
    end
    if evidence.program ~= nil then
      local program = basename(evidence.program)
      if program == "sh" or program == "bash" then
        local args = evidence.args or {}
        if args[1] == "-c" then
          return argv_from_string(args[2]), tostring(evidence.stdin or "")
        end
      end
      return copy_argv(evidence.program, evidence.args), tostring(evidence.stdin or "")
    end
    if evidence.rendered ~= nil or evidence.cmd ~= nil or evidence.command ~= nil then
      local command = tostring(evidence.rendered or evidence.cmd or evidence.command or "")
      return argv_from_string(command), command .. "\n" .. tostring(evidence.stdin or "")
    end
  end
  local command = tostring(evidence or "")
  return argv_from_string(command), command
end

local function skip_leading_options(argv, index)
  while index <= #argv and starts_with(tostring(argv[index]), "-") do
    local option = tostring(argv[index])
    index = index + 1
    if option_arg_flags[option] and index <= #argv then
      index = index + 1
    end
  end
  return index
end

local function argv_has_flag(argv, flag)
  for _, value in ipairs(argv) do
    if value == flag then
      return true
    end
  end
  return false
end

local function github_has_write_method(argv)
  if argv[2] ~= "api" then
    return false
  end
  for index = 3, #argv do
    local value = tostring(argv[index])
    local method = value:match("^%-%-method=(.+)$")
    if value == "--method" and argv[index + 1] ~= nil then
      method = argv[index + 1]
    end
    if method ~= nil and github_write_methods[tostring(method):upper()] then
      return true
    end
  end
  return false
end

local function github_is_graphql_mutation(argv, text)
  return argv[2] == "api"
    and argv[3] == "graphql"
    and tostring(text or ""):find("mutation") ~= nil
end

local function is_github_write(argv, text)
  if not is_github_program(argv[1]) then
    return false
  end
  for _, prefix in ipairs(github_write_prefixes) do
    if argv[2] == prefix[1] and argv[3] == prefix[2] then
      return true
    end
  end
  for flag, _ in pairs(github_write_flags) do
    if argv_has_flag(argv, flag) then
      return true
    end
  end
  return github_has_write_method(argv) or github_is_graphql_mutation(argv, text)
end

local function is_git_push(argv)
  if not is_git_program(argv[1]) then
    return false
  end
  local command_index = skip_leading_options(argv, 2)
  return argv[command_index] == "push"
end

function F.is_write_class(evidence)
  local argv, text = argv_from_evidence(evidence)
  if is_git_push(argv) then
    return true
  end
  return is_github_write(argv, text)
end

return F
