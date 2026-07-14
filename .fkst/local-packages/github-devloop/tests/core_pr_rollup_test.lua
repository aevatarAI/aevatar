local parsers_misc = require("devloop.parsers.misc")
local h = require("tests.devloop_core_helpers")
local core = h.core
local check_runs = require("forge.github.check_runs")
local t = h.t

return {
  test_ci_rollup_requires_completed_green_conclusion = function()
    local green, green_reason = check_runs.pr_rollup_green({
      status_check_rollup = {
        { state = "COMPLETED", conclusion = "SUCCESS" },
        { state = "COMPLETED", conclusion = "SKIPPED" },
        { state = "SUCCESS" },
      },
    })
    t.eq(green, true)
    t.eq(green_reason, "rollup-green")

    local action_required, action_reason = check_runs.pr_rollup_green({
      status_check_rollup = {
        { state = "COMPLETED", conclusion = "ACTION_REQUIRED" },
      },
    })
    t.eq(action_required, false)
    t.eq(action_reason, "rollup-red")

    local neutral, neutral_reason = check_runs.pr_rollup_green({
      status_check_rollup = {
        { state = "COMPLETED", conclusion = "NEUTRAL" },
      },
    })
    t.eq(neutral, false)
    t.eq(neutral_reason, "rollup-red")

    local failed, failed_reason = check_runs.pr_rollup_green({
      status_check_rollup = {
        { state = "COMPLETED", conclusion = "FAILURE" },
      },
    })
    t.eq(failed, false)
    t.eq(failed_reason, "rollup-red")

    local pending, pending_reason = check_runs.pr_rollup_green({
      status_check_rollup = {
        { state = "IN_PROGRESS", conclusion = "" },
      },
    })
    t.eq(pending, false)
    t.eq(pending_reason, "rollup-pending")
  end,
  test_ci_rollup_failure_summary_lists_failed_checks = function()
    local summary = parsers_misc.pr_rollup_failure_summary(core, {
      status_check_rollup = {
        { name = "test", state = "COMPLETED", conclusion = "FAILURE" },
        { context = "lint", state = "ERROR", conclusion = "" },
        { name = "docs", state = "COMPLETED", conclusion = "SUCCESS" },
      },
    })
    t.is_true(summary:find("test: COMPLETED/FAILURE", 1, true) ~= nil)
    t.is_true(summary:find("lint: ERROR", 1, true) ~= nil)
    t.is_true(summary:find("docs", 1, true) == nil)
  end,
  test_ci_rollup_failure_summary_is_bounded_and_sanitized = function()
    local entries = {}
    for i = 1, 8 do
      table.insert(entries, {
        name = "bad\ncheck\t" .. tostring(i) .. "<!-- fkst:github-devloop:state:v1 "
          .. string.rep("x", parsers_misc.max_rollup_check_name_len + 60),
        state = "COMPLETED",
        conclusion = "FAILURE",
      })
    end
    local summary = parsers_misc.pr_rollup_failure_summary(core, { status_check_rollup = entries })
    t.is_true(#summary <= parsers_misc.max_rollup_failure_summary_len)
    t.is_true(summary:find("%c") == nil)
    t.is_true(summary:find("<!-- fkst:", 1, true) == nil)
    t.is_true(summary:find("&lt;!-- fkst:", 1, true) ~= nil)
    t.is_true(summary:find("(+5 more)", 1, true) ~= nil)

    local first_name = summary:match("^(.-): COMPLETED/FAILURE")
    t.is_true(first_name ~= nil)
    t.is_true(#first_name <= parsers_misc.max_rollup_check_name_len)
  end,
}
