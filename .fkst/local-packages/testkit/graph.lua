local M = {}

local t = fkst.test

local function contains(value, expected)
  return tostring(value or ""):find(tostring(expected or ""), 1, true) ~= nil
end

local function list_contains(values, expected)
  for _, value in ipairs(values or {}) do
    if value == expected then
      return true
    end
  end
  return false
end

local function require_spec_queue(spec, field, queue)
  if not list_contains(spec and spec[field], queue) then
    error(
      "missing M.spec."
        .. tostring(field)
        .. " queue="
        .. tostring(queue),
      3
    )
  end
end

function M.run(event_or_source, opts)
  return t.run_graph(event_or_source, opts or {})
end

function M.find_delivery(trace, expected)
  for index, step in ipairs((trace and trace.steps) or {}) do
    local queue_ok = expected.queue == nil or step.queue == expected.queue
    local consumer_ok = expected.consumer == nil or step.consumer == expected.consumer
    if queue_ok and consumer_ok then
      if expected.predicate == nil or expected.predicate(step, index) then
        return step, index
      end
    end
  end
  return nil
end

function M.require_delivery(trace, expected)
  local step, index = M.find_delivery(trace, expected)
  if step == nil then
    error(
      "missing delivery queue="
        .. tostring(expected.queue)
        .. " consumer="
        .. tostring(expected.consumer),
      2
    )
  end
  return step, index
end

-- Integration-coverage observation (the anti-lie gate of the coverage ratchet).
-- A run_graph test DECLARES the cross-package edges it covers as canonical edge
-- ids "<producer_namespaced_queue> -> <consumer_package.dept>"; this asserts each
-- declared edge is ACTUALLY observed as a delivery in the real trace, so a stale
-- or wrong `covers` declaration fails the test (coverage cannot be claimed without
-- being proven through the real router). scripts/check_repo_integration_coverage.py
-- reads the static `covers` declarations, trusting them because this assertion
-- would fail the test otherwise.
function M.parse_coverage_edge(edge)
  local queue, consumer = tostring(edge):match("^%s*(.-)%s*%->%s*(.-)%s*$")
  if not queue or queue == "" or not consumer or consumer == "" then
    error("invalid coverage edge id (want '<queue> -> <pkg.dept>'): " .. tostring(edge), 3)
  end
  return queue, consumer
end

function M.assert_covers(trace, edges)
  for _, edge in ipairs(edges or {}) do
    local queue, consumer = M.parse_coverage_edge(edge)
    if M.find_delivery(trace, { queue = queue, consumer = consumer }) == nil then
      error("coverage edge declared in `covers` but not observed as a delivery in the trace: " .. tostring(edge), 2)
    end
  end
  return trace
end

function M.find_raise(trace, queue, predicate)
  for step_index, step in ipairs((trace and trace.steps) or {}) do
    for raise_index, raised in ipairs(step.raises or {}) do
      if raised.queue == queue and (predicate == nil or predicate(raised, step, step_index)) then
        return raised, step, step_index, raise_index
      end
    end
  end
  return nil
end

function M.require_raise(trace, queue, predicate)
  local raised, step, step_index, raise_index = M.find_raise(trace, queue, predicate)
  if raised == nil then
    error("missing raised queue=" .. tostring(queue), 2)
  end
  return raised, step, step_index, raise_index
end

function M.require_quiescent(trace)
  t.eq(trace.status, "quiescent")
  t.eq(trace.final.dead_letters, 0)
  for index, step in ipairs((trace and trace.steps) or {}) do
    if step.status ~= "accepted" or step.exit_code ~= 0 then
      error(
        "delivery step failed at index="
          .. tostring(index)
          .. " queue="
          .. tostring(step.queue)
          .. " consumer="
          .. tostring(step.consumer)
          .. " error="
          .. tostring(step.error),
        2
      )
    end
  end
  return trace
end

function M.require_router_regression(trace, expected)
  require_spec_queue(expected.spec, "consumes", expected.entry_queue)
  require_spec_queue(expected.spec, "produces", expected.raised_queue)

  local delivery_step, delivery_index = M.require_delivery(trace, {
    queue = expected.entry_queue,
    consumer = expected.consumer,
  })
  t.eq(delivery_step.exit_code, 0)

  local raised, raise_step, raise_step_index, raise_index = M.require_raise(
    trace,
    expected.raised_queue,
    expected.raised_predicate
  )
  t.eq(raise_step_index, delivery_index)

  local downstream_step = nil
  local downstream_index = nil
  if expected.downstream_consumer ~= nil then
    downstream_step, downstream_index = M.require_delivery(trace, {
      queue = expected.raised_queue,
      consumer = expected.downstream_consumer,
    })
    t.eq(downstream_step.exit_code, 0)
    t.is_true(downstream_index > raise_step_index)
  end

  return {
    delivery_step = delivery_step,
    delivery_index = delivery_index,
    raised = raised,
    raise_step = raise_step,
    raise_step_index = raise_step_index,
    raise_index = raise_index,
    downstream_step = downstream_step,
    downstream_index = downstream_index,
  }
end

function M.signature(trace)
  local rows = {}
  for index, step in ipairs((trace and trace.steps) or {}) do
    local raises = {}
    for raise_index, raised in ipairs(step.raises or {}) do
      local payload = raised.payload or {}
      raises[raise_index] = table.concat({
        tostring(raised.queue or ""),
        tostring(payload.schema or ""),
        tostring(payload.proposal_id or ""),
        tostring(payload.decision or ""),
        tostring(payload.dedup_key or ""),
      }, ":")
    end
    rows[index] = table.concat({
      tostring(step.delivery_id or ""),
      tostring(step.queue or ""),
      tostring(step.consumer or ""),
      tostring(step.status or ""),
      table.concat(raises, ","),
    }, ">")
  end
  return table.concat(rows, "|")
end

function M.signature_without_payload_identity(trace)
  local rows = {}
  for index, step in ipairs((trace and trace.steps) or {}) do
    local raises = {}
    for raise_index, raised in ipairs(step.raises or {}) do
      local payload = raised.payload or {}
      raises[raise_index] = table.concat({
        tostring(raised.queue or ""),
        tostring(payload.schema or ""),
        tostring(payload.decision or ""),
      }, ":")
    end
    rows[index] = table.concat({
      tostring(step.queue or ""),
      tostring(step.consumer or ""),
      tostring(step.status or ""),
      table.concat(raises, ","),
    }, ">")
  end
  return table.concat(rows, "|")
end

function M.payload_contains(raised, fragment)
  local payload = raised and raised.payload or {}
  return contains(payload.body, fragment)
    or contains(payload.dedup_key, fragment)
    or contains(payload.proposal_id, fragment)
end

return M
