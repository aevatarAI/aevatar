local t = fkst.test
local conformance = require("testkit.namespaced_dispatch_conformance")

local function load_department(path, module_name)
  local old_pipeline = pipeline
  local module = require(module_name)
  pipeline = old_pipeline
  return { path = path, module = module }
end

local departments = conformance.loaded_departments({
  load_department("departments/dead_letter/main.lua", "departments.dead_letter.main"),
  load_department("departments/decide/main.lua", "departments.decide.main"),
  load_department("departments/test_cache_seed/main.lua", "departments.test_cache_seed.main"),
})

local function proposal_payload()
  return {
    schema = "consensus.proposal.v1",
    proposal_id = "proposal-42",
    title = "Adopt consensus package",
    body = "Create a small flat package that asks several angles to judge a proposal.",
    content_fetch = "fetch-source --ref demo/consensus/42 --full",
    angles = { "minimal", "structural", "delete" },
    dedup_key = "proposal-42-v1",
    source_ref = {
      kind = "proposal",
      ref = "demo/consensus/42",
    },
  }
end

local function payload_for_queue(_path, queue)
  local payloads = {
    cache_seed = {
      key = "consensus/test-cache-seed",
      value = "1",
    },
    dead_letter = {
      delivery_id = "delivery/v1/raised/queue/consensus.proposal/dept/consensus.decide/01HY",
      queue = "consensus.proposal",
      dept = "consensus.decide",
      dedup_key = "dead-letter-test",
      attempt = 1,
      error = "test error",
      source_ref = {
        kind = "proposal",
        ref = "demo/consensus/42",
      },
    },
    proposal = proposal_payload(),
  }
  local payload = payloads[queue]
  if payload == nil then
    error("consensus: no production-shaped queue fixture for " .. tostring(queue))
  end
  return payload
end

return {
  test_all_departments_accept_production_namespaced_consumed_queues = function()
    conformance.assert_all_consumed_queues_route({
      t = t,
      package_name = "consensus",
      package_root = "packages/consensus",
      departments = departments,
      payload_for_queue = payload_for_queue,
    })
  end,
}
