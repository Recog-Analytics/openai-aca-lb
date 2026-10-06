#!/usr/bin/env bash
set -euo pipefail
export DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH"
repo_root="$(cd "$(dirname "$0")/../.." && pwd)"
devkit_pid=""
lb_pid=""
dashboard_pid=""
cleanup() {
  trap - EXIT INT TERM
  if [[ -n "$lb_pid" ]]; then kill -TERM "$lb_pid" 2>/dev/null || true; fi
  if [[ -n "$devkit_pid" ]]; then kill -TERM "$devkit_pid" 2>/dev/null || true; fi
  if [[ -n "$dashboard_pid" ]]; then kill -TERM "$dashboard_pid" 2>/dev/null || true; fi
  if [[ -n "$lb_pid" ]]; then wait "$lb_pid" 2>/dev/null || true; fi
  if [[ -n "$devkit_pid" ]]; then wait "$devkit_pid" 2>/dev/null || true; fi
  if [[ -n "$dashboard_pid" ]]; then wait "$dashboard_pid" 2>/dev/null || true; fi
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM
cd "$repo_root"
# Build the dashboard web app when its output is missing or older than any of its sources.
web="$repo_root/dashboard/web"
built="$repo_root/dashboard/server/wwwroot/index.html"
if [[ ! -f "$built" ]] || [[ -n "$(find "$web/src" "$web/index.html" "$web/package.json" "$web/bun.lock" "$web/vite.config.ts" -newer "$built" -print -quit)" ]]; then
  command -v bun >/dev/null || { echo "bun is required to build the dashboard web app: https://bun.sh" >&2; exit 1; }
  (cd "$web" && bun install --frozen-lockfile && bun run build)
fi
dotnet build src/openai-loadbalancer.sln --nologo
(cd "$repo_root/dashboard/server" && ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS=http://localhost:5200 exec dotnet bin/Debug/net10.0/openai-dashboard.dll) &
dashboard_pid=$!
(cd "$repo_root/tools/devkit" && exec dotnet bin/Debug/net10.0/openai-devkit.dll) &
devkit_pid=$!
(cd "$repo_root/src" && ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS=http://localhost:5080 exec dotnet bin/Debug/net10.0/openai-loadbalancer.dll) &
lb_pid=$!
printf '\nDashboard: http://localhost:5200\nDevkit controls: http://localhost:5100\nLB: http://localhost:5080\nCtrl+C stops all processes.\n\n'
while kill -0 "$devkit_pid" 2>/dev/null && kill -0 "$lb_pid" 2>/dev/null && kill -0 "$dashboard_pid" 2>/dev/null; do sleep 1; done
# An early server exit also stops its peer and makes launch failure visible.
exit 1
