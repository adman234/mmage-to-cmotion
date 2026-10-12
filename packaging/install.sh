#!/bin/sh
# Installs Millway from the extracted tarball, for distributions without .deb support.
# Run as root:  sudo ./install.sh        Remove with:  sudo ./install.sh --uninstall
set -eu
here=$(cd "$(dirname "$0")" && pwd)
if [ "$(id -u)" -ne 0 ]; then echo "Please run as root (sudo ./install.sh)"; exit 1; fi

desktop=/usr/share/applications/millway.desktop
icon=/usr/share/icons/hicolor/256x256/apps/millway.png
rules=/etc/udev/rules.d/70-millway.rules

if [ "${1:-}" = "--uninstall" ]; then
    rm -r /opt/millway
    rm -f /usr/local/bin/millway "$desktop" "$icon" "$rules"
    echo "Millway removed."
    exit 0
fi

install -d /opt/millway /usr/share/applications /usr/share/icons/hicolor/256x256/apps /etc/udev/rules.d
cp -r "$here/app"/. /opt/millway/
chmod 755 /opt/millway/millway
ln -sf /opt/millway/millway /usr/local/bin/millway
install -m 644 "$here/millway.desktop" "$desktop"
install -m 644 "$here/icon.png" "$icon"
install -m 644 "$here/70-millway.rules" "$rules"
if command -v udevadm >/dev/null 2>&1; then
    udevadm control --reload-rules || true
    udevadm trigger --subsystem-match=tty || true
fi
echo "Millway installed. Start it from the application menu or run: millway"
