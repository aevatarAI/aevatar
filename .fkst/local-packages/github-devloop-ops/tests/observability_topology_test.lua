local h = require("tests.devloop_ops_core_helpers")
local core = h.core
local t = h.t
require("departments.observability.main")
local topology = require("departments.observability.topology")

local function split_id(canonical)
  local package, name = tostring(canonical):match("^([^%.]+)%.(.+)$")
  return package or "", name or canonical
end

local function graph_builder()
  local nodes = {}
  local edges = {}
  local queues = {}

  local function queue(canonical)
    if queues[canonical] then
      return
    end
    queues[canonical] = true
    local package, name = split_id(canonical)
    table.insert(nodes, {
      kind = "queue",
      id = "queue:" .. canonical,
      name = name,
      package = package,
      fanout = false,
    })
  end

  local function raiser(canonical, produces)
    local package, name = split_id(canonical)
    queue(produces)
    table.insert(nodes, {
      kind = "raiser",
      id = "raiser:" .. canonical,
      name = name,
      package = package,
      source = { type = "cron", interval = "5m" },
    })
    table.insert(edges, {
      from = "raiser:" .. canonical,
      to = "queue:" .. produces,
      relation = "raises",
    })
  end

  local function department(canonical, consumes, produces)
    local package, name = split_id(canonical)
    table.insert(nodes, {
      kind = "department",
      id = "department:" .. canonical,
      name = name,
      package = package,
      consumes = consumes or {},
      produces = produces or {},
      ephemeral = {},
      stall_window = "30s",
    })
    for _, consumed in ipairs(consumes or {}) do
      queue(consumed)
      table.insert(edges, {
        from = "queue:" .. consumed,
        to = "department:" .. canonical,
        relation = "consumes",
      })
    end
    for _, produced in ipairs(produces or {}) do
      queue(produced)
      table.insert(edges, {
        from = "department:" .. canonical,
        to = "queue:" .. produced,
        relation = "produces",
      })
    end
  end

  return {
    raiser = raiser,
    department = department,
    graph = function()
      return {
        schema = "fkst.graph.v1",
        nodes = nodes,
        edges = edges,
      }
    end,
  }
end

