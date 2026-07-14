local base_ids = require("devloop.base_ids")
local strings = require("contract.strings")
local C = {}
local forge_validators = require("devloop.forge_validators")
local env = require("workflow.env")

local allowed_env = {
  FKST_GITHUB_BOT_LOGIN = true,
  FKST_GITHUB_CLAIM_MODE = true,
  FKST_GITHUB_REPO = true,
  FKST_GITHUB_WRITE = true,
  FKST_DEVLOOP_UPSTREAM_BRANCH = true,
  FKST_DEVLOOP_INTEGRATION_BRANCH = true,
  FKST_DEVLOOP_FORK_GRACE_HOURS = true,
  FKST_DEVLOOP_MAX_INFLIGHT = true,
  FKST_DEVLOOP_MANAGED_SIBLING_REPOS = true,
  FKST_DEVLOOP_MANAGED_BOT_LOGINS = true,
  FKST_DEVLOOP_ROLLUP_MERGE = true,
  FKST_DEVLOOP_ROLLUP_AUTOFIX = true,
  FKST_DEVLOOP_ROLLUP_RED_WINDOW_MINUTES = true,
  FKST_DEVLOOP_RELEASE_NOTES_FALLBACK = true,
  FKST_DEVLOOP_CONFLICT_LOG_CMD = true,
  FKST_DEVLOOP_BOARD_CMD = true,
  FKST_DEVLOOP_TEST_COMMAND = true,
  FKST_OUTPUT_LANG = true,
  FKST_DEBUG_STAMP = true,
}

local allowed_presence_env = {
  GH_TOKEN = true,
  GITHUB_TOKEN = true,
  FKST_GITHUB_READ_TOKEN = true,
  FKST_GITHUB_WRITE_TOKEN = true,
  FKST_GITHUB_MERGE_TOKEN = true,
}

local function read_env_command(name)
  if not allowed_env[name] then
    error("github-devloop: env name is not allowed")
  end
  return 'printf %s "$' .. name .. '"'
end

local function env_present_command(name)
  if not allowed_presence_env[name] then
    error("github-devloop: env name is not allowed")
  end
  return 'if [ -n "${' .. name .. ':-}" ]; then printf present; fi'
end

local read_env = env.read_env(read_env_command)

function C.read_env_command(name)
  return read_env_command(name)
end

function C.read_env(name, exec)
  return read_env(name, exec)
end

function C.env_present_command(M, name)
  return env_present_command(name)
end

function C.env_present(M, name, exec)
  local run = exec or exec_sync
  if type(run) ~= "function" then
    return false
  end
  local ok, out = pcall(run, env_present_command(name))
  return ok and type(out) == "table" and out.exit_code == 0 and out.stdout ~= ""
end

function C.write_mode(M, exec)
  return C.read_env("FKST_GITHUB_WRITE", exec) == "1" and "real" or "dry-run"
end

-- Claim mode is opt-in and additive: the default (unset/empty/unknown) is
-- "assignee", which is byte-for-byte today's behavior. "label" opts into
-- holding ownership via the fkst-dev:claimed label, which a GitHub App can set
-- even though an App cannot be an issue assignee.
function C.claim_mode(exec)
  local raw = C.read_env("FKST_GITHUB_CLAIM_MODE", exec)
  raw = strings.trim(raw or "")
  if raw == "label" then
    return "label"
  end
  return "assignee"
end

-- Rollup auto-fix is opt-in and additive: default (unset/anything-but-"1") is
-- off, which is byte-for-byte today's behavior (the rollup-health watchdog only
-- files a passive issue). When "1", the watchdog issue is created already
-- fkst-dev:enabled + fkst-class:expedite so the loop claims and fixes the red
-- rollup ahead of new issues (expedite class + inflight cap = priority).
function C.rollup_autofix_enabled(M, exec)
  return strings.trim(C.read_env("FKST_DEVLOOP_ROLLUP_AUTOFIX", exec) or "") == "1"
