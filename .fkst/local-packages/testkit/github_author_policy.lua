local M = {}
local content_filter = require("forge.github.content_filter")

local defaults = {
  bot_login = "fkst-test-bot",
  managed_bot_logins = "fkst-test-bot,ElonSG",
  authorized_logins = "trusted-human",
}

local function run_env(run_opts)
  if type(run_opts) == "table" and type(run_opts.env) == "table" then
    return run_opts.env
  end
  return {}
end

local function value_or_default(env, key, default)
  if env[key] ~= nil then
    return tostring(env[key])
  end
  return default
end

function M.values(run_opts)
  local env = run_env(run_opts)
  return {
    bot_login = value_or_default(env, "FKST_GITHUB_BOT_LOGIN", defaults.bot_login),
    managed_bot_logins = value_or_default(env, "FKST_DEVLOOP_MANAGED_BOT_LOGINS", defaults.managed_bot_logins),
    authorized_logins = value_or_default(env, "FKST_GITHUB_AUTHORIZED_LOGINS", defaults.authorized_logins),
  }
end

local function append_csv(logins, raw)
  for login in tostring(raw or ""):gmatch("[^,%s]+") do
    table.insert(logins, login)
  end
end

function M.policy(run_opts)
  local values = M.values(run_opts)
  local logins = { values.bot_login }
  append_csv(logins, values.managed_bot_logins)
  append_csv(logins, values.authorized_logins)
  return content_filter.author_policy_from_logins(logins)
end

function M.github_options(run_opts)
  local policy = nil
  return {
    trusted_author_policy = function()
      if policy == nil then
        policy = M.policy(run_opts)
      end
      return policy
    end,
  }
end

local function mock_read(t, name, value)
  t.mock_command('printf %s "$' .. name .. '"', {
    stdout = value,
    stderr = "",
    exit_code = 0,
  })
end

local function configure_trusted_login(configure, login)
  if type(configure) == "function" then
    configure(login)
  elseif type(configure) == "table" and type(configure.configure_trusted_bot_login) == "function" then
    configure.configure_trusted_bot_login(login)
  end
end

function M.mock_env(t, run_opts, opts)
  opts = opts or {}
  local values = M.values(run_opts)
  configure_trusted_login(opts.configure_trusted_bot_login or opts.configure, values.bot_login)
  local times = tonumber(opts.times) or 1
  for _ = 1, times do
    mock_read(t, "FKST_GITHUB_BOT_LOGIN", values.bot_login)
    mock_read(t, "FKST_DEVLOOP_MANAGED_BOT_LOGINS", values.managed_bot_logins)
    mock_read(t, "FKST_GITHUB_AUTHORIZED_LOGINS", values.authorized_logins)
  end
  return values
end

return M