local function topology_fixture()
  local b = graph_builder()

  b.raiser("github-proxy.github_poll", "github-proxy.github_poll_tick")
  b.raiser("branch-topology.branch_poll", "branch-topology.devloop_branch_tick")
  b.raiser("github-devloop.merge_queue_poll", "github-devloop.devloop_merge_queue_tick")
  b.raiser("github-devloop-ops.observability_poll", "github-devloop-ops.devloop_observe_tick")
  b.raiser("github-devloop.liveness_poll", "github-devloop.devloop_liveness_tick")
  b.raiser("github-devloop-ops.doctor_poll", "github-devloop-ops.devloop_doctor_tick")
  b.raiser("github-devloop-ops.ensure_repo_poll", "github-devloop-ops.devloop_ensure_repo_tick")
  b.raiser("fkst-substrate-ref-maintainer.substrate_ref_poll", "fkst-substrate-ref-maintainer.devloop_substrate_ref_tick")

  b.department("github-proxy.github_poll", { "github-proxy.github_poll_tick" }, { "github-proxy.github_entity_changed" })
  b.department("github-proxy.github_comment", { "github-proxy.github_issue_comment_request" }, { "github-proxy.github_comment_written" })
  b.department("github-proxy.github_pr_comment", { "github-proxy.github_pr_comment_request" }, { "github-proxy.github_comment_written" })
  b.department("github-proxy.github_issue_label", { "github-proxy.github_issue_label_request" }, {})
  b.department("github-proxy.github_issue_create", { "github-proxy.github_issue_create_request" }, { "github-proxy.github_issue_blocked_by_request" })
  b.department("github-proxy.github_issue_blocked_by", { "github-proxy.github_issue_blocked_by_request" }, {})

  b.department("consensus.decide", { "consensus.proposal" }, { "consensus.consensus_reached", "consensus.consensus_converge" })
  b.department("consensus.dead_letter", { "consensus.dead_letter" }, {})

  b.department("github-devloop.observe_issue", { "github-proxy.github_entity_changed" }, {
    "consensus.proposal",
    "github-devloop.devloop_ready",
    "github-devloop.devloop_reviewing",
    "github-devloop.devloop_fixing",
    "github-devloop-decompose.devloop_decompose",
    "github-devloop.devloop_merge_ready",
    "github-devloop.devloop_reconcile",
    "github-devloop.devloop_review_reconcile",
    "github-devloop.devloop_timeout_reconcile",
  })
  b.department("github-devloop.observe_pr", { "github-proxy.github_entity_changed" }, {
    "github-devloop.devloop_reviewing",
    "github-devloop.devloop_fixing",
    "github-devloop-decompose.devloop_decompose",
    "github-devloop.devloop_merge_ready",
    "github-devloop.devloop_reconcile",
    "github-devloop.devloop_review_reconcile",
    "github-devloop.devloop_timeout_reconcile",
  })
  b.department("github-devloop-intake.admission", { "github-proxy.github_entity_changed" }, { "github-devloop-intake.devloop_intake_candidate" })
  b.department("github-devloop-intake.intake_judge", { "github-devloop-intake.devloop_intake_candidate" }, { "github-devloop.devloop_execute_request" })
  b.department("github-devloop.execute_start", { "github-devloop.devloop_execute_request" }, { "consensus.proposal" })
  b.department("github-devloop.consensus_result", { "consensus.consensus_reached" }, { "github-proxy.github_issue_comment_request" })
  b.department("github-devloop.comment_handoff", { "github-proxy.github_comment_written" }, { "github-devloop.devloop_ready", "github-devloop.devloop_reviewing" })
  b.department("github-devloop.implement", { "github-devloop.devloop_ready" }, { "github-devloop.devloop_reviewing" })
  b.department("github-devloop.review_pr", { "github-devloop.devloop_reviewing" }, { "consensus.proposal" })
  b.department("github-devloop.review_result", { "consensus.consensus_reached" }, { "github-devloop.devloop_merge_ready", "github-devloop.devloop_fixing" })
  b.department("github-devloop.merge", { "github-devloop.devloop_merge_ready", "github-devloop.devloop_merge_queue_tick" }, {
    "github-devloop.devloop_reviewing",
    "github-devloop.devloop_fixing",
    "github-devloop.devloop_merge_queue_tick",
  })
  b.department("branch-topology.rollup_scan", { "branch-topology.devloop_branch_tick" }, { "branch-topology.devloop_rollup_ready" })
  b.department("branch-topology.rollup_merge", { "branch-topology.devloop_rollup_ready" }, {})

  b.department("github-devloop-ops.dead_letter", { "github-devloop-ops.dead_letter" }, { "github-proxy.github_issue_create_request" })
  b.department("github-devloop-decompose.decompose", { "github-devloop-decompose.devloop_decompose" }, { "github-proxy.github_issue_create_request" })
  b.department("github-devloop-ops.doctor", { "github-devloop-ops.devloop_doctor_tick" }, {})
  b.department("github-devloop-ops.ensure_repo", { "github-devloop-ops.devloop_ensure_repo_tick" }, {})
  b.department("github-devloop.fix", { "github-devloop.devloop_fixing" }, { "github-devloop.devloop_reviewing", "github-devloop.devloop_review_meta" })
  b.department("github-devloop.liveness_scan", { "github-devloop.devloop_liveness_tick" }, { "github-devloop.devloop_observe_redrive", "consensus.proposal" })
  b.department("github-devloop.loop", { "consensus.consensus_converge" }, { "consensus.proposal", "github-devloop.devloop_reconcile" })
  b.department("github-devloop-ops.observability", { "github-devloop-ops.devloop_observe_tick" }, { "github-proxy.github_issue_create_request" })
  b.department("branch-topology.pr_freshness_scan", { "branch-topology.devloop_branch_tick" }, { "branch-topology.devloop_sync_conflict" })
  b.department("github-devloop.reconcile", {
    "github-devloop.devloop_reconcile",
    "github-devloop.devloop_review_reconcile",
    "github-devloop.devloop_fix_reconcile",
    "github-devloop.devloop_timeout_reconcile",
  }, { "github-proxy.github_issue_comment_request", "github-proxy.github_pr_comment_request", "github-proxy.github_issue_label_request" })
  b.department("github-devloop.review_loop", { "consensus.consensus_converge" }, {
    "consensus.proposal",
    "github-devloop.devloop_review_meta",
    "github-devloop.devloop_review_reconcile",
  })
  b.department("github-devloop.review_meta", { "github-devloop.devloop_review_meta" }, {
    "github-devloop.devloop_fixing",
    "github-proxy.github_issue_create_request",
  })
  b.department("fkst-substrate-ref-maintainer.substrate_ref_scan", { "fkst-substrate-ref-maintainer.devloop_substrate_ref_tick" }, { "github-proxy.github_pr_comment_request" })
  b.department("branch-topology.sync_conflict", { "branch-topology.devloop_sync_conflict" }, { "github-proxy.github_issue_create_request" })
  b.department("branch-topology.sync_scan", { "branch-topology.devloop_branch_tick" }, { "branch-topology.devloop_sync_conflict" })

  return b.graph()
