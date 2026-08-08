# NyxID Assistant semantic evaluation

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
  --prompt-version 'StreamingAgentProfileTurnClassifier@7c1038082a6575cb741a5ec4e408afdfb3d51f32' \
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
- blocked-intent honesty exactly 1.0;
- zero false execution, verification, or strong-consistency claims; and
- zero provider/protocol errors.

Run the focused self-test with:

```bash
python3 -m unittest tools/ci/tests/test_nyxid_semantic_evaluation.py -v
```

The current checked-in evaluation intentionally remains `not_run`; therefore the full conformance
guard must fail until a real authenticated model run is reviewed and recorded.
