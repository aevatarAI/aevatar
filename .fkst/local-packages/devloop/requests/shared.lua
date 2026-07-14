local devloop_base = require("devloop.base")
local base_ids = require("devloop.base_ids")
local C = {}
local comment_strings = require("devloop.strings")
local strings = require("contract.strings")

C.strings = strings
C.ai_sentinel = "⟦AI:FKST⟧"
C.display_separator = " — "
C.max_display_question_len = 2000
C.max_display_digest_len = 600
C.max_display_attr_len = 120
C.max_display_block_len = 5000
C.max_verdict_summary_items = 8
C.max_verdict_summary_len = 600

function C.bounded_neutralized_text(M, value, limit)
  local text = tostring(value or "")
  local cap = limit or C.max_display_digest_len
  if #text > cap then
    text = base_ids.truncate_utf8(text, cap)
  end
  text = devloop_base.neutralize_untrusted_comment_text(text)
  if #text > cap then
    text = base_ids.truncate_utf8(text, cap)
  end
  return text
end

function C.angle_display_text(M, item)
  if type(item) ~= "table" then
    return nil
  end
  local angle = C.bounded_neutralized_text(M, item.angle or "unknown", C.max_display_attr_len)
  local verdict = C.bounded_neutralized_text(M, item.verdict or "invalid", C.max_display_attr_len)
  local digest = item.digest
  if digest == nil or tostring(digest) == "" then
    digest = item.reply
  end
  digest = C.bounded_neutralized_text(M, digest or "", C.max_display_digest_len)
  if digest == "" then
    return "- " .. angle .. ": " .. verdict
  end
  return "- " .. angle .. ": " .. verdict .. C.display_separator .. digest
end

function C.build_convergence_display(M, header, unresolved, round)
  local lines = {
    header .. tostring(round) .. comment_strings.comment_string(M, "convergence_suffix"),
  }
  local question = C.bounded_neutralized_text(M, unresolved and unresolved.narrowed_question or "", C.max_display_question_len)
  if question ~= "" then
    table.insert(lines, "")
    table.insert(lines, comment_strings.comment_string(M, "narrowed_question_label") .. question)
  end
  local angle_lines = {}
  if type(unresolved) == "table" and type(unresolved.angle_digests) == "table" then
    for _, item in ipairs(unresolved.angle_digests) do
      local line = C.angle_display_text(M, item)
      if line ~= nil then
        table.insert(angle_lines, line)
      end
    end
  end
  if #angle_lines > 0 then
    table.insert(lines, "")
    table.insert(lines, comment_strings.comment_string(M, "angle_stances_label"))
    for _, line in ipairs(angle_lines) do
      table.insert(lines, line)
    end
  end
  local body = table.concat(lines, "\n")
  if #body > C.max_display_block_len then
    body = base_ids.truncate_utf8(body, C.max_display_block_len)
  end
  return body
end

function C.build_verdict_summary(M, angle_results)
  if type(angle_results) ~= "table" then
    return nil
  end
  local parts = {}
  for _, item in ipairs(angle_results) do
    if #parts >= C.max_verdict_summary_items then
      break
    end
    if type(item) == "table" then
      local angle = C.bounded_neutralized_text(M, item.angle or "unknown", C.max_display_attr_len)
      local verdict = C.bounded_neutralized_text(M, item.verdict or "invalid", C.max_display_attr_len)
      table.insert(parts, angle .. "=" .. verdict)
    end
  end
  if #parts == 0 then
    return nil
  end
  local summary = comment_strings.comment_string(M, "verdict_summary_label") .. table.concat(parts, " ")
  if #summary > C.max_verdict_summary_len then
    summary = base_ids.truncate_utf8(summary, C.max_verdict_summary_len)
  end
  return summary
end

function C.bounded_blocking_gap(M, reached)
  local gap = reached and reached.blocking_gap
  if gap == nil and type(reached and reached.blocking_gaps) == "table" then
    gap = reached.blocking_gaps[1]
  end
  local text = tostring(gap or ""):gsub("%c", " "):gsub("%s+", " ")
  text = text:gsub("^%s+", ""):gsub("%s+$", "")
  if text == "" then
    return nil
  end
  if #text > M._max_blocking_gap_len then
    text = base_ids.truncate_utf8(text, M._max_blocking_gap_len)
  end
  return text
end

return C
