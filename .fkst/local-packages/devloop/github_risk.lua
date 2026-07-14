local github_risk = {}
local convergence_shared = require("devloop.convergence.shared")

local high_risk_patterns = {
  "^%.github/workflows/",
  "^%.github/actions/",
  "^%.github/dependabot%.yml$",
  "^%.github/CODEOWNERS$",
  "^Cargo%.toml$",
  "^Cargo%.lock$",
  "^package%.json$",
  "^package%-lock%.json$",
  "^pnpm%-lock%.yaml$",
  "^yarn%.lock$",
  "^requirements%.txt$",
  "^requirements/",
  "^pyproject%.toml$",
  "^poetry%.lock$",
  "^scripts/",
  "^%.github/",
  "^packages/[^/]+/raisers/[^/]+%.lua$",
  "^libraries/workflow/.+%.lua$",
  "^libraries/devloop/claims%.lua$",
  "^libraries/devloop/config%.lua$",
  -- forge is the gh/git/merge egress + auth authority surface; classify the whole
  -- production tree structurally (not by enumerated subdir) so no authority entrypoint
  -- (top-level github.lua/git.lua/merge_commands.lua, merge/*, future files) escapes the gate.
  "^libraries/forge/.+%.lua$",
}

function github_risk.github_high_risk_path(path)
  local text = tostring(path or "")
  for _, pattern in ipairs(high_risk_patterns) do
    if text:find(pattern) ~= nil then
      return true
    end
  end
  return false
end

function github_risk.github_high_risk_paths(paths)
  local result = {}
  for _, path in ipairs(paths or {}) do
    if github_risk.github_high_risk_path(path) then
      table.insert(result, tostring(path))
    end
  end
  return result
end

function github_risk.github_diff_name_paths(stdout)
  local paths = {}
  for line in tostring(stdout or ""):gmatch("([^\r\n]+)") do
    local path = line:gsub("^%s+", ""):gsub("%s+$", "")
    if path ~= "" then
      table.insert(paths, path)
    end
  end
  return paths
end

local function unknown_diff_name_risk(reason)
  return {
    high_risk = true,
    known = false,
    reason = reason,
    paths = {},
    high_risk_paths = {},
  }
end

function github_risk.github_diff_name_risk(result)
  if type(result) ~= "table" then
    return unknown_diff_name_risk("diff-name-only-unclassifiable")
  end
  if result.exit_code ~= 0 then
    return unknown_diff_name_risk("diff-name-only-failed")
  end
  if type(result.stdout) ~= "string" then
    return unknown_diff_name_risk("diff-name-only-unclassifiable")
  end
  local paths = github_risk.github_diff_name_paths(result.stdout)
  if #paths == 0 then
    return unknown_diff_name_risk("diff-name-only-empty")
  end
  local high_risk_paths = github_risk.github_high_risk_paths(paths)
  return {
    high_risk = #high_risk_paths > 0,
    known = true,
    reason = #high_risk_paths > 0 and "high-risk-paths" or "normal-risk-paths",
    paths = paths,
    high_risk_paths = high_risk_paths,
  }
end

function github_risk.github_paths_digest(paths)
  local selected = {}
  for _, path in ipairs(paths or {}) do
    table.insert(selected, tostring(path))
  end
  table.sort(selected)
  return convergence_shared.source_ref_digest({
    kind = "github-paths",
    ref = table.concat(selected, "\n"),
  })
end

return github_risk
