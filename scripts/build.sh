#!/usr/bin/env bash
# Build and test on Linux without installing the .NET SDK: everything runs in the official SDK image.
#
#   scripts/build.sh build            # restore + build every project (plugin included; it compiles on Linux)
#   scripts/build.sh test             # build + run the platform-neutral core tests (the only ones that can run here)
#   scripts/build.sh test --filter 'FullyQualifiedName~Protocol'
#   scripts/build.sh shell            # a shell in the container with the repo at /src
#
# The core integration tests need the telescope_control server: build it once with
# `cargo build -p telescope-server` in ../telescope_control (or point TELESCOPE_CONTROL at the checkout).
# They skip with a reason when it is missing.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
image="${DOTNET_SDK_IMAGE:-mcr.microsoft.com/dotnet/sdk:10.0}"
tc="${TELESCOPE_CONTROL:-$(cd "$here/.." && pwd)/telescope_control}"
nuget="${NUGET_PACKAGES_DIR:-$HOME/.nuget/packages}"
home="${here}/.dotnet-home"
mkdir -p "$nuget" "$home"

mounts=(-v "$here:/src" -v "$nuget:/nuget" -v "$home:/tmp/home")
env=(-e HOME=/tmp/home -e DOTNET_CLI_HOME=/tmp/home -e NUGET_PACKAGES=/nuget
     -e DOTNET_CLI_TELEMETRY_OPTOUT=1 -e DOTNET_NOLOGO=1 -e DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1)
if [ -d "$tc" ]; then
  mounts+=(-v "$tc:/telescope_control:ro")
  env+=(-e TELESCOPE_SERVER_BIN=/telescope_control/target/debug/telescope-server -e TELESCOPE_LIBRARY=/telescope_control/library)
fi

tty=()
if [ -t 0 ] && [ -t 1 ]; then tty=(-it); fi

run() {
  docker run --rm "${tty[@]}" --user "$(id -u):$(id -g)" "${env[@]}" "${mounts[@]}" -w /src "$image" "$@"
}

cmd="${1:-build}"; shift || true
case "$cmd" in
  build)
    run dotnet build ObsServicesPlugin.slnx -c "${CONFIGURATION:-Debug}" "$@" ;;
  test)
    run dotnet test tests/ObsServices.Core.Tests/ObsServices.Core.Tests.csproj -c "${CONFIGURATION:-Debug}" "$@" ;;
  shell)
    run bash "$@" ;;
  *)
    run dotnet "$cmd" "$@" ;;
esac
