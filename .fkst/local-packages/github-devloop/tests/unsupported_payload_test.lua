local devloop_base = require("devloop.base")
local payloads_builders = require("devloop.payloads.builders")
local conv_reconcile = require("devloop.convergence.reconcile")
local t = fkst.test
local core = require("core")
local execution_start = require("devloop.execution_start")
local queue = require("devloop.queue")
local m_mq = require("devloop.merge_queue")

local package_root = "packages/github-devloop"

local function department_paths()
  local root = package_root
  local result = {}
  local find = assert(io.popen("find " .. root .. "/departments -mindepth 2 -maxdepth 2 -name main.lua | sort"))
  for path in find:lines() do
    local rel = path:sub(#root + 2)
    table.insert(result, rel)
  end
  local ok = find:close()
  if ok == false then
    error("github-devloop: department discovery failed")
  end
  return result
end

local function read_file(path)
  local handle = assert(io.open(path, "r"))
  local body = handle:read("*a")
  handle:close()
  return body
end

local function department_source(path)
  return read_file(package_root .. "/" .. path)
end

local function load_department_spec(path)
  local old_pipeline = pipeline
  local module = require(tostring(path):gsub("/", "."):gsub("%.lua$", ""))
  pipeline = old_pipeline
  if type(module) ~= "table" or type(module.spec) ~= "table" then
    error("github-devloop: department spec missing for " .. tostring(path))
  end
  return module.spec
end

local function production_queue_name(queue)
  if tostring(queue):find("%.", 1, false) ~= nil then
    return queue
  end
  return "github-devloop." .. tostring(queue)
end

local function issue_consensus_payload()
  return {
    schema = "consensus.consensus_reached.v1",
    proposal_id = "github-devloop/issue/owner/repo/42",
    decision = "approve",
    body = "All angles approve.",
    dedup_key = "consensus:github-devloop/issue/owner/repo/42/2026-06-03T01-02-03Z",
    source_ref = { kind = "external", ref = "owner/repo#issue/42" },
  }
end

local function review_proposal_id()
  return devloop_base.pr_review_proposal_id("owner/repo", 7, "ready/consensus-github-devloop/issue/owner/repo/42/2026-06-03T01-02-03Z", "def456")
end

local function review_consensus_payload()
  local proposal_id = review_proposal_id()
  return {
    schema = "consensus.consensus_reached.v1",
    proposal_id = proposal_id,
    decision = "approve",
    body = "All angles approve.",
    dedup_key = "consensus:" .. proposal_id .. "/review",
    source_ref = { kind = "external", ref = "owner/repo#pr/7" },
  }
end

local function issue_unresolved_payload()
  return {
    schema = "consensus.consensus_converge.v1",
    proposal_id = "github-devloop/issue/owner/repo/42",
    dedup_key = "consensus:github-devloop/issue/owner/repo/42/2026-06-03T01-02-03Z",
    source_ref = { kind = "external", ref = "owner/repo#issue/42" },
  }
end

local function review_unresolved_payload()
  local proposal_id = review_proposal_id()
  return {
    schema = "consensus.consensus_converge.v1",
    proposal_id = proposal_id,
    dedup_key = "consensus:" .. proposal_id .. "/review",
    source_ref = { kind = "external", ref = "owner/repo#pr/7" },
  }
end

local function issue_entity_payload()
  return {
    schema = "github-proxy.v1",
    type = "issue",
    repo = "owner/repo",
    number = 42,
    title = "Implement decision recorder",
    state = "OPEN",
    updated_at = "2026-06-03T01:02:03Z",
    labels = { "fkst-dev:enabled" },
    dedup_key = "owner/repo#issue#42@2026-06-03T01:02:03Z",
    source_ref = { kind = "external", ref = "owner/repo#issue/42" },
  }
end

local function pr_entity_payload()
  return {
    schema = "github-proxy.v1",
    type = "pr",
    repo = "owner/repo",
    number = 7,
    title = "Implement decision recorder",
    state = "OPEN",
    updated_at = "2026-06-03T01:02:03Z",
    dedup_key = "owner/repo#pr#7@2026-06-03T01:02:03Z",
    source_ref = { kind = "external", ref = "owner/repo#pr/7" },
  }
end

local function run_department_with_logs(path, event)
  local result = t.run_department(path, event)
  t.is_true(type(result) == "table")
  return result.exit_code == 0, tostring(result.error or ""), table.concat({
    tostring(result.error or ""),
  }, "\n")
end

local function branch_tick_payload()
  return { schema = "github-devloop.branch-tick.v1" }
end

local function payload_for_queue(queue)
  local payloads = {
    ["consensus.consensus_converge"] = issue_unresolved_payload(),
    ["consensus.consensus_reached"] = issue_consensus_payload(),
    dead_letter = {
      delivery_id = "delivery/v1/raised/queue/github-devloop.devloop_ready/dept/github-devloop.implement/01HY",
      queue = "github-devloop.devloop_ready",
      dept = "github-devloop.implement",
      dedup_key = "dead-letter-test",
      attempt = 1,
      error = "test error",
      source_ref = { kind = "external", ref = "owner/repo#issue/42" },
    },
    devloop_doctor_tick = { schema = "github-devloop.doctor-tick.v1" },
    devloop_ensure_repo_tick = { schema = "github-devloop.ensure-repo-tick.v1" },
    devloop_execute_request = execution_start.build_execution_request_payload({
      proposal_id = "github-devloop/issue/owner/repo/42",
      dedup_key = "intake/github-devloop/issue/owner/repo/42/2026-06-03T01-02-03Z",
      source_ref = { kind = "external", ref = "owner/repo#issue/42" },
      origin = {
        package = "github-devloop-intake-default",
        route = "default",
        decision = "enable",
      },
      service_class = "standard",
    }),
    devloop_fix_reconcile = conv_reconcile.build_devloop_fix_reconcile_payload(core, {
      proposal_id = "github-devloop/issue/owner/repo/42",
      review_proposal_id = devloop_base.pr_review_proposal_id("owner/repo", 7, "ready/consensus-github-devloop/issue/owner/repo/42/2026-06-03T01-02-03Z/fix/3", "def456"),
      review_dedup_key = "consensus:" .. devloop_base.pr_review_proposal_id("owner/repo", 7, "ready/consensus-github-devloop/issue/owner/repo/42/2026-06-03T01-02-03Z/fix/3", "def456") .. "/review",
      reviewed_head_sha = "def456",
      pr_number = 7,
      source_ref = { kind = "external", ref = "owner/repo#pr/7" },
    }, "ready/consensus-github-devloop/issue/owner/repo/42/2026-06-03T01-02-03Z/fix/3"),
    devloop_fixing = {
      schema = "github-devloop.fixing.v1",
      proposal_id = "github-devloop/issue/owner/repo/42",
      pr_number = 7,
      version = "ready/consensus-github-devloop/issue/owner/repo/42/2026-06-03T01-02-03Z/fix/1",
      review_proposal_id = devloop_base.pr_review_proposal_id("owner/repo", 7, "ready/consensus-github-devloop/issue/owner/repo/42/2026-06-03T01-02-03Z", "def456"),
      review_dedup_key = "consensus:" .. devloop_base.pr_review_proposal_id("owner/repo", 7, "ready/consensus-github-devloop/issue/owner/repo/42/2026-06-03T01-02-03Z", "def456") .. "/review",
      reviewed_head_sha = "def456",
      blocking_gap = "missing regression guard",
      dedup_key = "fixing/github-devloop/issue/owner/repo/42/v1",
      source_ref = { kind = "external", ref = "owner/repo#pr/7" },
    },
    devloop_liveness_tick = { schema = "github-devloop.tick.v1" },
    devloop_merge_queue_tick = m_mq.merge_queue_tick_payload(core, "owner/repo", 6, {
      proposal_id = "github-devloop/issue/owner/repo/42",
      pr_number = 7,
      version = "ready/consensus-github-devloop/issue/owner/repo/42/2026-06-03T01-02-03Z",
      review_proposal_id = review_proposal_id(),
      review_dedup_key = "consensus:" .. review_proposal_id() .. "/review",
      reviewed_head_sha = "def456",
      head_sha = "def456",
      source_ref = { kind = "external", ref = "owner/repo#pr/7" },
    }),
    devloop_merge_ready = payloads_builders.build_devloop_merge_ready_payload(core, "github-devloop/issue/owner/repo/42", 7, "ready/consensus-github-devloop/issue/owner/repo/42/2026-06-03T01-02-03Z", {
      review_proposal_id = devloop_base.pr_review_proposal_id("owner/repo", 7, "ready/consensus-github-devloop/issue/owner/repo/42/2026-06-03T01-02-03Z", "def456"),
      review_dedup_key = "consensus:" .. devloop_base.pr_review_proposal_id("owner/repo", 7, "ready/consensus-github-devloop/issue/owner/repo/42/2026-06-03T01-02-03Z", "def456") .. "/review",
      reviewed_head_sha = "def456",
    }, { kind = "external", ref = "owner/repo#pr/7" }),
    devloop_observe_tick = { schema = "github-devloop.observe-tick.v1" },
    devloop_ready = {
      schema = "github-devloop.ready.v1",
      proposal_id = "github-devloop/issue/owner/repo/42",
      dedup_key = "ready/consensus-github-devloop/issue/owner/repo/42/2026-06-03T01-02-03Z",
      source_ref = { kind = "external", ref = "owner/repo#issue/42" },
    },
    devloop_ready_session = payloads_builders.build_devloop_ready_payload(core, {
      proposal_id = "github-devloop/issue/owner/repo/42",
      dedup_key = "consensus:github-devloop/issue/owner/repo/42/2026-06-03T01-02-03Z",
      source_ref = { kind = "external", ref = "owner/repo#issue/42" },
      include_ready_hand_off = true,
    }),
    devloop_reconcile = conv_reconcile.build_devloop_reconcile_payload(core, {
      schema = "consensus.consensus_converge.v1",
      proposal_id = "github-devloop/issue/owner/repo/42",
      dedup_key = "consensus:github-devloop/issue/owner/repo/42/2026-06-03T01-02-03Z/loop/3",
      source_ref = { kind = "external", ref = "owner/repo#issue/42" },
    }, 3, "consensus:github-devloop/issue/owner/repo/42/2026-06-03T01-02-03Z"),
    devloop_review_meta = payloads_builders.build_devloop_review_meta_payload(core, {
      schema = "consensus.consensus_converge.v1",
      proposal_id = review_proposal_id(),
      dedup_key = "consensus:" .. review_proposal_id() .. "/review/loop/2",
      source_ref = { kind = "external", ref = "owner/repo#pr/7" },
    }, "github-devloop/issue/owner/repo/42", "ready/consensus-github-devloop/issue/owner/repo/42/2026-06-03T01-02-03Z", 7, 3),
    devloop_review_reconcile = conv_reconcile.build_devloop_review_reconcile_payload(core, {
      schema = "consensus.consensus_converge.v1",
      proposal_id = review_proposal_id(),
      dedup_key = "consensus:" .. review_proposal_id() .. "/review/loop/3",
      round = 3,
      source_ref = { kind = "external", ref = "owner/repo#pr/7" },
    }, 3, "github-devloop/issue/owner/repo/42", "ready/consensus-github-devloop/issue/owner/repo/42/2026-06-03T01-02-03Z", "def456"),
    devloop_reviewing = {
      schema = "github-devloop.reviewing.v1",
      proposal_id = "github-devloop/issue/owner/repo/42",
      pr_number = 7,
      version = "ready/consensus-github-devloop/issue/owner/repo/42/2026-06-03T01-02-03Z",
      dedup_key = "reviewing/github-devloop/issue/owner/repo/42/ready-consensus-github-devloop-issue-owner-repo-42-2026-06-03T01-02-03Z/7",
      source_ref = { kind = "external", ref = "owner/repo#pr/7" },
    },
    board_digest_probe = {
      mode = "block",
      repo = "owner/repo",
      tick = "2026-06-10T02:12:03Z",
    },
    cache_seed = {
      key = "github-devloop/test-cache-key",
      value = "test-cache-value",
    },
    context_bundle_probe = {
      mode = "round_trip",
      root = "/tmp/fkst-packages-test/github-devloop-unsupported-context-bundle",
    },
    devloop_timeout_reconcile = conv_reconcile.build_devloop_timeout_reconcile_payload(core, {
      from_state = "ready",
    }, {
      version = "ready/consensus-github-devloop/issue/owner/repo/42/2026-06-03T01-02-03Z/timeout/ready/1",
    }, "github-devloop/issue/owner/repo/42", { kind = "external", ref = "owner/repo#issue/42" }, 1),
    ["github-proxy.github_comment_written"] = {
      schema = "github-proxy.comment-written.v1",
      repo = "owner/repo",
      target = "issue",
      issue_number = 42,
      comment_id = "123456",
      request_dedup_key = "github-devloop/issue/owner/repo/42/comment/approve/consensus-github-devloop/issue/owner/repo/42/2026-06-03T01-02-03Z",
      dedup_key = "github-devloop/issue/owner/repo/42/comment/approve/written/123456",
      source_ref = { kind = "external", ref = "owner/repo#issue/42" },
      handoff = {
        kind = "github-devloop.ready",
        proposal_id = "github-devloop/issue/owner/repo/42",
        version = "consensus:github-devloop/issue/owner/repo/42/2026-06-03T01-02-03Z",
        marker_version = "consensus:github-devloop/issue/owner/repo/42/2026-06-03T01-02-03Z",
        source_ref = { kind = "external", ref = "owner/repo#issue/42" },
      },
    },
    ["github-proxy.github_entity_changed"] = issue_entity_payload(),
    devloop_observe_issue = issue_entity_payload({ source = "liveness-scan" }),
  }
  local payload = payloads[queue]
  if payload == nil then
    error("github-devloop: no production-shaped queue fixture for " .. tostring(queue))
  end
  return payload
end

local function payload_for_department_queue(path, queue)
  return payload_for_queue(queue)
end

local function assert_no_unsupported_queue_fallthrough(path, queue, ok, err, logs)
  local text = tostring(err or "") .. "\n" .. tostring(logs or "")
  if text:find("consumed-queue-unrouted", 1, true) ~= nil then
    error("github-devloop: consumed queue is unrouted for " .. path .. " queue=" .. queue .. ": " .. text)
  end
  if text:find("unsupported event payload", 1, true) ~= nil
    or text:find("unsupported sync conflict payload", 1, true) ~= nil
    or text:find("skip-foreign(payload)", 1, true) ~= nil
    or text:find("skip-foreign(pr)", 1, true) ~= nil
    or text:find("skip-foreign(proposal_id)", 1, true) ~= nil
    or text:find("skip-foreign(source_ref)", 1, true) ~= nil then
    error("github-devloop: production-shaped consumed queue fell through unsupported path for " .. path .. " queue=" .. queue .. ": " .. text)
  end
end

local cases = {
  {
    dept = "loop",
    path = "departments/loop/main.lua",
    queue = "consensus.consensus_converge",
  },
  {
    dept = "implement",
    path = "departments/implement/main.lua",
    queue = "devloop_ready",
  },
  {
    dept = "consensus_result",
    path = "departments/consensus_result/main.lua",
    queue = "consensus.consensus_reached",
  },
  {
    dept = "observe_issue",
    path = "departments/observe_issue/main.lua",
    queue = "github-proxy.github_entity_changed",
  },
  {
    dept = "observe_issue",
    path = "departments/observe_issue/main.lua",
    queue = "devloop_observe_issue",
  },
  {
    dept = "reconcile",
    path = "departments/reconcile/main.lua",
    queue = "devloop_reconcile",
  },
}

return {
  test_consumed_queue_dispatch_accepts_namespaced_declared_queues = function()
    local routed = {}
    local spec = {
      consumes = { "devloop_ready", "devloop_ready_session" },
    }
    local handled = queue.dispatch_consumed_queue("test", spec, {
      queue = "github-devloop.devloop_ready_session",
      payload = {},
    }, {
      devloop_ready = function()
        table.insert(routed, "ready")
      end,
      devloop_ready_session = function()
        table.insert(routed, "ready-session")
      end,
    })

    t.eq(handled, true)
    t.eq(routed[1], "ready-session")
  end,

  test_consumed_queue_dispatch_fail_closed_when_declared_queue_is_unrouted = function()
    t.raises(function()
      queue.dispatch_consumed_queue("test", {
        consumes = { "devloop_ready", "devloop_ready_session" },
      }, {
        queue = "github-devloop.devloop_ready_session",
        payload = {},
      }, {
        devloop_ready = function() end,
      })
    end)
  end,

  test_consumed_queue_dispatch_skips_foreign_queue_without_error = function()
    local handled = queue.dispatch_consumed_queue("test", {
      consumes = { "devloop_ready" },
    }, {
      queue = "github-proxy.github_entity_changed",
      payload = {},
    }, {
      devloop_ready = function()
        error("github-devloop: unexpected foreign dispatch")
      end,
    })

    t.eq(handled, false)
  end,

  test_event_queue_matches_namespaced_session_queue = function()
    t.eq(queue.event_queue_matches({ queue = "github-devloop.devloop_ready_session" }, "devloop_ready_session"), true)
    t.eq(queue.event_queue_matches({ queue = "devloop_ready_session" }, "devloop_ready_session"), true)
    t.eq(queue.event_queue_matches({ queue = "github-devloop.devloop_ready" }, "devloop_ready_session"), false)
  end,

  test_all_departments_accept_production_namespaced_consumed_queues = function()
    for _, path in ipairs(department_paths()) do
      local spec = load_department_spec(path)
      for _, queue in ipairs(spec.consumes or {}) do
        local event = {
          queue = production_queue_name(queue),
          payload = payload_for_department_queue(path, queue),
        }
        local ok, err, logs = run_department_with_logs(path, event)
        assert_no_unsupported_queue_fallthrough(path, queue, ok, err, logs)
      end
    end
  end,

  test_observers_fail_closed_for_declared_but_unrouted_queue = function()
    for _, path in ipairs({
      "departments/observe_issue/main.lua",
    }) do
      local module = require(tostring(path):gsub("/", "."):gsub("%.lua$", ""))
      table.insert(module.spec.consumes, "devloop_unrouted_probe")

      local ok, err = pcall(function()
        module.pipeline({
          queue = "github-devloop.devloop_unrouted_probe",
          payload = {},
        })
      end)

      t.eq(ok, false)
      t.is_true(tostring(err):find("consumed%-queue%-unrouted") ~= nil)
    end
  end,

  test_unsupported_payload_consumers_skip_non_table_payloads = function()
    for _, case in ipairs(cases) do
      for _, payload in ipairs({ false, "foreign-payload", 42 }) do
        local result = t.run_department(case.path, {
          queue = case.queue,
          payload = payload,
        })

        t.eq(result.exit_code, 0)
        t.eq(#result.raises, 0)
      end
    end
  end,

  test_payload_field_returns_nil_for_userdata = function()
    local userdata_payload = assert(io.tmpfile())
    t.eq(core.payload_field(userdata_payload, "dedup_key"), nil)
    userdata_payload:close()
  end,
}
