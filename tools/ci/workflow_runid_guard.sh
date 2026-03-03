#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../.." && pwd)"
cd "${REPO_ROOT}"

scan_roots=(
  "src/workflow/Aevatar.Workflow.Core/Modules"
  "src/workflow/extensions"
)

violations=""

for root in "${scan_roots[@]}"; do
  [ -d "${root}" ] || continue

  while IFS= read -r file; do
    [ -z "${file}" ] && continue

    file_hits="$(
      awk '
      function count_chars(text, ch, i, cnt)
      {
        cnt = 0;
        for (i = 1; i <= length(text); i++)
        {
          if (substr(text, i, 1) == ch)
            cnt++;
        }
        return cnt;
      }

      BEGIN {
        inside = 0;
        depth = 0;
        has_run_id = 0;
        start_line = 0;
        kind = "";
      }

      {
        line = $0;

        if (!inside)
        {
          if (match(line, /new[[:space:]]+StepCompletedEvent[[:space:]]*\{/))
          {
            inside = 1;
            kind = "StepCompletedEvent";
            start_line = FNR;
            has_run_id = (line ~ /RunId[[:space:]]*=/);
            block = substr(line, RSTART);
            depth = count_chars(block, "{") - count_chars(block, "}");
            if (depth <= 0)
            {
              if (!has_run_id)
                printf "%s:%d:%s\n", FILENAME, start_line, kind;
              inside = 0;
              depth = 0;
              kind = "";
              has_run_id = 0;
            }
          }
          else if (match(line, /new[[:space:]]+StepRequestEvent[[:space:]]*\{/))
          {
            inside = 1;
            kind = "StepRequestEvent";
            start_line = FNR;
            has_run_id = (line ~ /RunId[[:space:]]*=/);
            block = substr(line, RSTART);
            depth = count_chars(block, "{") - count_chars(block, "}");
            if (depth <= 0)
            {
              if (!has_run_id)
                printf "%s:%d:%s\n", FILENAME, start_line, kind;
              inside = 0;
              depth = 0;
              kind = "";
              has_run_id = 0;
            }
          }
          next;
        }

        if (line ~ /RunId[[:space:]]*=/)
          has_run_id = 1;

        depth += count_chars(line, "{") - count_chars(line, "}");
        if (depth <= 0)
        {
          if (!has_run_id)
            printf "%s:%d:%s\n", FILENAME, start_line, kind;
          inside = 0;
          depth = 0;
          kind = "";
          has_run_id = 0;
        }
      }
      ' "${file}"
    )"

    if [ -n "${file_hits}" ]; then
      violations="${violations}${file_hits}"$'\n'
    fi
  done < <(rg --files "${root}" -g '*.cs')
done

if [ -n "${violations}" ]; then
  echo "${violations}"
  echo "Workflow step events must set RunId explicitly in StepRequestEvent/StepCompletedEvent initializers."
  exit 1
fi

echo "Workflow run-id guard passed."
