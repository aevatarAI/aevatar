#!/usr/bin/env bash
# Ensure the deployed Mainnet ConfigMap carries the Workflow Delivery product-console URL.
#
# The deployment mounts appsettings.Distributed.json from a ConfigMap over the copy baked
# into the image, so the values committed in this repository never reach production.
# Without Aevatar:Delivery:ConsoleWebBaseUrl, delivery responses carry no product-console link.
#
# Only the product-console key is written and the obsolete connect-callback key is removed;
# every other setting is preserved byte-for-byte by
# round-tripping the document through a JSON parser. Prints a diff and requires an
# explicit APPLY=1 to write.
set -euo pipefail

KUBECONFIG_PATH="${AEVATAR_PROD_KUBECONFIG:-$HOME/Code/aelf-shared-k8s-prod.yaml}"
NAMESPACE="${AEVATAR_PROD_NAMESPACE:-aismart-app-mainnet}"
CONFIGMAP="${AEVATAR_DELIVERY_CONFIGMAP:-aevatar-console-backend-appsettings-distributed}"
FILE_KEY="appsettings.Distributed.json"
CONSOLE_WEB_BASE_URL="${AEVATAR_DELIVERY_CONSOLE_WEB_BASE_URL:-https://aevatar-console.aevatar.ai}"

kube() { kubectl --kubeconfig="$KUBECONFIG_PATH" --insecure-skip-tls-verify -n "$NAMESPACE" "$@"; }

workdir="$(mktemp -d)"
trap 'rm -rf "$workdir"' EXIT

kube get configmap "$CONFIGMAP" -o "jsonpath={.data.${FILE_KEY//./\\.}}" > "$workdir/current.json"
[ -s "$workdir/current.json" ] || { echo "error: $CONFIGMAP/$FILE_KEY is empty or unreadable" >&2; exit 1; }

CONSOLE_WEB_BASE_URL="$CONSOLE_WEB_BASE_URL" \
python3 - "$workdir/current.json" "$workdir/next.json" <<'PY'
import json, os, sys
current, target = sys.argv[1], sys.argv[2]
cfg = json.load(open(current))
delivery = cfg.setdefault("Aevatar", {}).setdefault("Delivery", {})
delivery.pop("ConsoleBaseUrl", None)
delivery["ConsoleWebBaseUrl"] = os.environ["CONSOLE_WEB_BASE_URL"]
# Creating this section is itself a behaviour change: the allowlist falls back to the
# shipped packages only while Aevatar:Delivery is ABSENT. Once the section exists it fails
# closed unless it names an allowlist or opts in, so writing the URLs alone would remove
# every deliverable package. Opt in explicitly, without touching a configured allowlist.
if "AllowedWorkflowNames" not in delivery:
    delivery["UseShippedWorkflowAllowlist"] = True
json.dump(cfg, open(target, "w"), indent=2, ensure_ascii=False)
PY

echo "--- Aevatar:Delivery diff ---"
diff <(python3 -c 'import json,sys; print(json.dumps(json.load(open(sys.argv[1])).get("Aevatar",{}).get("Delivery"), indent=2, sort_keys=True))' "$workdir/current.json") \
     <(python3 -c 'import json,sys; print(json.dumps(json.load(open(sys.argv[1])).get("Aevatar",{}).get("Delivery"), indent=2, sort_keys=True))' "$workdir/next.json") \
  || true

python3 - "$workdir/current.json" "$workdir/next.json" <<'PY'
import json, sys
a = json.load(open(sys.argv[1])); b = json.load(open(sys.argv[2]))
a.setdefault("Aevatar", {}).pop("Delivery", None); b["Aevatar"].pop("Delivery", None)
if a != b:
    raise SystemExit("error: the rewrite changed settings outside Aevatar:Delivery; refusing to apply")
print("verified: no setting outside Aevatar:Delivery changed")
PY

if [ "${APPLY:-0}" != "1" ]; then
  echo "dry run only. Re-run with APPLY=1 to write the ConfigMap and restart the deployment."
  exit 0
fi

kube create configmap "$CONFIGMAP" \
  --from-file="$FILE_KEY=$workdir/next.json" \
  --dry-run=client -o yaml | kube apply -f -
kube rollout restart deploy/aevatar-console-backend
kube rollout status deploy/aevatar-console-backend --timeout=600s
