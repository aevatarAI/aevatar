#!/usr/bin/env bash
# Ensure the deployed Mainnet ConfigMap carries the Workflow Delivery product-console URL.
#
# The deployment mounts appsettings.Distributed.json from a ConfigMap over the copy baked
# into the image, so the values committed in this repository never reach production.
# Without Aevatar:Delivery:ConsoleWebBaseUrl, delivery responses carry no product-console link.
#
# Only the product-console key is written and the obsolete connect-callback key is removed.
# Retired catalog keys are reported but preserved so this helper remains usable during a
# rolling deployment; every other setting is preserved semantically while the document is
# round-tripped through a JSON parser. Prints a diff and requires an
# explicit APPLY=1 to write.
set -euo pipefail

KUBECONFIG_PATH="${AEVATAR_PROD_KUBECONFIG:-$HOME/Code/aelf-shared-k8s-prod.yaml}"
NAMESPACE="${AEVATAR_PROD_NAMESPACE:-aismart-app-mainnet}"
CONFIGMAP="${AEVATAR_DELIVERY_CONFIGMAP:-aevatar-console-backend-appsettings-distributed}"
FILE_KEY="appsettings.Distributed.json"
CONSOLE_WEB_BASE_URL="${AEVATAR_DELIVERY_CONSOLE_WEB_BASE_URL:-https://aevatar-console.aevatar.ai}"

kube() { kubectl --kubeconfig="$KUBECONFIG_PATH" -n "$NAMESPACE" "$@"; }

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
legacy_keys = sorted(
    key for key in ("AllowedWorkflowNames", "UseShippedWorkflowAllowlist")
    if key in delivery
)
if legacy_keys:
    joined = ", ".join(legacy_keys)
    print(
        "warning: retired Aevatar:Delivery settings remain for rolling compatibility: " +
        joined + ". New hosts ignore them; remove them after the rollout because they do not "
        "configure typed packages.",
        file=sys.stderr,
    )
delivery.pop("ConsoleBaseUrl", None)
delivery["ConsoleWebBaseUrl"] = os.environ["CONSOLE_WEB_BASE_URL"]
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

FILE_KEY="$FILE_KEY" python3 - "$workdir/next.json" "$workdir/configmap-patch.json" <<'PY'
import json, os, sys
source, target = sys.argv[1], sys.argv[2]
with open(source, encoding="utf-8") as stream:
    content = stream.read()
patch = {"data": {os.environ["FILE_KEY"]: content}}
with open(target, "w", encoding="utf-8") as stream:
    json.dump(patch, stream, ensure_ascii=False)
PY

echo "delivery package prerequisite: this helper does not create Aevatar:Delivery:Packages or mount YAML sources."
echo "Each configured package requires a matching {workflowName}.yaml under Aevatar:Delivery:PackageDirectory before rollout."

if [ "${APPLY:-0}" != "1" ]; then
  echo "dry run only. Re-run with APPLY=1 to write the ConfigMap and restart the deployment."
  exit 0
fi

kube patch configmap "$CONFIGMAP" --type=merge --patch-file "$workdir/configmap-patch.json"
kube rollout restart deploy/aevatar-console-backend
kube rollout status deploy/aevatar-console-backend --timeout=600s
