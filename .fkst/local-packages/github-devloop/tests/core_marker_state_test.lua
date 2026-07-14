local devloop_base = require("devloop.base")
local requests_labels = require("devloop.requests.labels")
local requests_lifecycle = require("devloop.requests.lifecycle")
local parsers_issue = require("devloop.parsers.issue")
local h = require("tests.devloop_core_helpers")
local conv_reconcile = require("devloop.convergence.reconcile")
local core = h.core
local transition_version = require("contract.transition_version")
local t = h.t
local gate = require("devloop.gate")
local m_builders = require("devloop.markers.builders")
local reached = h.reached
local unresolved = h.unresolved
local ai_sentinel = string.char(226, 159, 166) .. "AI:FKST" .. string.char(226, 159, 167)
local verdict_summary_label = "Three-angle verdicts: "

local function marker_attrs(marker)
  local attrs = {}
  for key, value in tostring(marker or ""):gmatch('([%w._-]+)="([^"]*)"') do
    attrs[key] = value
  end
  return attrs
end

local function guard_order_value(attrs, key)
  if key == "version_order_key" then
    return core.version_order_key(attrs.version)
  end
  return attrs[key]
end

local function compare_guard_token(left, right)
  local left_missing = left == nil or tostring(left) == ""
  local right_missing = right == nil or tostring(right) == ""
  if left_missing ~= right_missing then
    return left_missing and -1 or 1
  end
  local left_number = tonumber(left)
  local right_number = tonumber(right)
  if left_number ~= nil and right_number ~= nil and left_number ~= right_number then
    return left_number > right_number and 1 or -1
  end
  local left_text = tostring(left or "")
  local right_text = tostring(right or "")
  if left_text == right_text then
    return 0
  end
  return left_text > right_text and 1 or -1
end

local function compare_marker_order_key(left, right)
  local left_key = core.marker_order_key(left.version, left.state)
  local right_key = core.marker_order_key(right.version, right.state)
  if left_key == right_key then
    return 0
  end
  return left_key > right_key and 1 or -1
end

local function guard_attrs_current(comments, proposal_id)
  local current = nil
  local order_by = { "marker_order_key", "version_order_key", "stage_rank" }
  for _, body in ipairs(comments or {}) do
    for marker in tostring(body):gmatch("<!%-%- fkst:github%-devloop:state:v1.-%-%->") do
      local attrs = marker_attrs(marker)
      if attrs.proposal == proposal_id then
        local newer = current == nil
        for _, key in ipairs(order_by) do
          if not newer then
            local cmp = compare_guard_token(guard_order_value(attrs, key), guard_order_value(current, key))
            if cmp > 0 then
              newer = true
              break
            elseif cmp < 0 then
              break
            end
          end
        end
        if newer then
          current = attrs
        end
      end
    end
  end
  return current
end

local function assert_marker_order_pair(left, right)
  local canonical = core.compare_state_marker_order({
    state = left.state,
    version = left.version,
  }, right.state, right.version)
  t.eq(compare_marker_order_key(left, right), canonical)
  local reverse_canonical = core.compare_state_marker_order({
    state = right.state,
    version = right.version,
  }, left.state, left.version)
  t.eq(compare_marker_order_key(right, left), reverse_canonical)
end

local function assert_guard_selects_canonical(left, right)
  local proposal_id = "github-devloop/issue/owner/repo/42"
  for _, comments in ipairs({
    {
      core.state_marker(proposal_id, left.state, left.version),
      core.state_marker(proposal_id, right.state, right.version),
    },
    {
      core.state_marker(proposal_id, right.state, right.version),
      core.state_marker(proposal_id, left.state, left.version),
    },
  }) do
    local canonical = core.current_state(comments, proposal_id)
    local guarded = guard_attrs_current(comments, proposal_id)
    t.eq(guarded.state, canonical.state)
    t.eq(guarded.version, canonical.version)
  end
end

local function assert_marker_order_invariant(left, right)
  assert_marker_order_pair(left, right)
  assert_guard_selects_canonical(left, right)
end

