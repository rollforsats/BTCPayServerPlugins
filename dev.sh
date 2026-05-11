#!/bin/bash
# Build and run BTCPay Server with all plugins in this repo loaded
set -e

REPO_ROOT="$(cd "$(dirname "$0")" && pwd)"
PLUGINS=""

for csproj in "$REPO_ROOT"/BTCPayServer.Plugins.*/BTCPayServer.Plugins.*.csproj; do
    [ -f "$csproj" ] || continue
    NAME="$(basename "${csproj%.csproj}")"

    # Skip the template
    [ "$NAME" = "BTCPayServer.Plugins.Template" ] && continue
    # Skip test projects — they aren't plugins
    case "$NAME" in *.Tests) continue;; esac

    echo "Building $NAME..."
    dotnet build "$(dirname "$csproj")"

    DLL="$(dirname "$csproj")/bin/Debug/net10.0/$NAME.dll"
    if [ ! -f "$DLL" ]; then
        echo "Error: Built DLL not found at $DLL" >&2
        exit 1
    fi
    if [ -n "$PLUGINS" ]; then
        PLUGINS="$PLUGINS;$DLL"
    else
        PLUGINS="$DLL"
    fi
done

if [ -z "$PLUGINS" ]; then
    echo "No plugins found to load."
    exit 1
fi

echo ""
echo "Loading plugins: $PLUGINS"
echo ""

export BTCPAY_DEBUG_PLUGINS="$PLUGINS"
cd "$REPO_ROOT/btcpayserver"
dotnet watch run --project BTCPayServer --launch-profile Bitcoin-HTTPS
