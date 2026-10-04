#!/bin/bash

set -eu
umask 077

case "${AZURE_REGISTRY_NAME}" in
  ''|*[!a-zA-Z0-9]*) echo "Invalid Azure registry name." >&2; exit 1 ;;
esac

echo "Building openai-aca-lb:latest..."
az acr build --subscription "${AZURE_SUBSCRIPTION_ID}" --registry "${AZURE_REGISTRY_NAME}" --image openai-aca-lb:latest ./src/ --output none
image_name="${AZURE_REGISTRY_NAME}.azurecr.io/openai-aca-lb:latest"
script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
query=$(cat "${script_dir}/update-image.jmespath")
query=${query/__IMAGE__/$image_name}
update_file=$(mktemp)
trap 'rm -f "$update_file"' EXIT
trap 'exit 1' HUP INT TERM

az containerapp show --subscription "${AZURE_SUBSCRIPTION_ID}" --name "${SERVICE_WEB_NAME}" --resource-group "${RESOURCE_GROUP_NAME}" --query "$query" --output json > "$update_file"
az containerapp update --subscription "${AZURE_SUBSCRIPTION_ID}" --name "${SERVICE_WEB_NAME}" --resource-group "${RESOURCE_GROUP_NAME}" --yaml "$update_file" --output none
