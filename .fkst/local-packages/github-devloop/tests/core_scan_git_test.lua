local git_mechanics = require("devloop.git_mechanics")
local h = require("tests.devloop_core_helpers")
local core = h.core
local t = h.t

local function result(exit_code, stdout, stderr)
  return {
    exit_code = exit_code,
    stdout = stdout or "",
    stderr = stderr or "",
  }
end

local function assert_error_contains(fn, needle)
  local ok, err = pcall(fn)
  t.eq(ok, false)
  t.is_true(tostring(err):find(needle, 1, true) ~= nil)
end

return {
  test_run_required_returns_success_result = function()
    local expected = result(0, "ok\n", "")

    local actual = git_mechanics.run_required(expected, "scan op")

    t.eq(actual, expected)
  end,

  test_run_required_raises_with_github_devloop_prefix = function()
    assert_error_contains(function()
      git_mechanics.run_required(result(7, "", "bad ref"), "scan op")
    end, "github-devloop: scan op failed: bad ref")
  end,

  test_scan_git_is_ancestor_maps_zero_to_true = function()
    local calls = {}
    local git = {
      is_ancestor = function(ancestor_sha, descendant_sha, timeout)
        table.insert(calls, {
          ancestor_sha = ancestor_sha,
          descendant_sha = descendant_sha,
          timeout = timeout,
        })
        return result(0, "", "")
      end,
    }

    t.eq(git_mechanics.is_ancestor(git, "aaaa1111", "bbbb2222", "scan ancestor check"), true)
    t.eq(#calls, 1)
    t.eq(calls[1].ancestor_sha, "aaaa1111")
    t.eq(calls[1].descendant_sha, "bbbb2222")
    t.eq(calls[1].timeout, 30)
  end,

  test_scan_git_is_ancestor_maps_one_to_false = function()
    local git = {
      is_ancestor = function()
        return result(1, "", "")
      end,
    }

    t.eq(git_mechanics.is_ancestor(git, "aaaa1111", "bbbb2222", "scan ancestor check"), false)
  end,

  test_scan_git_is_ancestor_raises_on_other_exit_code = function()
    local git = {
      is_ancestor = function()
        return result(128, "", "fatal")
      end,
    }

    assert_error_contains(function()
      git_mechanics.is_ancestor(git, "aaaa1111", "bbbb2222", "scan ancestor check")
    end, "github-devloop: scan ancestor check failed: fatal")
  end,
}