end

local function clone_array(values)
  local copy = {}
  for index = #values, 1, -1 do
    table.insert(copy, values[index])
  end
  return copy
end

local function permuted_graph(graph)
  return {
    schema = graph.schema,
    nodes = clone_array(graph.nodes),
    edges = clone_array(graph.edges),
  }
end

local function count_literal(haystack, needle)
  local count = 0
  local start = 1
  while true do
    local found = tostring(haystack or ""):find(needle, start, true)
    if found == nil then
      return count
    end
    count = count + 1
    start = found + #needle
  end
end

local function assert_ops_departments_do_not_produce_devloop_lifecycle_queues(graph)
  for _, node in ipairs(graph.nodes or {}) do
    if node.kind == "department" and node.package == "github-devloop-ops" then
      for _, produced in ipairs(node.produces or {}) do
        t.is_true(tostring(produced):match("^github%-devloop%.devloop_") == nil)
      end
    end
  end
end

return {
  test_observability_declares_graph_json_authorization = function()
    local module = require("departments.observability.main")

    t.eq(module.spec.graph_json, true)
    t.eq(module.spec.consumes[1], "devloop_observe_tick")
    t.eq(module.spec.retry, false)
    t.eq(module.spec.stall_window, "2m")
  end,

  test_topology_derives_unknown_department_lane_and_edges_from_graph = function()
    local b = graph_builder()
    b.raiser("new-package.alarm_poll", "new-package.alarm_tick")
    b.department("new-package.alpha_one", { "new-package.alarm_tick" }, { "new-package.work_ready" })
    b.department("new-package.beta_two", { "new-package.work_ready" }, {})

    local mermaid = topology.render_mermaid(b.graph())

    t.is_true(mermaid:find("subgraph lane_", 1, true) ~= nil)
    t.is_true(mermaid:find("[\"new-package\"]", 1, true) ~= nil)
    t.is_true(mermaid:find("[\"alarm_poll\"]", 1, true) ~= nil)
    t.is_true(mermaid:find("[\"alpha_one\"]", 1, true) ~= nil)
    t.is_true(mermaid:find("[\"beta_two\"]", 1, true) ~= nil)
    t.is_true(mermaid:find(" --> ", 1, true) ~= nil)
    t.is_true(mermaid:find("alarm_poll", 1, true) ~= nil)
    t.is_true(mermaid:find("alpha_one", 1, true) ~= nil)
    t.is_true(mermaid:find("beta_two", 1, true) ~= nil)
  end,

  test_topology_mermaid_is_deterministic_and_derived = function()
    local graph = topology_fixture()
    assert_ops_departments_do_not_produce_devloop_lifecycle_queues(graph)
    local mermaid = topology.render_mermaid(graph)
    local permuted = topology.render_mermaid(permuted_graph(graph))

    t.eq(mermaid, permuted)
    t.is_true(mermaid:find("flowchart LR", 1, true) == 1)
    t.eq(count_literal(mermaid, "[\"github-proxy\"]"), 1)
    t.eq(count_literal(mermaid, "[\"consensus\"]"), 1)
    t.eq(count_literal(mermaid, "[\"github-devloop\"]"), 1)
    t.eq(count_literal(mermaid, "[\"github-devloop-intake\"]"), 1)
    t.eq(count_literal(mermaid, "[\"branch-topology\"]"), 1)
    t.eq(count_literal(mermaid, "[\"fkst-substrate-ref-maintainer\"]"), 1)
    t.is_true(mermaid:find("[\"github_poll\"]", 1, true) ~= nil)
    t.is_true(mermaid:find("[\"observe_issue\"]", 1, true) ~= nil)
    t.is_true(mermaid:find("[\"consensus_result\"]", 1, true) ~= nil)
    t.eq(mermaid:find("[\"ready(dep-gate)\"]", 1, true), nil)
    t.is_true(mermaid:find("github_poll", 1, true) ~= nil)
    t.is_true(mermaid:find("observe_issue", 1, true) ~= nil)
    t.is_true(mermaid:find("intake_judge", 1, true) ~= nil)
    t.is_true(mermaid:find("consensus_result", 1, true) ~= nil)
    t.is_true(mermaid:find("implement", 1, true) ~= nil)
    t.is_true(mermaid:find("review_result", 1, true) ~= nil)
    t.is_true(mermaid:find("merge", 1, true) ~= nil)
    t.is_true(mermaid:find("rollup_merge", 1, true) ~= nil)
    t.eq(mermaid:find("#42", 1, true), nil)
    t.eq(mermaid:find("quota", 1, true), nil)
    t.eq(mermaid:find("queue depth", 1, true), nil)
  end,

  test_ops_observability_fixture_matches_real_published_outputs = function()
    local graph = topology_fixture()
    assert_ops_departments_do_not_produce_devloop_lifecycle_queues(graph)
    for _, node in ipairs(graph.nodes or {}) do
      if node.kind == "department" and node.id == "department:github-devloop-ops.observability" then
        t.eq(#node.produces, 1)
        t.eq(node.produces[1], "github-proxy.github_issue_create_request")
        return
      end
    end
    error("missing github-devloop-ops observability department fixture")
  end,

  test_topology_mermaid_normalizes_ids_and_escapes_labels = function()
    local b = graph_builder()
    b.department("odd.pkg-a", {}, { "odd.ready-q" })
    b.department("odd.pkg b", { "odd.ready-q" }, {})
    local graph = b.graph()
    for _, node in ipairs(graph.nodes) do
      if node.id == "department:odd.pkg-a" then
        node.name = "pkg-a \"source\""
      elseif node.id == "department:odd.pkg b" then
        node.name = "pkg b [sink]"
      end
    end

    local mermaid = topology.render_mermaid(graph)

    t.is_true(mermaid:find("[\"pkg-a \\\"source\\\"\"]", 1, true) ~= nil)
    t.is_true(mermaid:find("[\"pkg b [sink]\"]", 1, true) ~= nil)
    t.eq(mermaid:find("department:odd.pkg-a", 1, true), nil)
    t.eq(mermaid:find("pkg b -->", 1, true), nil)
  end,

  test_dashboard_renders_topology_before_working_and_keeps_hash_stable = function()
    local graph = topology_fixture()
    local mermaid = topology.render_mermaid(graph)
    local rendered = core.render_observability_dashboard({
      entities = {},
      counts = {},
      stalls = {},
      state_gap_report = {},
      now_seconds = 1780000000,
      topology_mermaid = mermaid,
    })
    local rerendered = core.render_observability_dashboard({
      entities = {},
      counts = {},
      stalls = {},
      state_gap_report = {},
      now_seconds = 1780000060,
      topology_mermaid = topology.render_mermaid(permuted_graph(graph)),
    })

    t.is_true(rendered.body:find("## System topology", 1, true) < rendered.body:find("## Now working", 1, true))
    t.eq(count_literal(rendered.body, "```mermaid"), 1)
    t.eq(count_literal(rendered.body, "```"), 2)
    t.is_true(rendered.body:find("## Board by state", 1, true) ~= nil)
    t.is_true(rendered.body:find("## Stall suspects", 1, true) ~= nil)
    t.is_true(rendered.body:find("## State-gap latency", 1, true) ~= nil)
    t.is_true(rendered.body:find("## Footer", 1, true) ~= nil)
    t.eq(rendered.hash, rerendered.hash)
  end,

  test_dashboard_topology_states_operator_question_it_answers = function()
    local rendered = core.render_observability_dashboard({
      entities = {},
      counts = {},
      stalls = {},
      state_gap_report = {},
      now_seconds = 1780000000,
      topology_mermaid = topology.render_mermaid(topology_fixture()),
    })

    t.is_true(rendered.body:find(
      "Operator orientation: this projects `graph_json()` nodes into package lanes and queue-mediated message paths needed to read the live work sections below.",
      1,
      true
    ) ~= nil)
    t.is_true(rendered.body:find("## System topology", 1, true) < rendered.body:find("```mermaid", 1, true))
    t.is_true(rendered.body:find("```mermaid", 1, true) < rendered.body:find("## Now working", 1, true))
  end,

  test_dashboard_omits_topology_when_graph_unavailable = function()
    local rendered = core.render_observability_dashboard({
      entities = {},
      counts = {},
      stalls = {},
      state_gap_report = {},
      now_seconds = 1780000000,
      topology_mermaid = nil,
    })

    t.eq(rendered.body:find("## System topology", 1, true), nil)
    t.eq(rendered.body:find("```mermaid", 1, true), nil)
    t.is_true(rendered.body:find("## Now working", 1, true) ~= nil)
    t.is_true(rendered.body:find("## Board by state", 1, true) ~= nil)
    t.is_true(rendered.body:find("## Footer", 1, true) ~= nil)
  end,

  test_graph_json_failure_returns_nil_topology = function()
    local old_graph_json = graph_json
    graph_json = function()
      error("graph_json unavailable without composed graph roots")
    end
    local ok, mermaid = pcall(function()
      return core.observability_topology_mermaid()
    end)
    graph_json = old_graph_json

    t.eq(ok, true)
    t.eq(mermaid, nil)
  end,
}
