#!/bin/bash

set -eu
umask 077

case "${AZURE_REGISTRY_NAME}" in
  ''|*[!a-zA-Z0-9]*) echo "Invalid Azure registry name." >&2; exit 1 ;;
esac

script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
update_file=$(mktemp)
trap 'rm -f "$update_file"' EXIT
trap 'exit 1' HUP INT TERM

# deploy_image <image> <container app> <readiness path> <docker build arguments...>
deploy_image() {
  image=$1 app=$2 readiness=$3
  shift 3
  echo "Building ${image}:latest..."
  az acr build --subscription "${AZURE_SUBSCRIPTION_ID}" --registry "${AZURE_REGISTRY_NAME}" --image "${image}:latest" "$@" --output none
  query=$(cat "${script_dir}/update-image.jmespath")
  query=${query/__IMAGE__/${AZURE_REGISTRY_NAME}.azurecr.io/${image}:latest}
  query=${query/__READINESS_PATH__/$readiness}
  az containerapp show --subscription "${AZURE_SUBSCRIPTION_ID}" --name "$app" --resource-group "${RESOURCE_GROUP_NAME}" --query "$query" --output json > "$update_file"
  az containerapp update --subscription "${AZURE_SUBSCRIPTION_ID}" --name "$app" --resource-group "${RESOURCE_GROUP_NAME}" --yaml "$update_file" --output none
}

deploy_image openai-aca-lb "${SERVICE_WEB_NAME}" /readyz ./src/
deploy_image openai-aca-lb-dashboard "${SERVICE_DASHBOARD_NAME}" /healthz --file dashboard/Dockerfile .
