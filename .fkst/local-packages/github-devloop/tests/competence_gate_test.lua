local h = require("tests.devloop_core_helpers")
local gate = require("tests.competence_gate_helpers")
local t = h.t

local function by_id(values)
  local out = {}
  for _, value in ipairs(values or {}) do
    out[value.id or value] = value
  end
  return out
end

local function joined_errors(errors)
  return table.concat(errors or {}, "\n")
end

return {
  test_competence_gate_clean_tree_has_no_false_rejects = function()
    local report = gate.competence_gate_report()
    t.eq(report.schema, "github-devloop.competence-gate-report.v1")
    t.eq(report.framing, "evidence-carrying adversarial review for durable state-machine changes")
    t.eq(#report.clean_errors, 0, joined_errors(report.clean_errors))
    t.eq(report.metrics.false_reject_rate, 0)
  end,

  test_competence_gate_rejects_all_held_out_ready_split_bugs = function()
    local report = gate.competence_gate_report()
    t.eq(#report.challenges, 7)
    for _, challenge in ipairs(report.challenges) do
      t.eq(challenge.rejected, true, challenge.id .. ": " .. joined_errors(challenge.errors))
    end
    t.eq(report.metrics.challenge_recall, 1)
    t.eq(report.metrics.bug_class_recall, 1)
    t.eq(report.metrics.operator_escape_rate, 0)
  end,

  test_competence_gate_negative_control_inventory_is_exact = function()
    local report = gate.competence_gate_report()
    local controls = by_id(report.negative_controls)
    local expected = {
      "001-release-replay-uses-split-version",
      "002-queue-wait-extra-successor",
      "003-dependency-hold-marker-families",
      "004-operator-waiver-does-not-write-raw-ready",
      "005-ready-replay-uses-inner-version",
      "006-ready-dependency-partition-boundary",
      "007-partial-write-idempotency-completeness",
    }
    t.eq(#report.negative_controls, #expected)
    for _, id in ipairs(expected) do
      t.eq(controls[id], id)
    end
  end,

  test_competence_gate_challenge_error_classes_are_stable = function()
    local challenges = by_id(gate.competence_gate_report().challenges)
    t.eq(challenges["001"].bug_class, "strand")
    t.eq(challenges["002"].bug_class, "grader-weakening")
    t.eq(challenges["003"].bug_class, "false-terminal")
    t.eq(challenges["004"].bug_class, "operator-path-partial-migration")
    t.eq(challenges["005"].bug_class, "version-lineage-mismatch")
    t.eq(challenges["006"].bug_class, "partition-boundary")
    t.eq(challenges["007"].bug_class, "partial-write-idempotency")
  end,
}
