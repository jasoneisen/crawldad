#!/usr/bin/env bash
#
# Installs the Playwright Chromium build the real-browser tests need — WITHOUT PowerShell — by invoking the bundled
# Playwright Node driver that ships inside the built test output (the very driver the tests drive in
# tests/Crawldad.Tests/Integration/RealChromiumFixture.cs). This is the "Microsoft.Playwright.Program.Main install"
# route expressed through the driver the NuGet package already vendors, so CI needs neither `pwsh playwright.ps1` nor a
# globally installed dotnet tool.
#
# Prerequisite: the test project must already be built (the driver lives under its bin/<config>/net10.0/.playwright).
# Usage: eng/install-browsers.sh [Debug|Release]   (default: Release)
set -euo pipefail

CONFIG="${1:-Release}"
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT="${REPO_ROOT}/tests/Crawldad.Tests/bin/${CONFIG}/net10.0"
CLI="${OUT}/.playwright/package/cli.js"
# Resolve the vendored node binary without assuming the RID subfolder (linux-x64 / linux-arm64 / …).
NODE="$(find "${OUT}/.playwright/node" -maxdepth 2 -type f -name node 2>/dev/null | head -n1 || true)"

if [[ -z "${NODE}" || ! -f "${CLI}" ]]; then
  echo "error: Playwright driver not found under ${OUT}/.playwright" >&2
  echo "       Build the test project first, e.g.:" >&2
  echo "         dotnet build -c ${CONFIG} tests/Crawldad.Tests/Crawldad.Tests.csproj" >&2
  exit 1
fi

echo "Installing Playwright Chromium via: ${NODE} ${CLI} install --with-deps chromium"
exec "${NODE}" "${CLI}" install --with-deps chromium