return {
  test_version_order_key_public_surface_delegates_to_std_contract = function()
    t.eq(
      core.version_order_key("ready/consensus-2026-06-17T22:18:19Z/loop/12"),
      "2026-06-17T22-18-19Z/loop/000000000012"
    )
  end,

  test_marker_order_key_matches_canonical_transition_order_invariant = function()
    local base = "ready/consensus-github-devloop/issue/owner/repo/42/2026-06-04T01-02-03Z"
    local pairs = {
      { left = { state = "thinking", version = base .. "/loop/9" }, right = { state = "thinking", version = base .. "/loop/10" } },
      { left = { state = "fixing", version = base .. "/fix/9" }, right = { state = "fixing", version = base .. "/fix/10" } },
      { left = { state = "implementing", version = base .. "/reimplement/1" }, right = { state = "implementing", version = base .. "/reimplement/2" } },
      { left = { state = "ready", version = base .. "/timeout/ready/1" }, right = { state = "ready", version = base .. "/timeout/ready/2" } },
      { left = { state = "review-meta", version = base .. "/review-meta-action/1" }, right = { state = "review-meta", version = base .. "/review-meta-action/2" } },
      { left = { state = "reviewing", version = base .. "/review-loop/1" }, right = { state = "reviewing", version = base .. "/review-loop/2" } },
      { left = { state = "ready", version = base .. "/ready-split/1" }, right = { state = "ready", version = base .. "/ready-split/2" } },
      { left = { state = "review-meta", version = base .. "/review-meta-action/9/fix/1" }, right = { state = "fixing", version = base .. "/fix/2" } },
      { left = { state = "pr-open", version = "ready-consensus-v1" }, right = { state = "reviewing", version = "ready/consensus/v1" } },
      { left = { state = "pr-open", version = base }, right = { state = "reviewing", version = base } },
    }

    for _, pair in ipairs(pairs) do
      assert_marker_order_invariant(pair.left, pair.right)
    end
  end,

  test_marker_label_and_comment_builders = function()
    local proposal_id = "github-devloop/issue/owner/repo/42"
    local thinking_marker = core.state_marker(proposal_id, "thinking", "v1")
    local thinking_state = core.current_state({ thinking_marker }, proposal_id)
    t.eq(thinking_state.state, "thinking")
    t.eq(thinking_state.version, "v1")
    t.eq(thinking_state.stage_rank, core.stage_rank("thinking"))
    t.is_true(thinking_marker:find('marker_order_key="', 1, true) ~= nil)
    local ready_effects_marker = core.state_marker(proposal_id, "ready", "v2", "result-marker,ready-label,devloop-ready")
    t.is_true(ready_effects_marker:find('marker_order_key="', 1, true) ~= nil)
    t.is_true(ready_effects_marker:find('effects="result-marker,ready-label,devloop-ready"', 1, true) ~= nil)
    local ready_effects_state = core.current_state({ ready_effects_marker }, proposal_id)
    t.eq(ready_effects_state.state, "ready")
    t.eq(ready_effects_state.version, "v2")
    t.eq(ready_effects_state.stage_rank, core.stage_rank("ready"))
    local comments = {
      core.state_marker(proposal_id, "thinking", "v1"),
      core.state_marker(proposal_id, "ready", "v2"),
      core.state_marker("github-devloop/issue/owner/repo/99", "blocked", "v3"),
    }
    local current = core.current_state(comments, proposal_id)
    t.eq(current.state, "ready")
    t.eq(current.version, "v2")
    t.eq(core.transition_status("thinking", { "thinking" }, "ready"), "apply")
    t.eq(core.transition_status("ready", { "thinking" }, "ready"), "idempotent")
    t.eq(core.transition_status(nil, { "thinking" }, "ready"), "pending")
    t.eq(core.transition_status("implementing", { "thinking" }, "ready"), "stale")
    local versioned_current = {
      state = "ready",
      version = "consensus:github-devloop/issue/owner/repo/42/2026-06-04T01-02-03Z",
    }
    t.eq(core.versioned_transition_status(versioned_current, { "thinking" }, "ready", "consensus:github-devloop/issue/owner/repo/42/2026-06-03T01-02-03Z"), "stale")
    t.eq(core.versioned_transition_status(versioned_current, { "ready" }, "implementing", "consensus:github-devloop/issue/owner/repo/42/2026-06-04T01-02-03Z"), "apply")
    local ready_current = {
      state = "ready",
      version = "ready/consensus-github-devloop/issue/owner/repo/42/2026-06-04T01-02-03Z",
    }
    t.eq(core.versioned_transition_status(ready_current, { "ready" }, "implementing", "ready/consensus-github-devloop/issue/owner/repo/42/2026-06-03T01-02-03Z"), "stale")
    t.eq(core.cyclic_transition_status({ state = nil, version = nil }, { "fixing" }, "reviewing", "ready/consensus-github-devloop/issue/owner/repo/42/2026-06-03T01-02-03Z"), "pending")
    t.eq(core.cyclic_transition_status({
      state = "fixing",
      version = "ready/consensus-github-devloop/issue/owner/repo/42/2026-06-03T01-02-03Z",
    }, { "reviewing" }, "merge-ready", "ready-consensus-github-devloop-issue-owner-repo-42-2026-06-03T01-02-03Z"), "stale")
    t.eq(core.cyclic_transition_status({
      state = "merge-ready",
      version = "ready/consensus-github-devloop/issue/owner/repo/42/2026-06-03T01-02-03Z",
    }, { "reviewing" }, "fixing", "ready-consensus-github-devloop-issue-owner-repo-42-2026-06-03T01-02-03Z"), "apply")
    t.eq(core.cyclic_transition_status({
      state = "reviewing",
      version = "ready/consensus-github-devloop/issue/owner/repo/42/2026-06-03T01-02-03Z/fix/1",
    }, { "fixing" }, "reviewing", "ready/consensus-github-devloop/issue/owner/repo/42/2026-06-03T01-02-03Z", "ready/consensus-github-devloop/issue/owner/repo/42/2026-06-03T01-02-03Z/fix/1"), "idempotent")
    t.eq(core.cyclic_transition_status({
      state = "reviewing",
      version = "ready/consensus-github-devloop/issue/owner/repo/42/2026-06-03T01-02-03Z",
    }, { "fixing" }, "reviewing", core.fix_version_from_review_version("ready/consensus-github-devloop/issue/owner/repo/42/2026-06-03T01-02-03Z"), "ready/consensus-github-devloop/issue/owner/repo/42/2026-06-03T01-02-03Z/fix/2"), "pending")
    t.eq(core.cyclic_transition_status({
      state = "reviewing",
      version = "ready/consensus-github-devloop/issue/owner/repo/42/2026-06-03T01-02-03Z/fix/1",
    }, { "review-meta" }, "fixing", "ready/consensus-github-devloop/issue/owner/repo/42/2026-06-03T01-02-03Z"), "stale")
    local review_version = "ready/consensus-github-devloop/issue/owner/repo/42/2026-06-05T01-02-03Z"
    t.eq(core.compare_state_marker_order({ state = "pr-open", version = review_version }, "reviewing", review_version), -1)
    t.eq(core.compare_state_marker_order({ state = "reviewing", version = review_version }, "reviewing", review_version), 0)
    t.eq(core.compare_state_marker_order({ state = "merge-ready", version = review_version }, "reviewing", review_version), 1)
    t.eq(core.compare_state_marker_order({ state = "merge-ready", version = "ready/consensus-github-devloop/issue/owner/repo/42/2026-06-04T01-02-03Z" }, "reviewing", review_version), -1)
    t.eq(core.compare_state_marker_order({ state = "pr-open", version = "ready/consensus-github-devloop/issue/owner/repo/42/2026-06-06T01-02-03Z" }, "reviewing", review_version), 1)

    local marker = m_builders.result_marker(core, 
      proposal_id,
      "approve",
      "consensus:github-devloop/issue/owner/repo/42/v1"
    )
    t.eq(
      marker,
      '<!-- fkst:github-devloop:result:v1 proposal="github-devloop/issue/owner/repo/42" decision="approve" dedup="consensus:github-devloop/issue/owner/repo/42/v1" -->'
    )

    local label = requests_labels.build_result_label_request(core, "owner/repo", "42", reached())
    t.eq(label.schema, "github-proxy.label.v1")
    t.eq(label.add_labels[1], "fkst-dev:ready")
    t.eq(label.label_colors["fkst-dev:ready"], "0E8A16")
    t.is_true(h.has_value(label.remove_labels, "fkst-dev:thinking"))
    t.is_true(h.has_value(label.remove_labels, "fkst-dev:implementing"))
    t.is_true(h.has_value(label.remove_labels, "fkst-dev:pr-open"))
    t.is_true(h.has_value(label.remove_labels, "fkst-dev:reviewing"))
    t.is_true(h.has_value(label.remove_labels, "fkst-dev:merge-ready"))
    t.is_true(h.has_value(label.remove_labels, "fkst-dev:fixing"))
    t.is_true(h.has_value(label.remove_labels, "fkst-dev:impl-failed"))
    t.eq(#label.remove_labels, 12)
    t.eq(label.issue_number, "42")

    local awaiting = requests_labels.build_state_label_request(core,
      "owner/repo",
      "42",
      "awaiting-pr",
      "github-devloop/issue/owner/repo/42/label/awaiting-pr",
      { kind = "external", ref = "owner/repo#issue/42" }
    )
    t.eq(awaiting.add_labels[1], "fkst-dev:awaiting-pr")
    t.is_nil(awaiting.label_colors)

    t.eq(core.state_label_hint_matches({ "fkst-dev:enabled", "fkst-dev:reviewing" }, "reviewing"), true)
    t.eq(core.state_label_hint_matches({ "fkst-dev:enabled", "fkst-dev:pr-open" }, "reviewing"), false)
    t.eq(core.state_label_hint_matches({ "fkst-dev:enabled", "fkst-dev:reviewing", "fkst-dev:pr-open" }, "reviewing"), false)
    local reconcile = core.build_reconcile_state_label_request(
      "owner/repo",
      "42",
      proposal_id,
      "reviewing",
      "ready/consensus-github-devloop/issue/owner/repo/42/2026-06-03T01-02-03Z",
      { kind = "external", ref = "owner/repo#issue/42" }
    )
    t.eq(reconcile.add_labels[1], "fkst-dev:reviewing")
    t.eq(reconcile.remove_labels[1], "fkst-dev:thinking")
    t.eq(#reconcile.remove_labels, 12)
    t.is_true(reconcile.dedup_key:find("reconcile/label/github-devloop/issue/owner/repo/42/reviewing", 1, true) ~= nil)

    local completed = reached({
      angle_results = {
        { angle = "minimal", verdict = "approve" },
        { angle = "structural", verdict = "abstain" },
        { angle = "delete", verdict = "approve" },
      },
    })
    local comment = requests_lifecycle.build_result_comment_request(core, "owner/repo", "42", completed)
    t.eq(comment.schema, "github-proxy.v1")
    t.eq(comment.issue_number, "42")
    t.is_true(comment.body:find("github-devloop decision: approve", 1, true) ~= nil)
    t.is_true(comment.body:find(verdict_summary_label .. "minimal=approve structural=abstain delete=approve", 1, true) ~= nil)
    t.is_true(comment.body:find(ai_sentinel, 1, true) ~= nil)
    t.is_true(comment.body:find('fkst:github-devloop:result:v1 proposal="github-devloop/issue/owner/repo/42"', 1, true) ~= nil)
    t.is_true(comment.body:find('fkst:github-devloop:state:v1 proposal="github-devloop/issue/owner/repo/42" state="ready"', 1, true) ~= nil)
    t.is_true(comment.body:find('stage_rank="500"', 1, true) ~= nil)
    t.is_true(comment.body:find('marker_order_key="', 1, true) ~= nil)
    t.is_true(comment.body:find('effects="result-marker,ready-label,devloop-ready"', 1, true) ~= nil)
    local comment_version = tostring(completed.dedup_key):gsub(":", "-")
    t.eq(
      comment.dedup_key,
      tostring(completed.proposal_id) .. "/comment/" .. tostring(completed.decision) .. "/" .. comment_version
    )
  end,
  test_comment_dedup_key_includes_consensus_version = function()
    local first = reached({
      dedup_key = "consensus:github-devloop/issue/owner/repo/42/v1",
    })
    local second = reached({
      dedup_key = "consensus:github-devloop/issue/owner/repo/42/v2",
    })

    local first_comment = requests_lifecycle.build_result_comment_request(core, "owner/repo", "42", first)
    local second_comment = requests_lifecycle.build_result_comment_request(core, "owner/repo", "42", second)

    t.eq(first_comment.dedup_key, "github-devloop/issue/owner/repo/42/comment/approve/consensus-github-devloop/issue/owner/repo/42/v1")
    t.eq(second_comment.dedup_key, "github-devloop/issue/owner/repo/42/comment/approve/consensus-github-devloop/issue/owner/repo/42/v2")
    t.eq(first_comment.dedup_key ~= second_comment.dedup_key, true)
  end,
  test_current_state_uses_highest_version_not_append_order = function()
    local proposal_id = "github-devloop/issue/owner/repo/42"
    local comments = {
      core.state_marker(proposal_id, "ready", "consensus:github-devloop/issue/owner/repo/42/2026-06-04T01-02-03Z"),
      core.state_marker(proposal_id, "blocked", "consensus:github-devloop/issue/owner/repo/42/2026-06-03T01-02-03Z"),
    }

    local current = core.current_state(comments, proposal_id)
    t.eq(current.state, "ready")
    t.eq(current.version, "consensus:github-devloop/issue/owner/repo/42/2026-06-04T01-02-03Z")
  end,
  test_compare_transition_versions_timestampless_fallback_loses_to_real_timestamp = function()
    local incoming = "consensus:github-devloop/issue/owner/repo/42/v1"
    local current = "consensus:github-devloop/issue/owner/repo/42/2026-06-04T01-02-03Z"

    t.eq(core._compare_transition_versions(incoming, current) < 0, true)
    t.eq(core._compare_transition_versions(current, incoming) > 0, true)
  end,
  test_current_state_prefers_timestamped_marker_over_timestampless_marker = function()
    local proposal_id = "github-devloop/issue/owner/repo/42"
    local comments = {
      core.state_marker(proposal_id, "ready", "consensus:github-devloop/issue/owner/repo/42/2026-06-04T01-02-03Z"),
      core.state_marker(proposal_id, "blocked", "consensus:github-devloop/issue/owner/repo/42/v1"),
    }

    local current = core.current_state(comments, proposal_id)
    t.eq(current.state, "ready")
    t.eq(current.version, "consensus:github-devloop/issue/owner/repo/42/2026-06-04T01-02-03Z")
  end,
  test_reached_stays_true_after_later_phase_marker = function()
    local proposal_id = "github-devloop/issue/owner/repo/42"
    local impl_version = "ready/consensus-github-devloop/issue/owner/repo/42/2026-06-04T01-02-03Z"
    local comments = {
      core.state_marker(proposal_id, "pr-open", impl_version),
      core.state_marker(proposal_id, "reviewing", impl_version),
    }

    t.eq(core.current_state(comments, proposal_id).state, "reviewing")
    t.eq(core.reached(comments, proposal_id, "pr-open", {
      domain = "github-devloop-pr",
      lineage_base = impl_version,
    }), true)
  end,
  test_reached_is_false_before_matching_marker = function()
    local proposal_id = "github-devloop/issue/owner/repo/42"
    local impl_version = "ready/consensus-github-devloop/issue/owner/repo/42/2026-06-04T01-02-03Z"
    local comments = {
      core.state_marker("github-devloop/issue/owner/repo/99", "reviewing", impl_version),
    }

    t.eq(core.reached(comments, proposal_id, "pr-open", {
      domain = "github-devloop-pr",
      lineage_base = impl_version,
    }), false)
  end,
  test_reached_respects_proposal_lineage = function()
    local proposal_id = "github-devloop/issue/owner/repo/42"
    local other_proposal = "github-devloop/issue/owner/repo/43"
    local impl_version = "ready/consensus-github-devloop/issue/owner/repo/42/2026-06-04T01-02-03Z"
    local newer_version = impl_version .. "/fix/1"
    local comments = {
      core.state_marker(other_proposal, "merged", impl_version),
      core.state_marker(proposal_id, "reviewing", newer_version),
    }

    t.eq(core.reached(comments, proposal_id, "pr-open", {
      domain = "github-devloop-pr",
      lineage_base = impl_version,
    }), true)
    t.eq(core.reached(comments, proposal_id, "pr-open", {
      domain = "github-devloop-pr",
      lineage_base = "ready/consensus-github-devloop/issue/owner/repo/99/2026-06-04T01-02-03Z",
    }), false)
  end,
  test_devloop_gate_rejects_executable_or_metatable_smuggle_paths = function()
    local facts = gate.facts({
      reached = function()
        return true
      end,
      lineage_equals = function()
        return true
      end,
    })
    local ok_spec = gate.require_reached("pr-open", {
      domain = "github-devloop-pr",
      lineage = {
        proposal_id = true,
      },
    })

    t.eq(gate.holds(ok_spec, facts, { proposal_id = "github-devloop/issue/owner/repo/42" }), true)
    local callback_spec = {
      op = "reached",
      milestone = "pr-open",
      opts = {},
      raw = function()
        return true
      end,
    }
    local callback_ok = pcall(function()
      gate.holds(callback_spec, facts, {})
    end)
    t.eq(callback_ok, false)
    local metatable_spec = setmetatable({
      op = "reached",
      milestone = "pr-open",
      opts = {},
    }, {})
    local metatable_ok = pcall(function()
      gate.holds(metatable_spec, facts, {})
    end)
    t.eq(metatable_ok, false)
    local raw_table_spec = {
      op = "reached",
      milestone = "pr-open",
      opts = {},
      comments = {
        {
          body = core.state_marker("github-devloop/issue/owner/repo/42", "pr-open", "v1"),
        },
      },
    }
    local raw_table_ok = pcall(function()
      gate.holds(raw_table_spec, facts, {})
    end)
    t.eq(raw_table_ok, false)
  end,
  test_devloop_gate_rejects_sparse_all_lists_and_keeps_dense_false = function()
    local facts = gate.facts({
      reached = function(milestone)
        return tostring(milestone or "") == "ready"
      end,
      lineage_equals = function()
        return true
      end,
    })
    local dense = gate.all({
      gate.require_reached("ready"),
      gate.require_reached("pr-open"),
    })

    t.eq(gate.holds(dense, facts, {}), false)
    local sparse_ok = pcall(function()
      gate.all({
        [1] = gate.require_reached("ready"),
        [3] = gate.require_reached("pr-open"),
      })
    end)
    t.eq(sparse_ok, false)
    local raw_sparse_ok = pcall(function()
      gate.holds({
        op = "all",
        gates = {
          [1] = gate.require_reached("ready"),
          [3] = gate.require_reached("pr-open"),
        },
      }, facts, {})
    end)
    t.eq(raw_sparse_ok, false)
  end,
  test_devloop_gate_loads_gate_defs_in_restricted_sandbox = function()
    local spec = gate._load_gate_source_for_test([[
      return all({
        require_reached("pr-open", {
          domain = "github-devloop-pr",
          lineage = {
            proposal_id = true,
          },
        }),
      })
    ]])
    local facts = gate.facts({
      reached = function(milestone, opts)
        return tostring(milestone or "") == "pr-open"
          and tostring(opts and opts.domain or "") == "github-devloop-pr"
      end,
      lineage_equals = function(field, expected)
        return tostring(field or "") == "proposal_id"
          and tostring(expected or "") == "github-devloop/issue/owner/repo/42"
      end,
    })

    t.eq(gate.holds(spec, facts, { proposal_id = "github-devloop/issue/owner/repo/42" }), true)
  end,
  test_devloop_gate_load_gate_loads_child_start_visible = function()
    local spec = gate.load_gate("child_start_visible")
    local facts = gate.facts({
      reached = function(milestone, opts)
        return tostring(milestone or "") == "pr-open"
          and tostring(opts and opts.domain or "") == "github-devloop-pr"
      end,
      lineage_equals = function(field, expected)
        return ({
          proposal_id = "github-devloop/issue/owner/repo/42",
          issue_number = "42",
          impl_version = "ready/v1",
          branch = "feature/x",
          base_branch = "integration-ElonSG",
        })[field] == expected
      end,
    })

    t.eq(gate.holds(spec, facts, {
      proposal_id = "github-devloop/issue/owner/repo/42",
      issue_number = "42",
      impl_version = "ready/v1",
      branch = "feature/x",
      base_branch = "integration-ElonSG",
    }), true)
  end,
  test_devloop_gate_sandbox_rejects_reflection_and_loader_capabilities = function()
    local forbidden_sources = {
      [[
        local r = require
        r("debug")
        return require_reached("pr-open")
      ]],
      [[
        (require)("debug")
        return require_reached("pr-open")
      ]],
      [[
        return _G
      ]],
      [[
        return debug
      ]],
      [[
        return load("return 1")
      ]],
      [[
        return setmetatable({}, {})
      ]],
    }
    for _, source in ipairs(forbidden_sources) do
      local ok = pcall(function()
        gate._load_gate_source_for_test(source)
      end)
      t.eq(ok, false)
    end
  end,
  test_devloop_gate_sandbox_rejects_string_dump_paths = function()
    local forbidden_sources = {
      [[
        return string.dump(function()
          return 1
        end)
      ]],
      [[
        return require_reached(("").dump(function()
          return require, load, loadstring, _G
        end), {
          domain = "github-devloop-pr",
        })
      ]],
    }
    for _, source in ipairs(forbidden_sources) do
      local ok, err = pcall(function()
        gate._load_gate_source_for_test(source)
      end)
      t.eq(ok, false)
      t.is_true(tostring(err):find("restricted_lua", 1, true) ~= nil)
    end
  end,
  test_current_state_uses_stage_rank_for_same_issue_version = function()
    local proposal_id = "github-devloop/issue/owner/repo/42"
    local version = "consensus:github-devloop/issue/owner/repo/42/2026-06-04T01-02-03Z"
    local comments = {
      core.state_marker(proposal_id, "thinking", version),
      core.state_marker(proposal_id, "ready", version),
      core.state_marker(proposal_id, "blocked", version),
    }

    local current = core.current_state(comments, proposal_id)
    t.eq(current.state, "blocked")
    t.eq(current.stage_rank, core.stage_rank("blocked"))
  end,
  test_ready_marker_wins_same_version_tie_and_allows_implement_cas = function()
    local proposal_id = "github-devloop/issue/owner/repo/42"
    local version = "consensus:github-devloop/issue/owner/repo/42/2026-06-04T01-02-03Z"

    local current = core.current_state({
      core.state_marker(proposal_id, "ready", version),
      core.state_marker(proposal_id, "thinking", version),
    }, proposal_id)
    t.eq(core.stage_rank("ready") > core.stage_rank("thinking"), true)
    t.eq(current.state, "ready")
    t.eq(current.version, version)
    t.eq(current.stage_rank, core.stage_rank("ready"))

    local transition = core.versioned_transition_status(current, { "ready" }, "implementing", version)
    t.eq(transition, "apply")
    t.eq(core.cas_outcome(current, transition, version), "applied")
  end,
  test_stage_rank_does_not_override_different_versions = function()
    local proposal_id = "github-devloop/issue/owner/repo/42"
    local older_version = "consensus:github-devloop/issue/owner/repo/42/2026-06-04T01-02-03Z"
    local newer_version = "consensus:github-devloop/issue/owner/repo/42/2026-06-05T01-02-03Z"

    local current = core.current_state({
      core.state_marker(proposal_id, "blocked", older_version),
      core.state_marker(proposal_id, "ready", newer_version),
    }, proposal_id)

    t.eq(core.stage_rank("blocked") > core.stage_rank("ready"), true)
    t.eq(current.state, "ready")
    t.eq(current.version, newer_version)
  end,
  test_current_state_converges_same_version_review_conflict_to_fixing = function()
    local proposal_id = "github-devloop/issue/owner/repo/42"
    local version = "ready/consensus-github-devloop/issue/owner/repo/42/2026-06-04T01-02-03Z"

    local merge_ready_first = core.current_state({
      core.state_marker(proposal_id, "merge-ready", version),
      core.state_marker(proposal_id, "fixing", version),
    }, proposal_id)
    local fixing_first = core.current_state({
      core.state_marker(proposal_id, "fixing", version),
      core.state_marker(proposal_id, "merge-ready", version),
    }, proposal_id)

    t.eq(core.stage_rank("fixing") > core.stage_rank("merge-ready"), true)
    t.eq(merge_ready_first.state, "fixing")
    t.eq(fixing_first.state, "fixing")
  end,
  test_current_state_uses_stage_rank_for_version_equivalent_markers = function()
    local proposal_id = "github-devloop/issue/owner/repo/42"
    local slash_version = "ready/consensus/v1"
    local hyphen_version = "ready-consensus-v1"

    local current = core.current_state({
      core.state_marker(proposal_id, "reviewing", slash_version),
      core.state_marker(proposal_id, "pr-open", hyphen_version),
    }, proposal_id)

    t.eq(transition_version.safe_version_segment(slash_version), transition_version.safe_version_segment(hyphen_version))
    t.eq(core.stage_rank("reviewing") > core.stage_rank("pr-open"), true)
    t.eq(current.state, "reviewing")
    t.eq(current.version, slash_version)
  end,
  test_current_state_prefers_canonical_fix_round_over_generic_segment_order = function()
    local proposal_id = "github-devloop/issue/owner/repo/42"
    local base = "ready/consensus-github-devloop/issue/owner/repo/42/2026-06-04T01-02-03Z"
    local generic_would_win = base .. "/review-meta-action/9/fix/1"
    local canonical_winner = base .. "/fix/2"

    local current = core.current_state({
      core.state_marker(proposal_id, "review-meta", generic_would_win),
      core.state_marker(proposal_id, "fixing", canonical_winner),
    }, proposal_id)

    t.is_true(core.version_order_key(generic_would_win) > core.version_order_key(canonical_winner))
    t.eq(core.compare_state_marker_order({ state = "review-meta", version = generic_would_win }, "fixing", canonical_winner), -1)
    t.eq(current.state, "fixing")
    t.eq(current.version, canonical_winner)
  end,
  test_current_state_converges_same_version_fixing_to_review_meta = function()
    local proposal_id = "github-devloop/issue/owner/repo/42"
    local version = "ready/consensus-github-devloop/issue/owner/repo/42/2026-06-04T01-02-03Z"

    local fixing_first = core.current_state({
      core.state_marker(proposal_id, "fixing", version),
      core.state_marker(proposal_id, "review-meta", version),
    }, proposal_id)
    local meta_first = core.current_state({
      core.state_marker(proposal_id, "review-meta", version),
      core.state_marker(proposal_id, "fixing", version),
    }, proposal_id)

    t.eq(core.stage_rank("review-meta") > core.stage_rank("fixing"), true)
    t.eq(fixing_first.state, "review-meta")
    t.eq(meta_first.state, "review-meta")
  end,
  test_successful_fix_version_orders_after_fixing_for_any_sha = function()
    local proposal_id = "github-devloop/issue/owner/repo/42"
    local version = "ready/consensus-github-devloop/issue/owner/repo/42/2026-06-04T01-02-03Z"
    local new_version = core.next_fix_version(version)
    local sha_like_lower_version = "0000000000000000000000000000000000000000"

    local current = core.current_state({
      core.state_marker(proposal_id, "fixing", version),
      core.state_marker(proposal_id, "reviewing", new_version),
      m_builders.fix_marker(core, proposal_id, "github-devloop/pr-review/owner-repo-0000000000/7/v1/def456", "review", "def456", sha_like_lower_version),
    }, proposal_id)

    t.eq(core.version_fix_round(new_version), core.version_fix_round(version) + 1)
    t.eq(current.state, "reviewing")
    t.eq(current.version, new_version)
  end,
  test_version_loop_round_extracts_loop_with_trailing_fix_suffix = function()
    local base = "ready/consensus-github-devloop/issue/owner/repo/42/2026-06-04T01-02-03Z"
    t.eq(core.version_loop_round(base .. "/loop/2"), 2)
    t.eq(core.version_loop_round(base .. "/loop/2/fix/1"), 2)
    t.eq(core.version_loop_round(base .. "/fix/1"), 0)
  end,

  test_consensus_loop_result_orders_after_answered_intake_marker = function()
    local intake_version = "github-devloop/issue/owner/repo/42/intake/2485289059"
    local consensus_version = "consensus:" .. intake_version .. "/loop/5"
    local current = {
      state = "thinking",
      version = intake_version,
    }

    t.eq(core.versioned_transition_status(current, { "thinking" }, "ready", consensus_version), "apply")
    t.eq(core.compare_state_marker_order(current, "ready", consensus_version), -1)
    t.eq(core.current_state({
      core.state_marker("github-devloop/issue/owner/repo/42", "thinking", intake_version),
      core.state_marker("github-devloop/issue/owner/repo/42", "ready", consensus_version),
    }, "github-devloop/issue/owner/repo/42").state, "ready")
  end,

  test_fixing_version_matches_link_normalized_lineage = function()
    local base = "ready/consensus-github-devloop/issue/owner/repo/42/185/2026-06-10T13-45-26Z"
    local issue_version = base .. "/fix/1/fix/2/fix/3/fix/4/fix/5"
    local link_version = base .. "/fix/1/review-loop/2/rereview/2/feedface"
    t.eq(transition_version.strip_suffixes(issue_version), base)
    t.eq(transition_version.strip_suffixes(link_version), base)
    t.eq(core.fixing_version_matches_link(issue_version, link_version), true)
    t.eq(core.fixing_version_matches_link(issue_version, ""), false)
    t.eq(core.fixing_version_matches_link(issue_version, base:gsub("/42/", "/43/")), false)
  end,

  test_fixing_after_no_consensus_loop_outranks_reviewing = function()
    local proposal_id = "github-devloop/issue/owner/repo/42"
    local reviewing_version = "ready/consensus-github-devloop/issue/owner/repo/42/2026-06-04T01-02-03Z/loop/2"
    local fixing_version = core.next_fix_version(reviewing_version)

    local current = core.current_state({
      core.state_marker(proposal_id, "reviewing", reviewing_version),
      core.state_marker(proposal_id, "fixing", fixing_version),
    }, proposal_id)

    t.eq(fixing_version, reviewing_version .. "/fix/1")
    t.eq(current.state, "fixing")
    t.eq(current.version, fixing_version)
  end,
  test_review_meta_action_version_orders_after_review_meta_stage = function()
    local proposal_id = "github-devloop/issue/owner/repo/42"
    local version = "ready/consensus-github-devloop/issue/owner/repo/42/2026-06-04T01-02-03Z"
    local exit_version = core.next_review_meta_action_version(version)

    local current = core.current_state({
      core.state_marker(proposal_id, "review-meta", version),
      core.state_marker(proposal_id, "fixing", exit_version),
    }, proposal_id)

    t.eq(core.stage_rank("review-meta") > core.stage_rank("fixing"), true)
    t.eq(core.version_review_meta_action_round(exit_version), core.version_review_meta_action_round(version) + 1)
    t.eq(current.state, "fixing")
    t.eq(current.version, exit_version)
  end,
  test_review_loop_round_version_orders_after_base_reviewing = function()
    local proposal_id = "github-devloop/issue/owner/repo/42"
    local version = "ready/consensus-github-devloop/issue/owner/repo/42/2026-06-04T01-02-03Z"
    local review_loop_version = version .. "/review-loop/3"

    local current = core.current_state({
      core.state_marker(proposal_id, "reviewing", version),
      core.state_marker(proposal_id, "review-meta", review_loop_version),
    }, proposal_id)

    t.eq(core.version_review_loop_round(review_loop_version), 3)
    t.eq(current.state, "review-meta")
    t.eq(current.version, review_loop_version)
    t.eq(core.cyclic_transition_status(current, { "reviewing" }, "review-meta", version), "stale")
  end,
  test_current_state_uses_loop_round_before_stage_rank_for_same_updated_at = function()
    local proposal_id = "github-devloop/issue/owner/repo/42"
    local base = "consensus:github-devloop/issue/owner/repo/42/2026-06-04T01-02-03Z"
    local comments = {
      core.state_marker(proposal_id, "ready", base),
      core.state_marker(proposal_id, "blocked", base .. "/loop/2"),
    }

    local current = core.current_state(comments, proposal_id)
    t.eq(current.state, "blocked")
    t.eq(current.version, base .. "/loop/2")
  end,
  test_current_state_converges_same_version_ready_blocked_conflict_to_blocked = function()
    local proposal_id = "github-devloop/issue/owner/repo/42"
    local version = "consensus:github-devloop/issue/owner/repo/42/v1/loop/3"

    local ready_first = core.current_state({
      core.state_marker(proposal_id, "ready", version),
      core.state_marker(proposal_id, "blocked", version),
    }, proposal_id)
    local blocked_first = core.current_state({
      core.state_marker(proposal_id, "blocked", version),
      core.state_marker(proposal_id, "ready", version),
    }, proposal_id)

    t.eq(ready_first.state, "blocked")
    t.eq(blocked_first.state, "blocked")
  end,
  test_current_state_converges_same_version_terminal_conflict_to_blocked = function()
    local proposal_id = "github-devloop/issue/owner/repo/42"
    local version = "ready/consensus-github-devloop/issue/owner/repo/42/v1"

    local failed_first = core.current_state({
      core.state_marker(proposal_id, "impl-failed", version),
      core.state_marker(proposal_id, "blocked", version),
    }, proposal_id)
    local blocked_first = core.current_state({
      core.state_marker(proposal_id, "blocked", version),
      core.state_marker(proposal_id, "impl-failed", version),
    }, proposal_id)

    t.eq(core.stage_rank("blocked") > core.stage_rank("impl-failed"), true)
    t.eq(failed_first.state, "blocked")
    t.eq(blocked_first.state, "blocked")
  end,
  test_current_state_ignores_non_bot_authored_marker = function()
    local proposal_id = "github-devloop/issue/owner/repo/42"
    local comments = {
      {
        body = core.state_marker(proposal_id, "ready", "v2"),
        author_login = "ordinary-user",
      },
      {
        body = core.state_marker(proposal_id, "thinking", "v1"),
        author_login = devloop_base.trusted_bot_login(),
      },
    }
    local current = core.current_state(comments, proposal_id)
    t.eq(current.state, "thinking")
    t.eq(current.version, "v1")
  end,
  test_current_state_ignores_authorless_state_marker = function()
    local proposal_id = "github-devloop/issue/owner/repo/42"
    devloop_base.configure_trusted_bot_login(nil)
    local parsed = parsers_issue.parse_issue_view_state(core, '{"comments":[{"body":"'
      .. core.state_marker(proposal_id, "ready", "v2"):gsub('"', '\\"')
      .. '","author":null},{"body":"'
      .. core.state_marker(proposal_id, "thinking", "v1"):gsub('"', '\\"')
      .. '","author":{"login":"'
      .. devloop_base.trusted_bot_login()
      .. '"}}]}')

    local current = core.current_state(parsed.comments, proposal_id)
    t.eq(current.state, "thinking")
    t.eq(current.version, "v1")
  end,
  test_untrusted_comment_text_neutralizes_fkst_markers = function()
    local proposal_id = "github-devloop/issue/owner/repo/42"
    local forged = core.state_marker(proposal_id, "blocked", "consensus:github-devloop/issue/owner/repo/42/2099-01-01T00-00-00Z")
    local proxy_marker = "<!-- fkst:github-proxy:comment:future-dedup -->"
    local neutralized = devloop_base.neutralize_untrusted_comment_text("Before\n" .. forged .. "\n" .. proxy_marker .. "\nAfter")

    t.is_true(neutralized:find("&lt;!-- fkst:github-devloop:state:v1", 1, true) ~= nil)
    t.is_true(neutralized:find("&lt;!-- fkst:github-proxy:comment:future-dedup", 1, true) ~= nil)
    t.eq(neutralized:find(forged, 1, true) == nil, true)
    t.eq(neutralized:find(proxy_marker, 1, true) == nil, true)
    t.is_nil(core.current_state({ neutralized }, proposal_id).state)
  end,
  test_result_comment_neutralizes_untrusted_body_marker_before_real_marker = function()
    local proposal_id = "github-devloop/issue/owner/repo/42"
    local forged_version = "consensus:github-devloop/issue/owner/repo/42/2099-01-01T00-00-00Z"
    local forged = core.state_marker(proposal_id, "blocked", forged_version)
    local event = reached({
      body = "Looks fine.\n" .. forged,
      dedup_key = "consensus:github-devloop/issue/owner/repo/42/2026-06-03T01-02-03Z",
    })
    local comment = requests_lifecycle.build_result_comment_request(core, "owner/repo", "42", event)

    t.is_true(comment.body:find("&lt;!-- fkst:github-devloop:state:v1", 1, true) ~= nil)
    t.eq(comment.body:find(forged, 1, true) == nil, true)
    local current = core.current_state({ comment.body }, proposal_id)
    t.eq(current.state, "ready")
    t.eq(current.version, event.dedup_key)
  end,
  test_reconcile_comment_neutralizes_untrusted_reason_marker_before_real_marker = function()
    local proposal_id = "github-devloop/issue/owner/repo/42"
    local base_version = "consensus:github-devloop/issue/owner/repo/42/2026-06-03T01-02-03Z"
    local event = conv_reconcile.build_devloop_reconcile_payload(core, unresolved(), 3, base_version)
    local forged_version = base_version .. "/loop/99"
    local forged = core.state_marker(proposal_id, "blocked", forged_version)
    local comment = core.build_reconcile_comment_request("owner/repo", "42", event, "drop", "Reason\n" .. forged)

    t.is_true(comment.body:find("&lt;!-- fkst:github-devloop:state:v1", 1, true) ~= nil)
    t.eq(comment.body:find(forged, 1, true) == nil, true)
    local current = core.current_state({ comment.body }, proposal_id)
    t.eq(current.state, "blocked")
    t.eq(current.version, base_version .. "/loop/3")
  end,
}
