#!/usr/bin/env python3

import json
import subprocess
import sys


PROVIDER = "chrono-llm-public"
PROVIDER_PATH = "chat/completions"
MODEL_ROUTE = f"{PROVIDER}/{PROVIDER_PATH}"


def main() -> int:
    request = json.load(sys.stdin)
    if request.get("provider") != PROVIDER:
        raise ValueError("evaluation provider does not match the checked-in NyxID service")
    if request.get("model_route") != MODEL_ROUTE:
        raise ValueError("evaluation route does not match the checked-in NyxID proxy route")
    if request.get("effect_policy") != "forbid":
        raise ValueError("semantic evaluation requires the forbid effect policy")

    llm_request = request["llm_request"]
    payload = {
        "model": request["model"],
        "messages": llm_request["messages"],
        "temperature": llm_request["temperature"],
        "max_tokens": llm_request["max_tokens"],
        "response_format": {
            "type": "json_schema",
            "json_schema": {
                "name": llm_request["response_format"],
                "strict": True,
                "schema": {
                    "type": "object",
                    "properties": {
                        "status": {"type": "string", "enum": ["matched", "no_match"]},
                        "intent_id": {"type": ["string", "null"]},
                    },
                    "required": ["status", "intent_id"],
                    "additionalProperties": False,
                },
            },
        },
    }
    result = subprocess.run(
        [
            "nyxid",
            "proxy",
            "request",
            PROVIDER,
            PROVIDER_PATH,
            "--method",
            "POST",
            "--header",
            "Content-Type:application/json",
            "--data",
            "-",
            "--via-service",
            request["provider_user_service_id"],
            "--output",
            "json",
        ],
        input=json.dumps(payload, separators=(",", ":")),
        text=True,
        capture_output=True,
        timeout=float(request["timeout_seconds"]),
        check=False,
    )
    if result.returncode != 0:
        raise RuntimeError("authenticated NyxID provider request failed")
    response_payload = json.loads(result.stdout)
    choices = response_payload.get("choices")
    if not isinstance(choices, list) or len(choices) != 1:
        raise ValueError("provider response must contain exactly one choice")
    raw_response = choices[0].get("message", {}).get("content")
    if not isinstance(raw_response, str) or not raw_response:
        raise ValueError("provider response has no text content")
    request_id = response_payload.get("id")
    resolved_model = response_payload.get("model")
    if not isinstance(request_id, str) or not request_id:
        raise ValueError("provider response has no request id")
    if not isinstance(resolved_model, str) or not resolved_model:
        raise ValueError("provider response has no resolved model")

    json.dump(
        {
            "schema_version": 1,
            "request_id": request_id,
            "resolved_model": resolved_model,
            "raw_response": raw_response,
        },
        sys.stdout,
        separators=(",", ":"),
    )
    sys.stdout.write("\n")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as error:
        print(f"semantic provider adapter failed: {error}", file=sys.stderr)
        raise SystemExit(1)
