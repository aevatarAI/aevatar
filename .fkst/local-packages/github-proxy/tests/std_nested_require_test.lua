local gh_exec = require("forge.github.exec")
local git_exec = require("forge.git.exec")

return {
  test_nested_std_require_resolves = function()
    assert(type(gh_exec.run) == "function", "forge.github.exec must resolve")
    assert(type(git_exec.run) == "function", "forge.git.exec must resolve")
  end,
}
