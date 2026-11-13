#!/usr/bin/env bash
set -euo pipefail

# Flow Phase 0/1 prerequisite checker (Ubuntu target)
# Reports presence of tooling needed for eBPF prototype build/load and general development.

ok() { printf '[OK]    %s\n' "$*"; }
warn() { printf '[MISS]  %s\n' "$*"; }
info() { printf '[INFO]  %s\n' "$*"; }

have() { command -v "$1" >/dev/null 2>&1; }

info "Host: $(uname -srmo 2>/dev/null || uname -a)"
if [[ -r /etc/os-release ]]; then
  # shellcheck disable=SC1091
  . /etc/os-release
  info "OS: ${PRETTY_NAME:-unknown}"
fi

echo
info "Core development"
have dotnet && ok "dotnet: $(dotnet --version 2>/dev/null || echo present)" || warn "dotnet SDK/runtime not found"
have git && ok "git: $(git --version)" || warn "git not found"

echo
info "eBPF / CO-RE build prerequisites"
have clang && ok "clang: $(clang --version | head -n1)" || warn "clang (needed to compile BPF C objects)"
have llc && ok "llc: $(llc --version | head -n1)" || warn "llc (LLVM, often needed in BPF toolchains)"
have llvm-strip && ok "llvm-strip present" || warn "llvm-strip (useful for BPF objects)"
have bpftool && ok "bpftool: $(bpftool version 2>/dev/null | head -n1)" || warn "bpftool"
[[ -e /sys/kernel/btf/vmlinux ]] && ok "kernel BTF: /sys/kernel/btf/vmlinux" || warn "kernel BTF not found (CO-RE may be limited)"
[[ -e /usr/include/linux/bpf.h ]] && ok "linux/bpf.h headers present" || warn "linux/bpf.h not found"
ldconfig -p 2>/dev/null | grep -q 'libbpf\.so' && ok "libbpf runtime library present" || warn "libbpf runtime library not found"
# libbpf dev heuristics
if compgen -G "/usr/include/bpf/libbpf*.h" >/dev/null || [[ -e /usr/include/bpf/libbpf.h ]]; then
  ok "libbpf development headers present"
else
  warn "libbpf development headers (e.g. libbpf-dev) not detected"
fi

echo
info "Kernel privileges / settings"
if [[ -r /proc/sys/kernel/unprivileged_bpf_disabled ]]; then
  val=$(cat /proc/sys/kernel/unprivileged_bpf_disabled)
  info "unprivileged_bpf_disabled=${val} (2 typically means admin-only/unprivileged BPF disabled)"
else
  warn "cannot read /proc/sys/kernel/unprivileged_bpf_disabled"
fi

echo
info "Suggested Ubuntu packages to investigate (verify names/availability on target):"
echo "  - clang llvm (or versioned clang/llvm packages)"
echo "  - libbpf-dev linux-tools-common linux-tools-\$(uname -r) (as appropriate)"
echo "  - pkg-config build-essential"
echo
info "Note: package installation may require sudo and is not performed by this script."
