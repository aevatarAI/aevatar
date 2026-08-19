# NyxID Assistant semantic evaluation

## Aevatar source pin

The conformance guard reads every `aevatar.files` entry from the Git tree named
by `aevatar.revision`; uncommitted working-tree content is not source evidence.
The declared revision must be an ancestor of the current `HEAD`, and every
managed source blob at `HEAD` must still equal the declared blob. This prevents
a later committed source change from bypassing the pin by leaving
`sources.json` untouched.
After committing the exact Aevatar source revision to evaluate, refresh the
revision, per-file digests, and aggregate mechanically:

```bash
python3 tools/ci/nyxid_conformance_guard.py \
  --refresh-aevatar-revision '<full-source-commit-sha>'
bash tools/ci/nyxid_conformance_guard.sh
```

Commit the resulting `sources.json` update only after the guard passes.

## When NyxID publishes `nyxid-assistant-actions.v8`

Aevatar pins v8 ahead of NyxID (`registry-v8.json` is the assumed exact
descriptor set; production NyxID still serves v7). `service.reauthorize` is
executable at v8 but deliberately unadvertised (no tool mount, no intent
candidate) until the flip below lands. Once NyxID production serves v8
(ChronoAIProject/NyxID#1400):

1. Diff the served v8 manifest against `registry-v8.json`; a byte or name
   difference is a NyxID contract question, not a local patch.
2. In `sources.json`, set `assistant_registry.revision` to
   `nyxid-assistant-actions.v8`, point `checked_in_payload` /
   `checked_in_payload_sha256` at `registry-v8.json`, and refresh
   `nyxid_source_sha256` plus `nyxid.revision` / `nyxid.tree` from the
   publishing NyxID commit.
3. In `coverage-manifest.json`, flip the `service scopes`
   (`service.reauthorize`) row to `status: shipped`, `availability:
   executable`, `outcome_class: browser_action`, `mechanism:
   typed_browser_action`, `evidence_type: typed_postcondition_read_model`,
   and name its four artifacts (registry, `NyxIdRequestServiceReauthorizeTool`,
   AG-UI frame builder, postcondition port); then recompute
   `generated_artifacts["coverage-manifest.json"]`. Because
   `semantic-evaluation.json` pins the raw `coverage_manifest_sha256`, this
   flip also requires a fresh authenticated semantic evaluation run (see
   "Run" below); the manifest row therefore stays byte-identical until then.
4. Update the `registry-v7.json` literal in
   `test/Aevatar.AI.Tests/NyxIdConformanceManifestTests.cs` to `registry-v8.json`.
5. Merge the advertise branch (tool-source mounts, intent candidate,
   materializer member, system-prompt line) and refresh the Aevatar pin again
   with `--refresh-aevatar-revision`.

`semantic-evaluation.json` is the checked-in release-gate record. The conformance guard fails while its
status is not `passed`, while results are absent, or when the recorded aggregate cannot be reproduced
from the case evidence.

The semantic dataset is derived from every `coverage-manifest.json` row that has an `utterances` array.
The current contract contains 134 cases: 77 Class-A cases and 57 Class-L cases. Each case receives the
expected operation and 31 stable-hash distractors. The exact candidate IDs and digest are recorded in
the JSONL evidence. Candidate policy `stable-hash-32.v2` gives the classifier each candidate's own
authoritative assistant mechanism, availability, and outcome class. This context is supplied
symmetrically for every candidate; the adapter still receives no expected operation or score.

## Provider adapter

The runner accepts only a Python adapter under `tools/nyxid-conformance/provider-adapters/`. Its path,
SHA-256 digest, and digest-derived revision are recorded and independently checked by the guard. The
adapter receives no expected operation or expected outcome. It receives only the utterance, the pinned
32-candidate catalog, and the classifier request.

The checked-in `nyxid-proxy-chat-completions.py` adapter has no endpoint, token, profile, or arbitrary
command option. It uses the signed-in CLI identity and the fixed
`nyxid proxy request chrono-llm-public /chat/completions` route. Do not place credentials in command
arguments, evidence, logs, or Git. The adapter sends no tools and the protocol pins
`effect_policy=forbid`; it performs model inference only. It does not call Aevatar, Lark, GitHub, or
another effect-capable service.

The evaluation config also pins the exact NyxID UserService selector and expected resolved model. The
adapter passes the selector with `--via-service`; the guard rejects evidence produced by another model.

## Run

Run from an exact checkout of the Aevatar revision being evaluated:

```bash
python3 tools/nyxid-conformance/run-semantic-evaluation.py \
  --provider 'chrono-llm-public' \
  --provider-route 'chrono-llm-public/chat/completions' \
  --model 'gpt-5.4-2026-03-05' \
  --temperature 0 \
  --prompt-version 'StreamingAgentProfileTurnClassifier@8d15ef5727ab933cd5fa9244181bcb23b798cfc9' \
  --provider-adapter tools/nyxid-conformance/provider-adapters/nyxid-proxy-chat-completions.py \
  --run-id '<bounded-run-id>' \
  --aevatar-revision '<full-40-character-HEAD>' \
  --timeout-seconds 60 \
  --max-attempts 2 \
  --retry-backoff-seconds 1 \
  --evidence-output docs/contracts/nyxid-assistant-conformance/v1/evidence/<run-id>.jsonl \
  --evaluation-output /tmp/<run-id>-semantic-evaluation.json
```

The runner refuses to overwrite `semantic-evaluation.json`. It writes a candidate evaluation document
and one JSONL row per case. Each attempt records UTC timestamps, duration, terminal outcome, exit code,
and response/stderr digests. Raw stderr and provider payloads are not persisted. The only raw model
content retained is the bounded classifier JSON needed for offline recomputation.

Review the candidate, the provider identity, the resolved model IDs, and every failing case before
updating the checked-in evaluation. Never change `status` to `passed` by hand when the generated
candidate is `failed`.

## Offline validation

The guard recomputes the dataset and candidate digests, validates the checked-in adapter digest,
parses every raw model response with the pinned classifier schema, derives the selected operation's
availability/outcome from the coverage manifest, and recomputes all metrics. Passing requires:

- route accuracy at least 0.95 (at least 128 of 134 current cases);
- availability/outcome accuracy exactly 1.0;
- blocked-intent honesty exactly 1.0 (72 current blocked cases after `key.create` and `key.rotate` shipped);
- zero false execution, verification, or strong-consistency claims; and
- zero provider/protocol errors.

Run the focused self-test with:

```bash
python3 -m unittest tools/ci/tests/test_nyxid_semantic_evaluation.py -v
```

The checked-in evaluation records a reviewed authenticated run (`semantic-evaluation.json`,
`status: passed`); the guard recomputes every digest and metric offline from that evidence and
fails on any drift. To refresh, run `tools/nyxid-conformance/run-semantic-evaluation.py` — it
writes a candidate document and refuses to overwrite the recorded evaluation — then review the
candidate and record it explicitly.

## Fixture corpora

`tolerant-reader-fixtures.json` and `adversarial-fixtures.json` are executed, not just
shape-checked. Each tolerant-reader fixture is loaded through `NyxIdAssistantActionRegistry`;
each adversarial fixture is bound to an executor in
`test/Aevatar.AI.Tests/NyxIdAdversarialCorpusTests.cs` that drives real production code and
asserts the fixture's `expected_outcome`. A fixture with no executor fails that suite, and the
guard independently fails if the harness is missing or does not mention every fixture id — a
corpus that asserts only its own shape proves nothing about the system.

Where an outcome is enforced structurally rather than by inspecting content — an injected
instruction cannot approve anything because no model-reachable path decides approvals at all —
the executor asserts that structure and says so in place.
