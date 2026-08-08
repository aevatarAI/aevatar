local strings = require("contract.strings")
local forge_strings = require("forge.strings")

local M = {}

local function valid_repo_segment(value)
  return type(value) == "string"
    and value ~= ""
    and value ~= "."
    and value ~= ".."
    and value:find("^[%w%._%-]+$") ~= nil
end

local function valid_bot_login(value)
  return type(value) == "string" and value ~= "" and value:find("^[%w%-]+$") ~= nil
end

local function identity_error(why)
  return {
    error_class = "github-claim-identity-unverified",
    why = tostring(why),
  }
end

function M.source_ref(repo, bot_login)
  return {
    kind = "github-assignee-query",
    ref = tostring(repo) .. "#issues?state=open&assignee=" .. tostring(bot_login),
  }
end

function M.from_values(repo_value, bot_login_value)
  local repo = strings.trim(repo_value)
  if repo == "" then
    return nil, identity_error("missing FKST_GITHUB_REPO")
  end
  local owner, name = forge_strings.split_repo(repo)
  if not valid_repo_segment(owner) or not valid_repo_segment(name) then
    return nil, identity_error("malformed FKST_GITHUB_REPO")
  end

  local bot_login = forge_strings.strip_bot_login_suffix(strings.trim(bot_login_value))
  if bot_login == nil or bot_login == "" then
    return nil, identity_error("missing FKST_GITHUB_BOT_LOGIN")
  end
  if not valid_bot_login(bot_login) then
    return nil, identity_error("malformed FKST_GITHUB_BOT_LOGIN")
  end

  return {
    repo = repo,
    bot_login = bot_login,
    source_ref = M.source_ref(repo, bot_login),
  }, nil
end

function M.read(read_env)
  if type(read_env) ~= "function" then
    return nil, identity_error("missing claim identity reader")
  end
  return M.from_values(read_env("FKST_GITHUB_REPO"), read_env("FKST_GITHUB_BOT_LOGIN"))
end

return M
