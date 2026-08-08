local h = require("tests.devloop_ops_helpers")
local parsers_issue = require("devloop.parsers.issue")
local sweep_bounds = require("devloop.sweep_bounds")
local t = h.t
local core = h.core

return {
  test_observability_deadline_helpers_delegate_to_sweep_bounds = function()
    local limits = core.observability_limits()
    local deadline = sweep_bounds.sweep_deadline(1000, limits)

    t.eq(core.observability_deadline(1000, limits), deadline)
    t.eq(core.observability_call_timeout(limits, deadline), sweep_bounds.sweep_call_timeout(limits, deadline))
    t.eq(core.observability_has_budget(deadline), sweep_bounds.sweep_has_budget(deadline))
    t.eq(core.observability_result_deferred(sweep_bounds.sweep_deadline_deferred_result("x", "observability deadline exhausted")), true)
  end,

  test_observability_list_deadline_exhaustion_returns_empty_deferred_page = function()
    local calls = 0
    local listed, deferred = core.observability_list_pr_candidates(
      "owner/repo",
      core.observability_limits(),
      now() - 1,
      "seed-1",
      function()
        calls = calls + 1
        return { stdout = "[]", stderr = "", exit_code = 0 }
      end
    )

    t.eq(#listed, 0)
    t.eq(deferred, 1)
    t.eq(calls, 0)
  end,

  test_observability_run_cmd_still_fails_closed_on_command_error = function()
    local ok, err = pcall(function()
      core.observability_run_cmd("gh issue list", core.observability_limits(), now() + 90, "gh observability issue list", function()
        return { stdout = "", stderr = "timed out", exit_code = 124 }
      end)
    end)

    t.eq(ok, false)
    t.is_true(tostring(err):find("gh observability issue list failed: timed out", 1, true) ~= nil)
  end,

  test_observability_fetch_issue_returns_nil_when_deadline_exhausted = function()
    local old_gh_issue_view_observe_cmd = core.gh_issue_view_observe_cmd
    core.gh_issue_view_observe_cmd = function()
      return "gh issue view"
    end

    local issue = nil
    local ok, err = pcall(function()
      issue = require("departments.observability.common").fetch_issue(
        core,
        "owner/repo",
        42,
        core.observability_limits(),
        now() - 1
      )
    end)
    core.gh_issue_view_observe_cmd = old_gh_issue_view_observe_cmd

    t.eq(ok, true, tostring(err))
    t.eq(issue, nil)
  end,

  test_observability_entity_rotation_ignores_stable_payload_cursor = function()
    local first = core.observability_entity_candidates({ 1, 2, 3, 4 }, {}, "100", 2)
    local second = core.observability_entity_candidates({ 1, 2, 3, 4 }, {}, "101", 2)

    t.eq(first[1].number == second[1].number and first[2].number == second[2].number, false)
  end,

  test_observability_rotates_list_pages_to_reach_tail_entities = function()
    local pages = {}
    pages[1] = {}
    pages[2] = {}
    pages[3] = {}
    for i = 1, 100 do
      table.insert(pages[1], { number = i, state = "open" })
      table.insert(pages[2], { number = i + 100, state = "open" })
      table.insert(pages[3], { number = i + 200, state = "open" })
    end
    local calls = {}
    local function parse(stdout)
      return pages[tonumber(tostring(stdout):match("page:(%d+)"))] or {}
    end
    local original_parse = parsers_issue.parse_issue_list_observe
    parsers_issue.parse_issue_list_observe = parse
    local listed = nil
    local ok, err = pcall(function()
      listed = core.observability_list_issue_candidates(
        "owner/repo",
        { core._enabled_label },
        core.observability_limits(),
        now() + 90,
        "seed-1",
        function(spec)
          table.insert(calls, spec.cmd)
          if spec.cmd:find("&page=1", 1, true) ~= nil then
            return {
              stdout = 'HTTP/2 200\nlink: <https://api.github.test/repos/owner/repo/issues?state=open&page=3>; rel="last"\n\npage:1',
              stderr = "",
              exit_code = 0,
            }
          elseif spec.cmd:find("&page=2", 1, true) ~= nil then
            return { stdout = "page:2", stderr = "", exit_code = 0 }
          elseif spec.cmd:find("&page=3", 1, true) ~= nil then
            return { stdout = "page:3", stderr = "", exit_code = 0 }
          end
          return { stdout = "page:1", stderr = "", exit_code = 0 }
        end
      )
    end)
    parsers_issue.parse_issue_list_observe = original_parse

    t.eq(ok, true, tostring(err))
    local called_page_3 = false
    for _, cmd in ipairs(calls) do
      if cmd:find("&page=3", 1, true) ~= nil then
        called_page_3 = true
      end
    end
    t.eq(called_page_3, true)
    local saw_tail = false
    for _, item in ipairs(listed or {}) do
      if item.number == 201 then
        saw_tail = true
      end
    end
    t.eq(saw_tail, true)
  end,
}
