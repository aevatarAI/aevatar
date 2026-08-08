-- forge.ports: production port wiring shared by every department on the gh/git
-- Ports & Adapters layer. A department defines make_department(ports) that
-- closes over the injected handles and returns its engine-facing table
-- ({ spec, pipeline }); forge.ports.install builds the production handles from the
-- host exec_argv primitive, constructs the department, and exposes
-- make_department so fake-port tests can re-build the same department against
-- forge.github_fake / forge.git_fake. This removes the per-department
-- production_exec_argv / production_ports copy (DRY: the framework owns the stable
-- common wiring, the script keeps only its business pipeline).
local M = {}

function M.github_author_options(read_env, owner, opts)
  assert(type(read_env) == "function", "forge.ports.github_author_options requires read_env")
  local options = opts or {}
  local bot_login_env = options.bot_login_env
  if type(bot_login_env) ~= "string" or bot_login_env == "" then
    error("forge.ports.github_author_options requires opts.bot_login_env", 2)
  end
  local extra_login_envs = options.extra_login_envs or {}
  local content_filter = require("forge.github.content_filter")
  local strings = require("contract.strings")
  local policy = nil
  local function append_csv_logins(logins, raw)
    for login in tostring(raw or ""):gmatch("[^,%s]+") do
      table.insert(logins, login)
    end
  end
  return {
    trusted_author_policy = function()
      if policy == nil then
        local bot_login = strings.trim(read_env(bot_login_env) or "")
        if bot_login == "" then
          error(tostring(owner or "forge.ports") .. ": missing-github-bot-login: " .. bot_login_env .. " is required for authored GitHub reads")
        end
        local logins = { bot_login }
        for _, env_name in ipairs(extra_login_envs) do
          append_csv_logins(logins, read_env(env_name))
        end
        policy = content_filter.author_policy_from_logins(logins)
      end
      return policy
    end,
  }
end

local function production_exec_argv()
  if type(exec_argv) == "function" then
    return exec_argv
  end
  return function()
    error("forge.ports: production ports require exec_argv")
  end
end

local function github_from_options(run, github_options)
  if github_options.trusted_author_policy == nil then
    return setmetatable({}, {
      __index = function()
        error("forge.ports: trusted_author_policy is required for production GitHub reads")
      end,
    })
  end
  return require("forge.github").new(run, {
    trusted_author_policy = github_options.trusted_author_policy,
  })
end

function M.production_handles(opts)
  local run = production_exec_argv()
  local github_options = opts or {}
  return {
    github = github_from_options(run, github_options),
    git = require("forge.git").new(run),
  }
end

local function validate_department(department)
  if type(department) ~= "table" or type(department.spec) ~= "table" or type(department.pipeline) ~= "function" then
    error("forge.ports.install: make_department must return a table with spec and pipeline", 2)
  end
end

local function make_with_pipeline_restore(make_department, handles)
  local previous_pipeline = _G.pipeline
  local ok, department_or_err = pcall(make_department, handles)
  if not ok then
    _G.pipeline = previous_pipeline
    error(department_or_err, 0)
  end
  local captured_pipeline = _G.pipeline
  if type(department_or_err) == "table" and type(captured_pipeline) == "function" and captured_pipeline ~= previous_pipeline then
    department_or_err.pipeline = captured_pipeline
  end
  local ok_validate, validate_err = pcall(validate_department, department_or_err)
  if not ok_validate then
    _G.pipeline = previous_pipeline
    error(validate_err, 0)
  end
  _G.pipeline = previous_pipeline
  return department_or_err
end

function M.install(make_department, opts)
  assert(type(make_department) == "function", "forge.ports.install requires a make_department function")
  local department = make_with_pipeline_restore(make_department, M.production_handles(opts))
  _G.pipeline = department.pipeline
  department.make_department = function(handles)
    return make_with_pipeline_restore(make_department, handles)
  end
  return department
end

return M
