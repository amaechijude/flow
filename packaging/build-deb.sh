#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
stage="$repo_root/packaging/build/flow_0.1.0_amd64"
output="$repo_root/packaging/build/flow_0.1.0_amd64.deb"

rm -rf "$stage"
mkdir -p \
  "$stage/DEBIAN" \
  "$stage/usr/lib/flow/daemon" \
  "$stage/usr/lib/flow/desktop" \
  "$stage/usr/lib/flow/loader" \
  "$stage/usr/lib/systemd/system" \
  "$stage/usr/lib/systemd/user" \
  "$stage/usr/share/dbus-1/services" \
  "$stage/usr/share/applications" \
  "$stage/usr/share/doc/flow"

echo "[1/5] Publishing .NET projects"
dotnet publish "$repo_root/src/Flow.Daemon/Flow.Daemon.csproj" -c Release -o "$stage/usr/lib/flow/daemon" --nologo -v minimal
dotnet publish "$repo_root/src/Flow.Desktop/Flow.Desktop.csproj" -c Release -o "$stage/usr/lib/flow/desktop" --nologo -v minimal

echo "[2/5] Copying eBPF loader artifact"
loader="$repo_root/prototype/bpf/flow_tcp_bytes_loader"
if [[ ! -x "$loader" ]]; then
  echo "error: $loader not found or not executable; build prototype/bpf first (see prototype/bpf/README.md)" >&2
  exit 1
fi
cp "$loader" "$stage/usr/lib/flow/loader/flow_tcp_bytes_loader"

echo "[3/5] Copying service, D-Bus, desktop, and docs files"
cp "$repo_root/packaging/flow-collector.service" "$stage/usr/lib/systemd/system/"
cp "$repo_root/packaging/flow-daemon.service" "$stage/usr/lib/systemd/user/"
cp "$repo_root/packaging/org.flow.Daemon.service" "$stage/usr/share/dbus-1/services/"
cp "$repo_root/packaging/flow.desktop" "$stage/usr/share/applications/"
cp "$repo_root/packaging/deb/DEBIAN/control" "$stage/DEBIAN/control"
cp "$repo_root/packaging/postinst.sh" "$stage/DEBIAN/postinst"
cp "$repo_root/packaging/prerm.sh" "$stage/DEBIAN/prerm"
cp "$repo_root/README.md" "$stage/usr/share/doc/flow/README.md"

echo "[4/5] Setting permissions"
find "$stage" -type d -exec chmod 0755 {} +
find "$stage" -type f -exec chmod 0644 {} +
chmod 0755 "$stage/usr/lib/flow/daemon/Flow.Daemon"
chmod 0755 "$stage/usr/lib/flow/desktop/Flow.Desktop"
chmod 0755 "$stage/usr/lib/flow/loader/flow_tcp_bytes_loader"
chmod 0755 "$stage/DEBIAN/postinst" "$stage/DEBIAN/prerm"
if command -v desktop-file-validate >/dev/null 2>&1; then
  desktop-file-validate "$stage/usr/share/applications/flow.desktop"
fi

echo "[5/5] Building $output"
dpkg-deb --build --root-owner-group "$stage" "$output"
dpkg-deb --info "$output" | sed -n '1,20p'
echo "built: $output"
