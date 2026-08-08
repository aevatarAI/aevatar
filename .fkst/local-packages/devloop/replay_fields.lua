local R = {}

local marker_aliases = {
  ["pr-delegation"] = { pr = "pr_number", pr_proposal = "pr_proposal_id" },
  ["pr-link"] = { pr = "pr_number" },
  ["review-result"] = { gap = "blocking_gap" },
  ["merge-gate"] = { review_proposal = "review_proposal_id", review_dedup = "review_dedup_key", head_sha = "reviewed_head_sha" },
  ["merge-ready"] = { pr = "pr_number", review_proposal = "review_proposal_id", review_dedup = "review_dedup_key", head_sha = "head_sha" },
  merging = { head_sha = "head_sha" },
  ["review-converge-round"] = { proposal = "proposal_id", dedup = "dedup_key", round = "n" },
}

local function marker_source(facts, family)
  if family == "state" then
    return facts.state
  end
  if family == "pr-delegation" then
    return facts["pr-delegation"] or facts.pr_delegation
  end
  if family == "child-state" then
    return facts.child_state
  end
  if family == "pr-link" then
    return facts.link
  end
  if family == "review-result" or family == "merge-gate" then
    return facts.feedback
  end
  if family == "review-meta" or family == "fix-reflection" or family == "review-converge-round" then
    return facts.review_meta
  end
  if family == "decomposed" then
    return facts.decomposed
  end
  if family == "merge-ready" then
    return facts.merge_ready or facts["merge-ready"]
  end
  return facts[family]
end

local function marker_value(facts, family, attr)
  local source = marker_source(facts, family)
  if source == nil then
    return nil
  end
  if family == "state" and attr == "proposal" then
    return facts.proposal_id
  end
  if attr == "proposal" and source.proposal_id ~= nil then
    return source.proposal_id
  end
  if attr == "dedup" and source.dedup_key ~= nil then
    return source.dedup_key
  end
  local aliases = marker_aliases[family] or {}
  local key = aliases[attr] or attr
  return source[key]
end

function R.resolve(row, state, facts, pr_source_ref)
  local resolved = {}
  local context = facts or {}
  context.state = state
  for field, reference in pairs(row.payload_fields or {}) do
    local family, attr = tostring(reference or ""):match("^marker:([^%.]+)%.(.+)$")
    if family ~= nil then
      resolved[field] = marker_value(context, family, attr)
    else
      local derivation = tostring(reference or ""):match("^source_ref:(.+)$")
      if derivation == "issue" or derivation == "entity" then
        resolved[field] = context.issue and context.issue.source_ref or nil
      elseif derivation == "pr" then
        if context.source_ref ~= nil then
          resolved[field] = context.source_ref
        else
          local pr_number = context.pr_number or (context.link and context.link.pr_number)
            or (context.feedback and context.feedback.pr_number) or (context.review_meta and context.review_meta.pr_number)
            or (context.decomposed and context.decomposed.pr_number)
          resolved[field] = pr_source_ref(context.issue and context.issue.repo or "", pr_number)
        end
      end
    end
  end
  return resolved
end

function R.restart_transition_row(transition_table, state_name)
  -- Behavior-preserving: the pre-extraction code iterated M.restart_transition_table()
  -- directly, so a nil/miswired table HARD-FAILS (ipairs(nil) errors) rather than
  -- silently returning no row. Do NOT add `or {}` — fail loud, never swallow a miswire.
  for _, row in ipairs(transition_table) do
    if row.from_state == state_name then
      return row
    end
  end
  return nil
end

function R.replay_raise_effects(log_apply, log_raise, dept, proposal_id, apply_state, version, label_changes, effects)
  local queues = {}
  for _, effect in ipairs(effects or {}) do
    table.insert(queues, effect.queue)
  end
  log_apply(dept, proposal_id, apply_state, version, label_changes or { add = {}, remove = {} }, queues)
  for _, effect in ipairs(effects or {}) do
    log_raise(dept, proposal_id, effect.queue, effect.payload)
  end
  return true
end

-- Shared marker-attribute reader: also used by replayer.gather_required_facts to
-- resolve a fallback proposal_id, so it is part of the typed module surface.
R.marker_value = marker_value

return R
