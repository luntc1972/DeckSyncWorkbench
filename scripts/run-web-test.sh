#!/usr/bin/env bash
# Headless TEST launcher for DeckFlow web (http://localhost:5173).
#
# RULE: UI testing must NEVER open a browser on Windows. This script guarantees
# that by exporting DECKFLOW_DISABLE_AUTO_BROWSER=true (the gate read in
# Program.cs IsAutoBrowserDisabled) BEFORE starting the server, so no Edge/Chrome
# window pops on each start during Playwright / curl / headless verification.
# Use this for any automated or screenshot-driven UI testing instead of
# run-web.sh / run-web-uat.sh (which may auto-launch a Windows browser in
# Development).
#
# SECURITY: the admin creds below are local-machine-only PLACEHOLDERS, not
# secrets. NEVER put a real/prod password here (public repo). Override by
# exporting FEEDBACK_ADMIN_USER / FEEDBACK_ADMIN_PASSWORD before running.
set -euo pipefail

cd "$(dirname "$0")/.."

# THE point of this script: suppress the Development auto-browser launch.
export DECKFLOW_DISABLE_AUTO_BROWSER=true
export ASPNETCORE_ENVIRONMENT="${ASPNETCORE_ENVIRONMENT:-Development}"
export FEEDBACK_ADMIN_USER="${FEEDBACK_ADMIN_USER:-admin}"
export FEEDBACK_ADMIN_PASSWORD="${FEEDBACK_ADMIN_PASSWORD:-changeme-local}"

if [ -n "${DECKFLOW_E2E_PORT:-}" ]; then
  PORT="$DECKFLOW_E2E_PORT"
elif [ -n "${PORT:-}" ]; then
  PORT="$PORT"
else
  WEB_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../DeckFlow.Web" && pwd)"
  PORT_HASH=0
  for ((INDEX = 0; INDEX < ${#WEB_ROOT}; INDEX++)); do
    printf -v CHAR_CODE '%d' "'${WEB_ROOT:INDEX:1}"
    PORT_HASH=$(((PORT_HASH * 31 + CHAR_CODE) % 10000))
  done
  PORT=$((20000 + PORT_HASH))
fi

if [ "${FORCE_RESTART:-0}" != "1" ] && command -v curl >/dev/null 2>&1; then
  if curl --silent --show-error --location --output /dev/null --write-out '%{http_code}' "http://localhost:${PORT}/" | grep -Eq '^[23][0-9][0-9]$'; then
    echo "Reusing existing server on :${PORT} (set FORCE_RESTART=1 to replace)"
    exit 0
  fi
fi

# WSL exposes the Windows SDK as dotnet.exe; native Linux has dotnet. Prefer
# whichever is on PATH so the same script runs from WSL or Linux.
DOTNET="$(command -v dotnet 2>/dev/null || command -v dotnet.exe 2>/dev/null || true)"
if [ -z "$DOTNET" ]; then
  echo "error: neither 'dotnet' nor 'dotnet.exe' found on PATH" >&2
  exit 1
fi

# WSL-exported vars do not cross into Windows .exe processes unless named in WSLENV.
if [[ "$DOTNET" == *.exe || "$DOTNET" == *"/mnt/c/"* ]]; then
  export WSLENV="${WSLENV:+${WSLENV}:}DECKFLOW_DISABLE_AUTO_BROWSER:DECKFLOW_E2E_PORT:ASPNETCORE_ENVIRONMENT:FEEDBACK_ADMIN_USER:FEEDBACK_ADMIN_PASSWORD"
fi

# Free the port so a stale server does not block the bind (best-effort).
if command -v fuser >/dev/null 2>&1; then
  fuser -k "${PORT}/tcp" 2>/dev/null || true
  sleep 0.5
fi

echo "DeckFlow.Web (headless test mode) -> http://localhost:${PORT}"
echo "Auto-browser: DISABLED (DECKFLOW_DISABLE_AUTO_BROWSER=true). No Windows browser will open."
echo "Admin login: ${FEEDBACK_ADMIN_USER} / ${FEEDBACK_ADMIN_PASSWORD}"

# Explicit --urls (not a launch profile) so no profile-driven browser launch.
"$DOTNET" run --project DeckFlow.Web --urls "http://localhost:${PORT}"
