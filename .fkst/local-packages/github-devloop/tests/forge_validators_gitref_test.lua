local t = fkst.test
local forge_validators = require("devloop.forge_validators")
local marker_builders = require("devloop.markers.builders")

local source_roots = {
  "libraries/devloop",
  "packages",
}

local function read_file(path)
  local handle = assert(io.open(path, "r"))
  local body = handle:read("*a")
  handle:close()
  return body
end

local function lua_source_paths(root)
  local paths = {}
  local find = assert(io.popen("find " .. root .. " -type f -name '*.lua' | sort"))
  for path in find:lines() do
    if path:find("/tests/", 1, true) == nil then
      table.insert(paths, path)
    end
  end
  local ok = find:close()
  if ok == false then
    error("github-devloop: source discovery failed")
  end
  return paths
end

local function all_source_paths()
  local paths = {}
  for _, root in ipairs(source_roots) do
    for _, path in ipairs(lua_source_paths(root)) do
      table.insert(paths, path)
    end
  end
  return paths
end

local function forty(char)
  return string.rep(char, 40)
end

return {
  test_forge_validators_freezes_current_gitref_behavior = function()
    local cases = {
      { name = "nil", value = nil, ref = false, sha = false },
      { name = "empty", value = "", ref = false, sha = false },
      { name = "blank", value = " ", ref = false, sha = false },
      { name = "newline", value = "\n", ref = false, sha = false },
      { name = "main", value = "main", ref = true, sha = false },
      { name = "HEAD", value = "HEAD", ref = true, sha = false },
      { name = "feature branch", value = "feature/foo", ref = true, sha = false },
      { name = "full ref", value = "refs/heads/main", ref = true, sha = false },
      { name = "short hex", value = "deadbee", ref = true, sha = true },
      { name = "forty lowercase hex", value = forty("a"), ref = true, sha = true },
      { name = "forty uppercase hex", value = forty("A"), ref = true, sha = true },
      { name = "thirty nine hex", value = string.rep("a", 39), ref = true, sha = true },
      { name = "forty one hex", value = string.rep("a", 41), ref = true, sha = true },
      { name = "non hex same length", value = forty("g"), ref = true, sha = false },
      { name = "slash", value = "/", ref = false, sha = false },
      { name = "space", value = "feature foo", ref = false, sha = false },
      { name = "semicolon", value = "feature;foo", ref = false, sha = false },
      { name = "shell metachar", value = "feature$(whoami)", ref = false, sha = false },
    }

    for _, case in ipairs(cases) do
      t.eq(forge_validators.is_git_ref_safe(case.value), case.ref, case.name .. " ref")
      t.eq(forge_validators.is_git_sha(case.value), case.sha, case.name .. " sha")
    end
  end,

  test_marker_builders_gitref_validation_does_not_need_base_install = function()
    local M = {}
    local marker = marker_builders.implementing_marker(M,
      "github-devloop/issue/owner/repo/42",
      "dedup-key",
      "feature/foo",
      forty("a"),
      "main",
      forty("b")
    )

    t.is_true(marker:find('branch="feature/foo"', 1, true) ~= nil)
    t.is_true(marker:find('base_branch="main"', 1, true) ~= nil)
  end,

  test_gitref_ambient_m_aliases_are_retired_from_production_sources = function()
    local offenders = {}
    for _, path in ipairs(all_source_paths()) do
      local body = read_file(path)
      if body:find("_is_git_sha", 1, true) ~= nil
        or body:find("_is_git_ref_safe", 1, true) ~= nil then
        table.insert(offenders, path)
      end
    end

    t.eq(table.concat(offenders, "\n"), "")
  end,
}
