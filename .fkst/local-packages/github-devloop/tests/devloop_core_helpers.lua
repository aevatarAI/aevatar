local core = require("core")
local t = fkst.test
local gh_argv = require("testkit.gh_argv_mock")
gh_argv.install(t, core)

local function has_value(values, expected)
  for _, value in ipairs(values or {}) do
    if value == expected then
      return true
    end
  end
  return false
end

local function source_ref()
  return {
    kind = "external",
    ref = "owner/repo#issue/42",
  }
end

local function issue(extra)
  local fields = extra or {}
  local updated_at = fields.updated_at or "2026-06-03T01:02:03Z"
  local value = {
    schema = "github-proxy.v1",
    type = "issue",
    repo = "owner/repo",
    number = 42,
    title = "Implement decision recorder",
    url = "https://github.example/owner/repo/issues/42",
    state = "OPEN",
    updated_at = updated_at,
    labels = { "fkst-dev:enabled" },
    dedup_key = "owner/repo#issue#42@2026-06-03T01:02:03Z",
    source_ref = source_ref(),
  }
  for key, field in pairs(fields) do
    value[key] = field
  end
  return value
end

local function reached(extra)
  local value = {
    schema = "consensus.consensus_reached.v1",
    proposal_id = "github-devloop/issue/owner/repo/42",
    decision = "approve",
    body = "All angles approve.",
    dedup_key = "consensus:github-devloop/issue/owner/repo/42/2026-06-03T01-02-03Z",
    source_ref = source_ref(),
  }
  for key, field in pairs(extra or {}) do
    value[key] = field
  end
  return value
end

local function unresolved(extra)
  local value = {
    schema = "consensus.consensus_converge.v1",
    proposal_id = "github-devloop/issue/owner/repo/42",
    dedup_key = "consensus:github-devloop/issue/owner/repo/42/2026-06-03T01-02-03Z",
    source_ref = source_ref(),
  }
  for key, field in pairs(extra or {}) do
    value[key] = field
  end
  return value
end

return {
  core = core,
  t = t,
  has_value = has_value,
  source_ref = source_ref,
  issue = issue,
  reached = reached,
  unresolved = unresolved,
  argv_rendered = gh_argv.argv_rendered,
}