end

function C.max_inflight(M, exec)
  local value = C.read_env("FKST_DEVLOOP_MAX_INFLIGHT", exec)
  if value == nil then
    return nil
  end
  value = strings.trim(value)
  if value == "" then
    return nil
  end
  local parsed = tonumber(value)
  if parsed == nil or parsed ~= math.floor(parsed) or parsed < 1 or parsed > 100 then
    error("github-devloop: invalid FKST_DEVLOOP_MAX_INFLIGHT")
  end
  return parsed
end

function C.managed_sibling_repos(M, exec)
  local raw = C.read_env("FKST_DEVLOOP_MANAGED_SIBLING_REPOS", exec)
  local repos = {}
  if raw == nil then
    return repos
  end
  for entry in tostring(raw):gmatch("[^,%s]+") do
    local repo = tostring(entry)
    if base_ids.issue_ref_round_trips(repo, 1) then
      repos[repo] = true
    end
  end
  return repos
end

function C.max_fix_rounds(M)
  return 12
end

function C.max_converge_rounds(M)
  return 8
end

function C.default_test_command(M)
  return "scripts/run.sh test"
end

function C.test_command(M, exec)
  local command = C.read_env("FKST_DEVLOOP_TEST_COMMAND", exec)
  if command == nil then
    return C.default_test_command(M)
  end
  return command
end

function C.local_iteration_test_command(M, _exec)
  return "scripts/run.sh test-affected"
end

local function current_checkout_branch(M, exec)
  local run = exec or exec_argv
  if type(run) ~= "function" then
    error("github-devloop: branch config requires exec_argv")
  end
  local git = require("forge.git").new(run)
  local ok, out = pcall(function()
    return git.current_branch(30)
  end)
  if not ok or type(out) ~= "table" or out.exit_code ~= 0 then
    error("github-devloop: current checkout branch read failed")
  end
  local branch = strings.trim(out.stdout)
  if branch == "HEAD" or not forge_validators.is_git_ref_safe(branch) then
    error("github-devloop: invalid current checkout branch")
  end
  return branch
end

local function validated_branch(M, name, branch)
  branch = strings.trim(branch)
  if not forge_validators.is_git_ref_safe(branch) then
    error("github-devloop: invalid " .. name)
  end
  return branch
end

function C.branch_config(M, exec)
  local upstream_env = C.read_env("FKST_DEVLOOP_UPSTREAM_BRANCH", exec)
  local upstream = upstream_env
  if upstream == nil then
    upstream = current_checkout_branch(M, exec)
  end
  upstream = validated_branch(M, "FKST_DEVLOOP_UPSTREAM_BRANCH", upstream)
  local integration = C.read_env("FKST_DEVLOOP_INTEGRATION_BRANCH", exec)
  if integration == nil then
    integration = upstream
  end
  integration = validated_branch(M, "FKST_DEVLOOP_INTEGRATION_BRANCH", integration)
  return {
    upstream = upstream,
    integration = integration,
  }
end

function C.devloop_config(M, exec)
  local branches = C.branch_config(M, exec)
  local rollup_merge = C.read_env("FKST_DEVLOOP_ROLLUP_MERGE", exec) or "auto"
  rollup_merge = strings.trim(rollup_merge)
  if rollup_merge ~= "auto" and rollup_merge ~= "manual" then
    error("github-devloop: invalid FKST_DEVLOOP_ROLLUP_MERGE")
  end
  return {
    repo = C.read_env("FKST_GITHUB_REPO", exec),
    bot_login = C.read_env("FKST_GITHUB_BOT_LOGIN", exec),
    write_mode = C.write_mode(M, exec),
    upstream_branch = branches.upstream,
    integration_branch = branches.integration,
    rollup_merge = rollup_merge,
    allow_release_notes_fallback = C.read_env("FKST_DEVLOOP_RELEASE_NOTES_FALLBACK", exec) == "1",
  }
end

return C
