#!/usr/bin/env bash
# Install or update the Mini Player plasmoid for the current user (no root, no
# rpm-ostree layering - it lands in ~/.local/share/plasma/plasmoids/).
set -euo pipefail

DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ID="io.github.miniplayer"

if kpackagetool6 --type Plasma/Applet --list 2>/dev/null | grep -qx "$ID"; then
    echo "Updating $ID ..."
    kpackagetool6 --type Plasma/Applet --upgrade "$DIR"
else
    echo "Installing $ID ..."
    kpackagetool6 --type Plasma/Applet --install "$DIR"
fi

echo
echo "Done. Add it via: right-click panel > Add Widgets > search \"Mini Player\"."
echo "If it is already on a panel, reload the shell to pick up changes:"
echo "  systemctl --user restart plasma-plasmashell   # Wayland"
