#!/bin/sh
# Builds a .deb from a published app directory.
# usage: build-deb.sh <publish-dir> <version> <deb-arch> <output-dir>
set -eu
src=$1; version=$2; arch=$3; out=$4
here=$(cd "$(dirname "$0")" && pwd)
root=$(mktemp -d)

install -d "$root/opt/millway" "$root/usr/bin" "$root/usr/share/applications" \
    "$root/usr/share/icons/hicolor/256x256/apps" "$root/lib/udev/rules.d" "$root/DEBIAN"
cp -r "$src"/. "$root/opt/millway/"
chmod 755 "$root/opt/millway/millway"
ln -s /opt/millway/millway "$root/usr/bin/millway"
install -m 644 "$here/millway.desktop" "$root/usr/share/applications/millway.desktop"
install -m 644 "$here/icon.png" "$root/usr/share/icons/hicolor/256x256/apps/millway.png"
install -m 644 "$here/70-millway.rules" "$root/lib/udev/rules.d/70-millway.rules"

cat > "$root/DEBIAN/control" <<EOF
Package: millway
Version: $version
Section: electronics
Priority: optional
Architecture: $arch
Depends: libx11-6, libice6, libsm6, libfontconfig1
Maintainer: Millway contributors <noreply@users.noreply.github.com>
Homepage: https://github.com/adman234/mmage-to-cmotion
Description: CNC machine control for GRBL routers
 Connects to Shapeoko and other GRBL 1.1 machines over USB to home, jog,
 zero, measure tools and run G-code programs, and accepts programs sent
 over the network.
EOF

cat > "$root/DEBIAN/postinst" <<'EOF'
#!/bin/sh
set -e
if command -v udevadm >/dev/null 2>&1; then
    udevadm control --reload-rules || true
    udevadm trigger --subsystem-match=tty || true
fi
EOF
chmod 755 "$root/DEBIAN/postinst"

mkdir -p "$out"
dpkg-deb --build --root-owner-group "$root" "$out/millway_${version}_${arch}.deb"
