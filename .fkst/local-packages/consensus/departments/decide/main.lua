local core = require("core")
local saga = require("workflow.saga")

local spec = {
  consumes = { "proposal" },
  published_seam = { "proposal" },
  produces = { "consensus_reached", "consensus_converge" },
  published_seam = { "proposal" },
  stall_window = "2m",
}

local function read_runtime_root()
  local result = exec_sync({ cmd = core.read_runtime_root_cmd(), timeout = 30 })
  if result.exit_code ~= 0 then
    error("consensus: FKST_RUNTIME_ROOT read failed: " .. tostring(result.stderr))
  end
  return result.stdout
end

local function prepare_judgment_worktree(path)
  local result = exec_sync({ cmd = core.mkdir_p_cmd(path), timeout = 30 })
  if result.exit_code ~= 0 then
    error("consensus: judgment scratch directory setup failed: " .. tostring(result.stderr))
  end
  return path
end

local function codex_opts(proposal, prompt, worktree, role)
  local opts = core.judgment_codex_opts(prompt, worktree)
  opts.role = role or "consensus"
  opts.proposal_id = proposal.proposal_id
  opts.dedup_key = proposal.dedup_key
  return opts
end

local function spawn_angle(proposal, angle, runtime_root)
  local prompt = core.build_angle_prompt(proposal, angle)
  local worktree = prepare_judgment_worktree(
    core.judgment_scratch_worktree(runtime_root, "angle-" .. tostring(angle), proposal.dedup_key)
  )
  return spawn_codex(codex_opts(proposal, prompt, worktree, "consensus"))
end

local function spawn_meta_judge(proposal, angle_results, runtime_root)
  local prompt = core.build_meta_judge_prompt(proposal, angle_results)
  local worktree = prepare_judgment_worktree(
    core.judgment_scratch_worktree(runtime_root, "meta-judge", proposal.dedup_key)
  )
  return spawn_codex_sync(codex_opts(proposal, prompt, worktree, "consensus"))
end

local function raise_converge(proposal, angle_results, narrowed_question)
  raise(
    "consensus_converge",
    core.build_converge_payload(proposal, narrowed_question, angle_results)
  )
end

local function decide(proposal)
  local runtime_root = read_runtime_root()

  local angle_results = {}
  local handles = {}
  local angles = core.angles(proposal)
  local verdict_mode = core.verdict_mode(proposal)
  for _, angle in ipairs(angles) do
    table.insert(handles, spawn_angle(proposal, angle, runtime_root))
  end

  local results = await_all(handles)
  for index, angle in ipairs(angles) do
    local parsed = nil
    local result = results[index]
    if type(result) == "table" and result.exit_code == 0 then
      parsed = core.parse_angle_output(result.stdout, verdict_mode)
    end
    table.insert(angle_results, {
      angle = angle,
      verdict = parsed and parsed.verdict or nil,
      reply = parsed and parsed.reply or nil,
      blocking_gap = parsed and parsed.blocking_gap or nil,
      stdout = type(result) == "table" and result.stdout or nil,
      exit_code = type(result) == "table" and result.exit_code or nil,
    })
  end

  local decision = core.aggregate(angle_results, verdict_mode)
  if decision ~= nil then
    return {
      queue = "consensus_reached",
      payload = core.build_reached_payload(proposal, decision, angle_results),
      cache = true,
    }
  end

  local meta_result = spawn_meta_judge(proposal, angle_results, runtime_root)
  local parsed = nil
  if type(meta_result) == "table" and meta_result.exit_code == 0 then
    parsed = core.parse_meta_judge_output(meta_result.stdout, verdict_mode)
  end
  if parsed ~= nil and parsed.kind == "reached" and core.all_angles_succeeded(angle_results) then
    return {
      queue = "consensus_reached",
      payload = core.build_reached_payload(
        proposal,
        parsed.decision,
        angle_results,
        parsed.framing
      ),
      cache = true,
    }
  end
  if parsed ~= nil and (parsed.kind == "converge" or parsed.kind == "plan") then
    return {
      queue = "consensus_converge",
      angle_results = angle_results,
      narrowed_question = parsed.narrowed_question,
    }
  end

  return {
    queue = "consensus_converge",
    angle_results = angle_results,
    narrowed_question = core.default_narrowed_question(proposal, angle_results),
  }
end

local function decision_done(event)
  local proposal = event.payload or {}
  if proposal.schema ~= "consensus.proposal.v1" then
    log.warn("consensus: unsupported proposal schema")
    return true
  end
  if not core.is_eligible(proposal) then
    return true
  end

  local cache_key = core.reached_cache_key(proposal.dedup_key)
  local already_reached = false
  with_lock(cache_key, function()
    already_reached = cache_get(cache_key) ~= nil
  end)
  return already_reached
end

local function act_decide(event)
  local proposal = event.payload or {}
  local cache_key = core.reached_cache_key(proposal.dedup_key)

  local ok, result = pcall(decide, proposal)
  if not ok then
    if core.is_stale_generation_context_error(result) then
      log.warn(
        "consensus dept=decide tag=STALE_GENERATION_CONTEXT"
          .. " proposal_id=" .. tostring(proposal.proposal_id)
          .. " dedup_key=" .. tostring(proposal.dedup_key)
          .. " error_class=" .. core.stale_generation_context_error_class()
      )
      return
    end
    error(result)
  end

  with_lock(cache_key, function()
    if cache_get(cache_key) then
      return
    end
    if result.queue == "consensus_reached" then
      raise("consensus_reached", result.payload)
      if result.cache then
        cache_set(cache_key, proposal.dedup_key)
      end
      return
    end
    if result.queue == "consensus_converge" then
      raise_converge(proposal, result.angle_results, result.narrowed_question)
      return
    end
    error("consensus: unknown decision result")
  end)
end

return saga.department(spec, {
  done = decision_done,
  act = act_decide,
  wrap = core.wrap_pipeline_failure,
  name = "decide",
})
