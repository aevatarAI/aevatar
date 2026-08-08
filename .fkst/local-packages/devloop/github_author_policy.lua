local devloop_base = require("devloop.base")
local strings = require("contract.strings")
local content_filter = require("forge.github.content_filter")

local M = {}

local function append_csv_logins(logins, raw)
  for login in tostring(raw or ""):gmatch("[^,%s]+") do
    table.insert(logins, login)
  end
end

function M.from_logins(logins)
  return content_filter.author_policy_from_logins(logins or {})
end

function M.from_env(exec)
  local bot_login = nil
  if type(devloop_base.configured_trusted_bot_login) == "function" then
    bot_login = devloop_base.configured_trusted_bot_login()
  end
  if bot_login == nil or tostring(bot_login or "") == "" then
    local ok_bot = true
    ok_bot, bot_login = pcall(devloop_base.read_env, "FKST_GITHUB_BOT_LOGIN", exec)
    bot_login = ok_bot and strings.trim(bot_login or "") or ""
  end
  if bot_login == "" then
    error("devloop.github_author_policy: FKST_GITHUB_BOT_LOGIN is required for authored GitHub reads")
  end
  local logins = { bot_login }
  for _, name in ipairs({ "FKST_DEVLOOP_MANAGED_BOT_LOGINS", "FKST_GITHUB_AUTHORIZED_LOGINS" }) do
    local ok, raw = pcall(devloop_base.read_env, name, exec)
    if ok then
      append_csv_logins(logins, raw)
    end
  end
  return M.from_logins(logins)
end

function M.is_authorized(policy, login)
  return content_filter.is_authorized(login, content_filter.policy_whitelist(policy))
end

function M.for_exec(exec)
  return M.from_env(exec)
end

function M.github_options(exec)
  local policy = nil
  return {
    trusted_author_policy = function()
      if policy == nil then
        policy = M.for_exec(exec)
      end
      return policy
    end,
  }
end

return M
