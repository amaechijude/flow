#!/usr/bin/env python3
"""Controlled validation for the Flow eBPF TCP+UDP attribution prototype.

Runs known-size TCP and UDP transfers while the privileged loader samples, then
compares per-process attributed bytes against the expected payloads and interface
counters. It also verifies interface attribution and records loader CPU time and
peak RSS as a first overhead datapoint.

Default mode uses loopback `lo` for both directions. `--mode veth` creates a
temporary veth pair split across two network namespaces; the server runs in the
root namespace on flowvA (192.0.2.1) and a client helper runs in `flowvns` on
flowvB (192.0.2.2). This forces traffic over two distinct interfaces. Because
`ifindex` is namespace-scoped, the helper side is reported as `ifindex@netns`.

This script MUST run as root (or with CAP_BPF/CAP_PERFMON/CAP_SYS_ADMIN) because
it loads and attaches BPF programs. It does not modify the system beyond loading
the prototype program for the duration of the run.

Usage:
    sudo python3 scripts/validate-ebpf-attribution.py --size-mib 16
    sudo python3 scripts/validate-ebpf-attribution.py --size-mib 16 --mode veth
    sudo python3 scripts/validate-ebpf-attribution.py --mode soak --duration 300 --soak-size-mib 1
    sudo python3 scripts/validate-ebpf-attribution.py --mode nic --peer-host <gateway-ip> --peer-port 9000 --peer-udp-port 9001 --interface wlp0s20f3
    sudo python3 scripts/validate-ebpf-attribution.py --mode netns-key --netns-size-mib 1
    sudo python3 scripts/validate-ebpf-attribution.py --mode map-churn --churn-target 9000
    sudo python3 scripts/validate-ebpf-attribution.py --mode wildcard --wildcard-size-mib 1
    sudo python3 scripts/validate-ebpf-attribution.py --mode tunnel --tunnel-size-mib 16
    sudo python3 scripts/validate-ebpf-attribution.py --mode prod-soak --duration 300 --prod-workers 8
    sudo python3 scripts/validate-ebpf-attribution.py --mode identity-diag --duration 30 --prod-workers 8
    sudo python3 scripts/validate-ebpf-attribution.py --mode wireguard --tunnel-size-mib 16

Output: a Markdown report (default under docs/measurements/) plus a JSON summary on stdout.

Soak mode repeats small loopback TCP/UDP transfers for `--duration` seconds and
periodically spawns short-lived child processes (`--soak-child-every`) to create
process churn. It samples loader CPU, peak RSS, and BPF map entry count (emitted
by the loader as `X` lines) to provide steady-state overhead/map-growth evidence.
Exactness checks cover only the parent TGID; child rows are reported as background.

NIC mode sends known TCP and UDP payloads through the default-route physical
interface to a peer echo listener (e.g. a phone hotspot gateway running TCP and
UDP echo servers) and requires the expected rows to resolve to that interface in
the root netns. Peer/interface are inferred from the default route or supplied
with `--peer-host`/`--interface`. If the peer reports byte counts, pass them with
`--peer-bytes`/`--peer-udp-bytes` to include them as checks.

Netns-key mode creates two temporary netns with loopback and runs one helper
process in both via setns(), keeping TGID/start/UID identical. It requires two
distinct counter rows with exact bytes, proving netns participates in the
identity key. It is TCP-only and privileged.

Map-churn mode spawns many short-lived TCP+UDP children in batches until the
BPF map reaches `--churn-target` or `--churn-timeout` expires. It samples map
entries, CPU, and RSS, and checks that child byte totals still match expected
payloads. It is an observation of map capacity behavior, not a bounded-memory
claim.

Wildcard mode creates a veth pair across two netns and uses one UDP server
bound to 0.0.0.0 in the root netns. Clients on loopback and on the veth peer
send known payloads; the run requires two RX rows for the same server process
with different interface names, validating per-datagram interface attribution
for wildcard-bound sockets.

Tunnel mode builds a GRE (or IPIP fallback) tunnel over a veth underlay between
two netns, runs a TCP echo server in the peer netns on the tunnel address, and
asserts that process rows resolve to the tunnel device rather than the underlay.
Overlay and underlay counter deltas are both reported to document overlap.

Production-soak mode runs several concurrent transfer workers for a configurable
duration, sampling CPU, RSS, and map growth. It is a loopback concurrency/duration
proxy for overhead, not a real Internet workload or production SLA claim.

Identity-diagnostic mode runs the same short concurrent workload but reports
BPF `start_ns`, loader `startticks`, computed ticks, `/proc` starttime/exe, and
the verification flag for each expected row. It is a measurement aid, not a fix.

WireGuard mode creates real WireGuard kernel devices in the root and peer netns
over a veth underlay, generates temporary keys, and runs a TCP echo server on the
WireGuard address. It asserts process rows resolve to `wg0` rather than the
underlay, and reports overlay/underlay counter overlap.
"""

from __future__ import annotations

import argparse
import ctypes
import json
import os
import signal
import socket
import subprocess
import sys
import threading
import time
from datetime import datetime, timezone
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[1]
DEFAULT_LOADER = REPO_ROOT / "prototype" / "bpf" / "flow_tcp_bytes_loader"
DEFAULT_REPORT_DIR = REPO_ROOT / "docs" / "measurements"

PROTO_NAMES = {1: "tcp", 2: "udp"}


def read_net_dev(interface: str = "lo") -> dict[str, int] | None:
    """Return rx/tx byte counters for one interface from /proc/net/dev."""
    try:
        lines = Path("/proc/net/dev").read_text(encoding="utf-8").splitlines()
    except OSError:
        return None

    for line in lines[2:]:
        name, _, rest = line.partition(":")
        if name.strip() != interface:
            continue
        fields = rest.split()
        if len(fields) < 9:
            return None
        return {"rx_bytes": int(fields[0]), "tx_bytes": int(fields[8])}
    return None


def read_net_dev_netns(interface: str, netns: str) -> dict[str, int] | None:
    result = subprocess.run(
        ["ip", "netns", "exec", netns, "cat", "/proc/net/dev"],
        capture_output=True, text=True, check=False,
    )
    if result.returncode != 0:
        return None
    for line in result.stdout.splitlines()[2:]:
        name, _, rest = line.partition(":")
        if name.strip() != interface:
            continue
        fields = rest.split()
        if len(fields) < 9:
            return None
        return {"rx_bytes": int(fields[0]), "tx_bytes": int(fields[8])}
    return None


def read_proc_cpu_ns(pid: int) -> int | None:
    """Return CPU time in nanoseconds from /proc/<pid>/schedstat (run time field)."""
    try:
        first = Path(f"/proc/{pid}/schedstat").read_text(encoding="utf-8").split()[0]
        return int(first)
    except (OSError, ValueError, IndexError):
        return None


def read_proc_startticks(pid: int) -> int | None:
    """Return /proc/<pid>/stat field 22 (starttime in clock ticks)."""
    try:
        stat = Path(f"/proc/{pid}/stat").read_text(encoding="utf-8")
        rparen = stat.rfind(")")
        if rparen < 0:
            return None
        fields = stat[rparen + 2 :].split()
        # fields[0] is state (field 3); field 22 is at index 19.
        return int(fields[19])
    except (OSError, ValueError, IndexError):
        return None


def read_proc_cpu_seconds(pid: int) -> float | None:
    """Return utime+stime in seconds for a process, as reported by /proc/<pid>/stat."""
    try:
        stat = Path(f"/proc/{pid}/stat").read_text(encoding="utf-8")
        rparen = stat.rfind(")")
        if rparen < 0:
            return None
        fields = stat[rparen + 2 :].split()
        # After comm: index 0 is state (field 3); utime/stime are fields 14/15.
        ticks = int(fields[11]) + int(fields[12])
        return ticks / os.sysconf("SC_CLK_TCK")
    except (OSError, ValueError, IndexError):
        return None


def read_proc_exe(pid: int) -> str | None:
    try:
        return os.readlink(f"/proc/{pid}/exe")
    except OSError:
        return None


def read_proc_peak_rss_kb(pid: int) -> int | None:
    try:
        for line in Path(f"/proc/{pid}/status").read_text(encoding="utf-8").splitlines():
            if line.startswith("VmHWM:"):
                return int(line.split()[1])
    except (OSError, ValueError):
        pass
    return None


VETH_CLIENT = "flowvA"
VETH_SERVER = "flowvB"
VETH_CLIENT_IP = "192.0.2.1"
VETH_SERVER_IP = "192.0.2.2"


def _ip(*args: str) -> None:
    result = subprocess.run(
        ["ip", *args], capture_output=True, text=True, check=False
    )
    if result.returncode != 0:
        raise SystemExit(
            f"ip {' '.join(args)} failed: {(result.stderr or result.stdout).strip()}"
        )


VETH_NS = "flowvns"


class VethPair:
    """Temporary veth pair split across two network namespaces.

    flowvA stays in the root namespace with 192.0.2.1/24; flowvB moves into
    flowvns with 192.0.2.2/24. Traffic between them must traverse the veth
    pair, so TX and RX genuinely use distinct interfaces.
    """

    def __init__(self) -> None:
        self.server_ifindex = 0
        self.client_ifindex = 0

    def start(self) -> None:
        namespaces = subprocess.run(
            ["ip", "-o", "netns", "list"], capture_output=True, text=True, check=False
        ).stdout
        if any(line.split(":")[0].strip() == VETH_NS for line in namespaces.splitlines()):
            raise SystemExit(f"netns {VETH_NS} already exists; refusing to modify it")
        for name in (VETH_CLIENT, VETH_SERVER):
            existing = subprocess.run(
                ["ip", "-o", "link", "show", name],
                capture_output=True,
                text=True,
                check=False,
            )
            if existing.returncode == 0:
                raise SystemExit(
                    f"interface {name} already exists; refusing to modify it"
                )
        _ip("link", "add", VETH_CLIENT, "type", "veth", "peer", "name", VETH_SERVER)
        try:
            _ip("netns", "add", VETH_NS)
            _ip("link", "set", VETH_SERVER, "netns", VETH_NS)
            _ip("addr", "add", f"{VETH_CLIENT_IP}/24", "dev", VETH_CLIENT)
            _ip("link", "set", VETH_CLIENT, "up")
            _ip("netns", "exec", VETH_NS, "ip", "link", "set", "lo", "up")
            _ip("netns", "exec", VETH_NS, "ip", "addr", "add", f"{VETH_SERVER_IP}/24", "dev", VETH_SERVER)
            _ip("netns", "exec", VETH_NS, "ip", "link", "set", VETH_SERVER, "up")
        except SystemExit:
            self.stop()
            raise
        detail = subprocess.run(
            ["ip", "netns", "exec", VETH_NS, "ip", "-o", "link", "show", VETH_SERVER],
            capture_output=True,
            text=True,
            check=False,
        )
        if detail.returncode == 0 and detail.stdout.strip():
            self.server_ifindex = int(detail.stdout.split(":", 1)[0].strip())
        client_detail = subprocess.run(
            ["ip", "-o", "link", "show", VETH_CLIENT],
            capture_output=True,
            text=True,
            check=False,
        )
        if client_detail.returncode == 0 and client_detail.stdout.strip():
            self.client_ifindex = int(client_detail.stdout.split(":", 1)[0].strip())
        time.sleep(0.2)

    def stop(self) -> None:
        subprocess.run(
            ["ip", "netns", "delete", VETH_NS],
            capture_output=True,
            text=True,
            check=False,
        )
        subprocess.run(
            ["ip", "link", "del", VETH_CLIENT],
            capture_output=True,
            text=True,
            check=False,
        )


NETNS_KEY_A = "flowkeyA"
NETNS_KEY_B = "flowkeyB"


TUNNEL_NAME = "flowtun0"
TUNNEL_ROOT_IP = "10.0.0.1"
TUNNEL_PEER_IP = "10.0.0.2"


class TunnelTopology:
    """Veth underlay plus a GRE (or IPIP fallback) tunnel between two netns."""

    def __init__(self) -> None:
        self.veth = VethPair()
        self.mode = "gre"
        self.root_ifindex = 0
        self.peer_ifindex = 0

    def _create_tunnel(self, mode: str, netns: bool) -> None:
        local = VETH_CLIENT_IP if not netns else VETH_SERVER_IP
        remote = VETH_SERVER_IP if not netns else VETH_CLIENT_IP
        addr = TUNNEL_ROOT_IP if not netns else TUNNEL_PEER_IP
        # _ip() prepends the outer "ip"; netns execution needs an inner "ip" too.
        prefix = ["netns", "exec", VETH_NS, "ip"] if netns else []
        underlay = VETH_CLIENT if not netns else VETH_SERVER
        if mode == "vxlan":
            _ip(
                *prefix, "link", "add", TUNNEL_NAME, "type", "vxlan",
                "id", "4242", "local", local, "remote", remote,
                "dev", underlay, "dstport", "4789",
            )
        else:
            _ip(*prefix, "tunnel", "add", TUNNEL_NAME, "mode", mode, "local", local, "remote", remote)
        _ip(*prefix, "addr", "add", f"{addr}/30", "dev", TUNNEL_NAME)
        _ip(*prefix, "link", "set", TUNNEL_NAME, "up")

    def start(self) -> None:
        self.veth.start()
        errors: list[str] = []
        for mode in ("gre", "ipip", "vxlan"):
            try:
                self._create_tunnel(mode, netns=False)
                self._create_tunnel(mode, netns=True)
                self.mode = mode
                break
            except SystemExit as exc:
                errors.append(f"{mode}: {exc}")
                self.stop_tunnels()
        else:
            self.veth.stop()
            raise SystemExit(
                "could not create GRE, IPIP, or VXLAN tunnel in the test netns:\n"
                + "\n".join(errors)
            )
        self.root_ifindex = self._ifindex(netns=False)
        self.peer_ifindex = self._ifindex(netns=True)
        time.sleep(0.2)

    @staticmethod
    def _ifindex(netns: bool) -> int:
        cmd = ["ip", "-o", "link", "show", TUNNEL_NAME]
        if netns:
            cmd = ["ip", "netns", "exec", VETH_NS, *cmd]
        result = subprocess.run(cmd, capture_output=True, text=True, check=False)
        if result.returncode == 0 and result.stdout.strip():
            return int(result.stdout.split(":", 1)[0].strip())
        return 0

    def stop_tunnels(self) -> None:
        for delete in (["ip", "tunnel", "del", TUNNEL_NAME], ["ip", "link", "del", TUNNEL_NAME]):
            subprocess.run(delete, capture_output=True, text=True, check=False)
            subprocess.run(
                ["ip", "netns", "exec", VETH_NS, *delete[1:]],
                capture_output=True, text=True, check=False,
            )

    def stop(self) -> None:
        self.stop_tunnels()
        self.veth.stop()


WG_NAME = "flowwg0"
WG_ROOT_IP = "10.7.0.1"
WG_PEER_IP = "10.7.0.2"
WG_ROOT_PORT = 51821
WG_PEER_PORT = 51820


def wg_keygen() -> tuple[str, str]:
    priv = subprocess.run(["wg", "genkey"], capture_output=True, text=True, check=True).stdout.strip()
    pub = subprocess.run(
        ["wg", "pubkey"], input=priv + "\n", capture_output=True, text=True, check=True
    ).stdout.strip()
    return priv, pub


class WireGuardTopology:
    """Real WireGuard kernel devices over a veth underlay between two netns."""

    def __init__(self) -> None:
        self.veth = VethPair()
        self.root_ifindex = 0
        self.peer_ifindex = 0

    def _run(self, cmd: list[str], stdin_text: str | None = None) -> None:
        result = subprocess.run(
            cmd, input=stdin_text, capture_output=True, text=True, check=False
        )
        if result.returncode != 0:
            raise SystemExit(
                f"{' '.join(cmd)} failed: {(result.stderr or result.stdout).strip()}"
            )

    def start(self) -> None:
        self.veth.start()
        root_priv, root_pub = wg_keygen()
        peer_priv, peer_pub = wg_keygen()

        try:
            self._run(["ip", "link", "add", WG_NAME, "type", "wireguard"])
            self._run(["ip", "netns", "exec", VETH_NS, "ip", "link", "add", WG_NAME, "type", "wireguard"])
            self._run(["ip", "addr", "add", f"{WG_ROOT_IP}/30", "dev", WG_NAME])
            self._run(["ip", "netns", "exec", VETH_NS, "ip", "addr", "add", f"{WG_PEER_IP}/30", "dev", WG_NAME])
            # Pass keys via /dev/stdin to avoid file-permission/apparmor issues in /tmp.
            self._run(
                ["wg", "set", WG_NAME, "private-key", "/dev/stdin",
                 "listen-port", str(WG_ROOT_PORT), "peer", peer_pub,
                 "endpoint", f"{VETH_SERVER_IP}:{WG_PEER_PORT}",
                 "allowed-ips", f"{WG_PEER_IP}/32"],
                stdin_text=root_priv + "\n",
            )
            self._run(
                ["ip", "netns", "exec", VETH_NS, "wg", "set", WG_NAME,
                 "private-key", "/dev/stdin", "listen-port", str(WG_PEER_PORT),
                 "peer", root_pub, "allowed-ips", f"{WG_ROOT_IP}/32"],
                stdin_text=peer_priv + "\n",
            )
            self._run(["ip", "link", "set", WG_NAME, "up"])
            self._run(["ip", "netns", "exec", VETH_NS, "ip", "link", "set", WG_NAME, "up"])
        except SystemExit:
            self.stop()
            raise

        self.root_ifindex = self._ifindex(False)
        self.peer_ifindex = self._ifindex(True)
        time.sleep(0.5)

    @staticmethod
    def _ifindex(netns: bool) -> int:
        cmd = ["ip", "-o", "link", "show", WG_NAME]
        if netns:
            cmd = ["ip", "netns", "exec", VETH_NS, *cmd]
        result = subprocess.run(cmd, capture_output=True, text=True, check=False)
        if result.returncode == 0 and result.stdout.strip():
            return int(result.stdout.split(":", 1)[0].strip())
        return 0

    def stop(self) -> None:
        subprocess.run(["ip", "link", "del", WG_NAME], capture_output=True, text=True, check=False)
        subprocess.run(
            ["ip", "netns", "exec", VETH_NS, "ip", "link", "del", WG_NAME],
            capture_output=True, text=True, check=False,
        )
        self.veth.stop()


class NetnsKeyPair:
    """Two temporary netns with only loopback, for identity-collision runs.

    A single helper process moves between them with setns(), keeping TGID,
    start time and UID identical. Only netns changes.
    """

    def start(self) -> None:
        existing = subprocess.run(
            ["ip", "-o", "netns", "list"], capture_output=True, text=True, check=False
        ).stdout
        names = {line.split(":")[0].strip() for line in existing.splitlines()}
        for name in (NETNS_KEY_A, NETNS_KEY_B):
            if name in names:
                raise SystemExit(f"netns {name} already exists; refusing to modify it")
        try:
            for name in (NETNS_KEY_A, NETNS_KEY_B):
                _ip("netns", "add", name)
                _ip("netns", "exec", name, "ip", "link", "set", "lo", "up")
        except SystemExit:
            self.stop()
            raise
        time.sleep(0.2)

    @property
    def paths(self) -> tuple[str, str]:
        return (f"/var/run/netns/{NETNS_KEY_A}", f"/var/run/netns/{NETNS_KEY_B}")

    def stop(self) -> None:
        for name in (NETNS_KEY_A, NETNS_KEY_B):
            subprocess.run(
                ["ip", "netns", "delete", name],
                capture_output=True,
                text=True,
                check=False,
            )


class LoaderRun:
    """Starts the prototype loader in TSV mode and collects its snapshot events."""

    def __init__(self, loader: Path, interval: float, rounds: int = 1_000_000) -> None:
        self.loader = loader
        self.interval = interval
        self.rounds = rounds
        self.proc: subprocess.Popen[str] | None = None
        self.entries: list[dict[str, object]] = []
        self.unattributed: list[dict[str, int]] = []
        self.map_samples: list[dict[str, int]] = []
        self.overflow_samples: list[dict[str, int]] = []
        self.meta: dict[str, int] = {}
        self.stderr: list[str] = []
        self._threads: list[threading.Thread] = []

    def start(self) -> None:
        if not self.loader.exists():
            raise SystemExit(f"loader not found: {self.loader} (build prototype/bpf first)")
        self.proc = subprocess.Popen(
            [str(self.loader), str(self.interval), str(self.rounds), "tsv"],
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            bufsize=1,
        )
        self._threads = [
            threading.Thread(target=self._read_stdout, daemon=True),
            threading.Thread(target=self._read_stderr, daemon=True),
        ]
        for t in self._threads:
            t.start()

        deadline = time.time() + 10
        while not self.meta and time.time() < deadline:
            if self.proc.poll() is not None:
                raise SystemExit(
                    "loader exited before emitting metadata; stderr:\n" + "\n".join(self.stderr)
                )
            time.sleep(0.05)

    def _read_stdout(self) -> None:
        assert self.proc is not None and self.proc.stdout is not None
        for line in self.proc.stdout:
            line = line.rstrip("\n")
            if line.startswith("M\t"):
                p = line.split("\t")
                self.meta = {
                    "clk_tck": int(p[1]) if len(p) > 1 and p[1].isdigit() else 100,
                    "boot_unix_ms": int(p[2]) if len(p) > 2 and p[2].isdigit() else 0,
                }
            elif line.startswith("E\t"):
                p = line.split("\t")
                if len(p) < 11:
                    continue
                self.entries.append(
                    {
                        "tgid": int(p[1]),
                        "uid": int(p[2]),
                        "tx_delta": int(p[5]),
                        "rx_delta": int(p[6]),
                        "comm": p[7],
                        "exe": p[8],
                        "startticks": int(p[9]) if p[9].isdigit() else 0,
                        "identity_verified": p[10] == "1",
                        "start_ns": int(p[11]) if len(p) > 11 and p[11].isdigit() else 0,
                        "cgroup_id": p[12] if len(p) > 12 else "",
                        # proto/ifname were added with UDP/per-interface accounting.
                        "proto": int(p[13]) if len(p) > 13 and p[13].isdigit() else 0,
                        "ifname": p[14] if len(p) > 14 else "",
                        # netns id (16th field) distinguishes namespace-scoped ifindexes.
                        "netns_id": int(p[15]) if len(p) > 15 and p[15].isdigit() else 0,
                        "raw_startticks": int(p[16]) if len(p) > 16 and p[16].isdigit() else 0,
                    }
                )
            elif line.startswith("U\t"):
                p = line.split("\t")
                if len(p) < 5:
                    continue
                self.unattributed.append(
                    {"tx_delta": int(p[3]), "rx_delta": int(p[4])}
                )
            elif line.startswith("F\t"):
                # F <snapshot> <tx_total> <rx_total> <tx_delta> <rx_delta> <events...>
                p = line.split("\t")
                if len(p) >= 8:
                    self.overflow_samples.append(
                        {
                            "snapshot": int(p[1]),
                            "tx_total": int(p[2]),
                            "rx_total": int(p[3]),
                            "tx_delta": int(p[4]),
                            "rx_delta": int(p[5]),
                            "tx_events": int(p[6]),
                            "rx_events": int(p[7]),
                        }
                    )
            elif line.startswith("X\t"):
                # X <snapshot> <map_entries> <unattributed_tx> <unattributed_rx>
                p = line.split("\t")
                if len(p) >= 5:
                    self.map_samples.append(
                        {
                            "snapshot": int(p[1]),
                            "entries": int(p[2]),
                            "utx": int(p[3]),
                            "urx": int(p[4]),
                        }
                    )

    def _read_stderr(self) -> None:
        assert self.proc is not None and self.proc.stderr is not None
        for line in self.proc.stderr:
            self.stderr.append(line.rstrip("\n"))

    def cpu_seconds(self) -> float | None:
        if self.proc is None:
            return None
        cpu_ns = read_proc_cpu_ns(self.proc.pid)
        if cpu_ns is not None:
            return cpu_ns / 1_000_000_000.0
        return read_proc_cpu_seconds(self.proc.pid)

    def peak_rss_kb(self) -> int | None:
        return read_proc_peak_rss_kb(self.proc.pid) if self.proc else None

    def stop(self) -> None:
        if self.proc is None:
            return
        if self.proc.poll() is None:
            self.proc.send_signal(signal.SIGINT)
            try:
                self.proc.wait(timeout=max(10.0, self.interval * 4))
            except subprocess.TimeoutExpired:
                self.proc.kill()
                self.proc.wait(timeout=5)
        for t in self._threads:
            t.join(timeout=5)


def run_client_helper(params: list[str]) -> int:
    """Client side of the netns veth probe. Runs inside the helper namespace."""
    proto, host, port, size = params
    print(f"HELPER_PID {os.getpid()}", flush=True)
    size_int = int(size)
    port_int = int(port)

    if proto == "tcp":
        with socket.create_connection((host, port_int), timeout=30) as client:
            chunk = b"x" * (1 << 20)
            sent = 0
            while sent < size_int:
                n = min(len(chunk), size_int - sent)
                client.sendall(chunk[:n])
                sent += n
            client.shutdown(socket.SHUT_WR)
    else:
        client = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        try:
            client.connect((host, port_int))
            payload = b"u" * 4096
            sent = 0
            datagrams = 0
            while sent < size_int:
                n = min(len(payload), size_int - sent)
                sent += client.send(payload[:n])
                datagrams += 1
                if datagrams % 64 == 0:
                    time.sleep(0.001)
        finally:
            client.close()

    # Stay alive until the parent has sampled snapshots, so identity enrichment
    # can verify this process generation.
    sys.stdin.readline()
    return 0


def spawn_client_helper(proto: str, host: str, port: int, size: int) -> tuple[subprocess.Popen[str], int]:
    command = [
        "ip", "netns", "exec", VETH_NS,
        sys.executable, str(Path(__file__).resolve()),
        "--client-helper", proto, host, str(port), str(size),
    ]
    proc = subprocess.Popen(
        command,
        stdin=subprocess.PIPE,
        stdout=subprocess.PIPE,
        stderr=subprocess.DEVNULL,
        text=True,
        bufsize=1,
    )
    line = proc.stdout.readline().strip() if proc.stdout else ""
    if not line.startswith("HELPER_PID "):
        proc.kill()
        raise SystemExit(f"client helper failed to start: {(line or 'no output')!r}")
    return proc, int(line.split()[1])


CLONE_NEWNET = 0x40000000


def set_netns(path: str) -> None:
    fd = os.open(path, os.O_RDONLY)
    try:
        if ctypes.CDLL(None, use_errno=True).setns(fd, CLONE_NEWNET) != 0:
            err = ctypes.get_errno()
            raise OSError(err, f"setns({path}) failed: {os.strerror(err)}")
    finally:
        os.close(fd)


def run_netns_key_helper(path_a: str, path_b: str, size: int) -> int:
    """Same process/TGID sends in two netns; only netns changes between runs."""
    print(f"HELPER_PID {os.getpid()}", flush=True)
    for path in (path_a, path_b):
        set_netns(path)
        sent, received, _ = run_tcp_transfer(size)
        print(f"HELPER_XFER {path} {sent} {received}", flush=True)
        time.sleep(2)
    # Stay alive so the loader can emit snapshots for both netns rows.
    sys.stdin.readline()
    return 0


def run_tunnel_server(port: int, size: int) -> int:
    """TCP echo server inside the tunnel peer netns."""
    print(f"SERVER_PID {os.getpid()}", flush=True)
    server = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    server.bind((TUNNEL_PEER_IP, port))
    server.listen(1)
    server.settimeout(30)
    conn, _ = server.accept()
    received = bytearray()
    try:
        while True:
            data = conn.recv(1 << 20)
            if not data:
                break
            received.extend(data)
        conn.sendall(received)
        print(f"SERVER_BYTES {len(received)}", flush=True)
    finally:
        conn.close()
        server.close()
    sys.stdin.readline()
    return 0


def run_wg_server(host: str, port: int) -> int:
    print(f"SERVER_PID {os.getpid()}", flush=True)
    server = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    server.bind((host, port))
    server.listen(1)
    server.settimeout(30)
    conn, _ = server.accept()
    received = bytearray()
    try:
        while True:
            data = conn.recv(1 << 20)
            if not data:
                break
            received.extend(data)
        conn.sendall(received)
        print(f"SERVER_BYTES {len(received)}", flush=True)
    finally:
        conn.close()
        server.close()
    sys.stdin.readline()
    return 0


def spawn_wg_server(host: str, port: int) -> tuple[subprocess.Popen[str], int]:
    proc = subprocess.Popen(
        [
            "ip", "netns", "exec", VETH_NS,
            sys.executable, str(Path(__file__).resolve()),
            "--wg-server", host, str(port),
        ],
        stdin=subprocess.PIPE,
        stdout=subprocess.PIPE,
        stderr=subprocess.DEVNULL,
        text=True,
        bufsize=1,
    )
    line = proc.stdout.readline().strip() if proc.stdout else ""
    if not line.startswith("SERVER_PID "):
        proc.kill()
        raise SystemExit(f"WireGuard server failed to start: {(line or 'no output')!r}")
    return proc, int(line.split()[1])


def spawn_tunnel_server(port: int, size: int) -> tuple[subprocess.Popen[str], int]:
    proc = subprocess.Popen(
        [
            "ip", "netns", "exec", VETH_NS,
            sys.executable, str(Path(__file__).resolve()),
            "--tunnel-server", str(port), str(size),
        ],
        stdin=subprocess.PIPE,
        stdout=subprocess.PIPE,
        stderr=subprocess.DEVNULL,
        text=True,
        bufsize=1,
    )
    line = proc.stdout.readline().strip() if proc.stdout else ""
    if not line.startswith("SERVER_PID "):
        proc.kill()
        raise SystemExit(f"tunnel server failed to start: {(line or 'no output')!r}")
    return proc, int(line.split()[1])


def run_wildcard_helper(host: str, port: int, size: int) -> int:
    """Send a known UDP payload from the peer netns to a wildcard listener."""
    print(f"HELPER_PID {os.getpid()}", flush=True)
    sent = send_udp_once(size, host, port)
    print(f"HELPER_SENT {sent}", flush=True)
    sys.stdin.readline()
    return 0


def spawn_wildcard_helper(host: str, port: int, size: int) -> tuple[subprocess.Popen[str], int]:
    proc = subprocess.Popen(
        [
            "ip", "netns", "exec", VETH_NS,
            sys.executable, str(Path(__file__).resolve()),
            "--wildcard-helper", host, str(port), str(size),
        ],
        stdin=subprocess.PIPE,
        stdout=subprocess.PIPE,
        stderr=subprocess.DEVNULL,
        text=True,
        bufsize=1,
    )
    line = proc.stdout.readline().strip() if proc.stdout else ""
    if not line.startswith("HELPER_PID "):
        proc.kill()
        raise SystemExit(f"wildcard helper failed to start: {(line or 'no output')!r}")
    return proc, int(line.split()[1])


def spawn_netns_key_helper(path_a: str, path_b: str, size: int) -> tuple[subprocess.Popen[str], int]:
    proc = subprocess.Popen(
        [
            sys.executable,
            str(Path(__file__).resolve()),
            "--netns-helper",
            path_a,
            path_b,
            str(size),
        ],
        stdin=subprocess.PIPE,
        stdout=subprocess.PIPE,
        stderr=subprocess.DEVNULL,
        text=True,
        bufsize=1,
    )
    line = proc.stdout.readline().strip() if proc.stdout else ""
    if not line.startswith("HELPER_PID "):
        proc.kill()
        raise SystemExit(f"netns-key helper failed to start: {(line or 'no output')!r}")
    return proc, int(line.split()[1])


def default_route() -> tuple[str | None, str | None]:
    """Return (gateway, interface) of the default route via `ip route`."""
    try:
        out = subprocess.run(
            ["ip", "route"], capture_output=True, text=True, check=False
        ).stdout
    except OSError:
        return None, None
    for line in out.splitlines():
        if not line.startswith("default "):
            continue
        parts = line.split()
        gateway = parts[parts.index("via") + 1] if "via" in parts else None
        dev = parts[parts.index("dev") + 1] if "dev" in parts else None
        return gateway, dev
    return None, None


def run_tcp_transfer_remote(
    size_bytes: int, host: str, port: int
) -> tuple[int, int, float]:
    """Send size_bytes to a remote echo server and read the echoed bytes back."""
    started = time.monotonic()
    sock = socket.create_connection((host, port), timeout=30)
    chunk = b"x" * (1 << 20)
    sent = 0
    try:
        while sent < size_bytes:
            n = min(len(chunk), size_bytes - sent)
            sock.sendall(chunk[:n])
            sent += n
        sock.shutdown(socket.SHUT_WR)
        received = 0
        while True:
            data = sock.recv(1 << 20)
            if not data:
                break
            received += len(data)
    finally:
        sock.close()
    return sent, received, time.monotonic() - started


def send_udp_once(size_bytes: int, host: str, port: int, datagram_size: int = 4096) -> int:
    """Fire-and-forget UDP payload; returns bytes accepted by send()."""
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    payload = b"w" * datagram_size
    sent = 0
    datagrams = 0
    try:
        sock.connect((host, port))
        while sent < size_bytes:
            n = min(datagram_size, size_bytes - sent)
            sent += sock.send(payload[:n])
            datagrams += 1
            if datagrams % 8 == 0:
                time.sleep(0.005)
    finally:
        sock.close()
    return sent


def receive_wildcard_udp(
    port: int, expected_root: int, expected_peer: int, timeout: float = 30.0
) -> dict[str, int]:
    """Receive on 0.0.0.0 and count bytes from loopback and veth sources."""
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sock.setsockopt(socket.SOL_SOCKET, socket.SO_RCVBUF, 8 << 20)
    sock.bind(("0.0.0.0", port))
    sock.settimeout(0.5)
    totals = {"root": 0, "peer": 0}
    deadline = time.monotonic() + timeout
    try:
        while time.monotonic() < deadline:
            if totals["root"] >= expected_root and totals["peer"] >= expected_peer:
                break
            try:
                data, addr = sock.recvfrom(1 << 16)
            except socket.timeout:
                continue
            if addr[0] == "127.0.0.1":
                totals["root"] += len(data)
            else:
                totals["peer"] += len(data)
    finally:
        sock.close()
    return totals


def run_udp_transfer_remote(
    size_bytes: int, host: str, port: int, datagram_size: int = 4096
) -> tuple[int, int, float]:
    """Send size_bytes to a remote UDP echo server; return sent, received, elapsed.

    UDP may drop datagrams or be NAT-limited. eBPF TX is compared to bytes
    accepted by send(); eBPF RX and the peer byte count are compared to bytes
    actually echoed back.
    """
    started = time.monotonic()
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sock.setsockopt(socket.SOL_SOCKET, socket.SO_RCVBUF, 8 << 20)
    sock.settimeout(0.5)
    sock.connect((host, port))
    payload = b"u" * datagram_size
    sent = 0
    datagrams = 0
    received = [0]
    stop = threading.Event()

    def receive_echoes() -> None:
        while not stop.is_set():
            try:
                data = sock.recv(1 << 16)
            except socket.timeout:
                continue
            except OSError:
                break
            if data:
                received[0] += len(data)

    threading.Thread(target=receive_echoes, daemon=True).start()
    try:
        while sent < size_bytes:
            n = min(datagram_size, size_bytes - sent)
            sent += sock.send(payload[:n])
            datagrams += 1
            if datagrams % 8 == 0:
                time.sleep(0.005)

        # Wait for echoed datagrams; stops early if delivery has settled.
        deadline = time.monotonic() + 30
        stable_since = time.monotonic()
        last = received[0]
        while received[0] < sent and time.monotonic() < deadline:
            time.sleep(0.05)
            if received[0] != last:
                last = received[0]
                stable_since = time.monotonic()
            elif time.monotonic() - stable_since > 3:
                break
    finally:
        stop.set()
        time.sleep(0.1)
        sock.close()
    return sent, received[0], time.monotonic() - started


def prod_worker(
    idx: int,
    size: int,
    deadline: float,
    results: list[dict[str, int] | None],
    children: list[subprocess.Popen[str]],
    lock: threading.Lock,
    child_every: int,
) -> None:
    totals = {"tcp_sent": 0, "tcp_recv": 0, "udp_sent": 0, "udp_recv": 0, "transfers": 0}
    while time.monotonic() < deadline:
        s, r, _ = run_tcp_transfer(size)
        totals["tcp_sent"] += s
        totals["tcp_recv"] += r
        s, r, _ = run_udp_transfer(size)
        totals["udp_sent"] += s
        totals["udp_recv"] += r
        totals["transfers"] += 1
        if child_every and totals["transfers"] % child_every == 0:
            child = spawn_soak_child(size)
            with lock:
                children.append(child)
    results[idx] = totals


def run_soak_child(size_bytes: int) -> int:
    """Short-lived process that does one loopback TCP+UDP transfer, then exits."""
    run_tcp_transfer(size_bytes)
    run_udp_transfer(size_bytes)
    return 0


def spawn_soak_child(size_bytes: int) -> subprocess.Popen[str]:
    return subprocess.Popen(
        [sys.executable, str(Path(__file__).resolve()), "--soak-child", str(size_bytes)],
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
        text=True,
    )


def run_tcp_transfer_netns(
    size_bytes: int, host: str
) -> tuple[int, int, float, subprocess.Popen[str], int]:
    """Server binds the root-namespace veth address; helper sends from flowvns.

    ``host`` is the root-side address (192.0.2.1 / flowvA), which is the only
    endpoint address present in this namespace; the client helper in the peer
    namespace connects to it over the veth pair.
    """
    server = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    server.bind((host, 0))
    server.listen(1)
    server.settimeout(30)
    port = server.getsockname()[1]
    started = time.monotonic()
    helper, helper_pid = spawn_client_helper("tcp", host, port, size_bytes)
    received = 0
    try:
        conn, _ = server.accept()
        with conn:
            while received < size_bytes:
                chunk = conn.recv(min(1 << 20, size_bytes - received))
                if not chunk:
                    break
                received += len(chunk)
    finally:
        server.close()
    return size_bytes, received, time.monotonic() - started, helper, helper_pid


def run_udp_transfer_netns(
    size_bytes: int, host: str
) -> tuple[int, int, float, subprocess.Popen[str], int]:
    """UDP counterpart of run_tcp_transfer_netns; host is the root-side address."""
    server = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    server.bind((host, 0))
    server.settimeout(0.25)
    port = server.getsockname()[1]
    started = time.monotonic()
    helper, helper_pid = spawn_client_helper("udp", host, port, size_bytes)
    received = 0
    deadline = time.monotonic() + 30
    stable_since = time.monotonic()
    last = 0
    try:
        while time.monotonic() < deadline:
            try:
                data, _ = server.recvfrom(1 << 16)
            except socket.timeout:
                if received >= size_bytes and time.monotonic() - stable_since > 0.5:
                    break
                continue
            if not data:
                continue
            received += len(data)
            if received != last:
                last = received
                stable_since = time.monotonic()
    finally:
        server.close()
    return size_bytes, received, time.monotonic() - started, helper, helper_pid


def run_tcp_transfer(
    size_bytes: int,
    host: str = "127.0.0.1",
    server_bind: str | None = None,
    client_bind: str | None = None,
    server_device: str | None = None,
    client_device: str | None = None,
) -> tuple[int, int, float]:
    """Send size_bytes over a TCP connection; return sent, received, elapsed.

    With distinct bind addresses/devices the client and server can exercise two
    different interfaces (e.g. a veth pair) in the same process.
    """
    server = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    if server_device:
        server.setsockopt(socket.SOL_SOCKET, socket.SO_BINDTODEVICE, server_device.encode())
    server.bind((server_bind or host, 0))
    server.listen(1)
    port = server.getsockname()[1]
    received = [0]

    def serve() -> None:
        conn, _ = server.accept()
        with conn:
            while True:
                chunk = conn.recv(1 << 20)
                if not chunk:
                    break
                received[0] += len(chunk)

    thread = threading.Thread(target=serve, daemon=True)
    thread.start()

    chunk = b"x" * (1 << 20)
    sent = 0
    started = time.monotonic()
    client = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    try:
        client.settimeout(30)
        if client_device:
            client.setsockopt(socket.SOL_SOCKET, socket.SO_BINDTODEVICE, client_device.encode())
        if client_bind:
            client.bind((client_bind, 0))
        client.connect((host, port))
        client.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
        while sent < size_bytes:
            n = min(len(chunk), size_bytes - sent)
            client.sendall(chunk[:n])
            sent += n
        client.shutdown(socket.SHUT_WR)
    finally:
        client.close()

    thread.join(timeout=60)
    server.close()
    return sent, received[0], time.monotonic() - started


def run_udp_transfer(
    size_bytes: int,
    host: str = "127.0.0.1",
    datagram_size: int = 4096,
    server_bind: str | None = None,
    client_bind: str | None = None,
    server_device: str | None = None,
    client_device: str | None = None,
) -> tuple[int, int, float]:
    """Send size_bytes of UDP payload; return sent, received, elapsed.

    UDP may drop datagrams. The caller compares eBPF TX against bytes accepted by
    send() and eBPF RX against bytes actually delivered to recvfrom(), so drops
    are reported rather than hidden. QUIC would appear here as UDP payload too.
    Distinct bind addresses exercise two interfaces in the same process.
    """
    server = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    server.setsockopt(socket.SOL_SOCKET, socket.SO_RCVBUF, 8 << 20)
    if server_device:
        server.setsockopt(socket.SOL_SOCKET, socket.SO_BINDTODEVICE, server_device.encode())
    server.bind((server_bind or host, 0))
    server.settimeout(0.25)
    port = server.getsockname()[1]
    received = [0]
    finished = threading.Event()

    def serve() -> None:
        while True:
            try:
                data, _ = server.recvfrom(1 << 16)
            except socket.timeout:
                if finished.is_set():
                    break
                continue
            except OSError:
                break
            if not data:
                continue
            received[0] += len(data)

    thread = threading.Thread(target=serve, daemon=True)
    thread.start()

    payload = b"u" * datagram_size
    sent = 0
    datagrams = 0
    started = time.monotonic()
    client = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    try:
        client.setsockopt(socket.SOL_SOCKET, socket.SO_SNDBUF, 8 << 20)
        if client_device:
            client.setsockopt(socket.SOL_SOCKET, socket.SO_BINDTODEVICE, client_device.encode())
        if client_bind:
            client.bind((client_bind, 0))
        client.connect((host, port))
        while sent < size_bytes:
            n = min(datagram_size, size_bytes - sent)
            sent += client.send(payload[:n])
            datagrams += 1
            # Pace bursts so the receiver queue (often capped below the
            # requested SO_RCVBUF) can drain; drops are still reported by the
            # sent/received comparison rather than hidden.
            if datagrams % 64 == 0:
                time.sleep(0.001)
        # Drain the receive queue before stopping the server thread.
        deadline = time.monotonic() + 5
        stable_since = time.monotonic()
        last = received[0]
        while time.monotonic() < deadline:
            time.sleep(0.05)
            if received[0] != last:
                last = received[0]
                stable_since = time.monotonic()
            elif time.monotonic() - stable_since > 0.5:
                break
    finally:
        finished.set()
        thread.join(timeout=2)
        client.close()
        server.close()

    return sent, received[0], time.monotonic() - started


def summarize(
    entries: list[dict[str, object]],
    unattributed: list[dict[str, int]],
    expected_tgid: int | set[int],
) -> dict[str, object]:
    expected_set = {expected_tgid} if isinstance(expected_tgid, int) else set(expected_tgid)
    processes: dict[tuple[int, int, int, int, str, int], dict[str, object]] = {}
    for e in entries:
        ifname = str(e.get("ifname") or "")
        netns_id = int(e.get("netns_id") or 0)
        key = (
            int(e["tgid"]),
            int(e["start_ns"]),
            int(e["uid"]),
            int(e.get("proto") or 0),
            ifname,
            netns_id,
        )
        cur = processes.setdefault(
            key,
            {
                "tgid": key[0],
                "start_ns": key[1],
                "startticks": int(e.get("startticks") or 0),
                "raw_startticks": int(e.get("raw_startticks") or 0),
                "uid": key[2],
                "proto": key[3],
                "ifname": key[4],
                "netns_id": key[5],
                "tx": 0,
                "rx": 0,
                "comm": e["comm"],
                "exe": e["exe"],
                "identity_verified": e["identity_verified"],
                "cgroup_id": e["cgroup_id"],
            },
        )
        cur["tx"] = int(cur["tx"]) + int(e["tx_delta"])
        cur["rx"] = int(cur["rx"]) + int(e["rx_delta"])
        if e.get("exe") not in (None, "", "-"):
            cur["exe"] = e["exe"]
        if e.get("identity_verified"):
            cur["identity_verified"] = True

    expected_rows = [p for p in processes.values() if int(p["tgid"]) in expected_set]
    expected_tx = sum(int(p["tx"]) for p in expected_rows)
    expected_rx = sum(int(p["rx"]) for p in expected_rows)
    attributed_tx = sum(int(p["tx"]) for p in processes.values())
    attributed_rx = sum(int(p["rx"]) for p in processes.values())
    unattributed_tx = sum(u["tx_delta"] for u in unattributed)
    unattributed_rx = sum(u["rx_delta"] for u in unattributed)

    by_proto: dict[int, dict[str, object]] = {}
    for proto in (1, 2):
        rows = [p for p in expected_rows if int(p["proto"]) == proto]
        by_proto[proto] = {
            "tx": sum(int(p["tx"]) for p in rows),
            "rx": sum(int(p["rx"]) for p in rows),
            "ifnames": sorted({str(p["ifname"]) for p in rows}),
            "tx_ifnames": sorted(
                {str(p["ifname"]) for p in rows if int(p["tx"]) > 0}
            ),
            "rx_ifnames": sorted(
                {str(p["ifname"]) for p in rows if int(p["rx"]) > 0}
            ),
            "unresolved_tx": sum(
                int(p["tx"]) for p in rows if str(p["ifname"]) in ("", "-", "?", "0")
            ),
            "unresolved_rx": sum(
                int(p["rx"]) for p in rows if str(p["ifname"]) in ("", "-", "?", "0")
            ),
            "rows": len(rows),
        }

    resolved_ifnames = sorted(
        {str(p["ifname"]) for p in expected_rows if str(p["ifname"]) not in ("", "-", "?", "0")}
    )
    unresolved_expected = [
        p for p in expected_rows if str(p["ifname"]) in ("", "-", "?", "0")
    ]

    return {
        "processes": sorted(processes.values(), key=lambda p: int(p["tx"]) + int(p["rx"]), reverse=True),
        "attributed_tx": attributed_tx,
        "attributed_rx": attributed_rx,
        "expected_tgid": min(expected_set) if expected_set else 0,
        "expected_tgids": sorted(expected_set),
        "expected_tx": expected_tx,
        "expected_rx": expected_rx,
        "background_tx": attributed_tx - expected_tx,
        "background_rx": attributed_rx - expected_rx,
        "expected_generations": len(
            {(int(p["tgid"]), int(p["start_ns"])) for p in expected_rows}
        ),
        "expected_generations_by_tgid": {
            tid: len({int(p["start_ns"]) for p in expected_rows if int(p["tgid"]) == tid})
            for tid in expected_set
        },
        "expected_start_ns": sorted({int(p["start_ns"]) for p in expected_rows}),
        "expected_identity_verified": bool(expected_rows) and all(
            bool(p["identity_verified"]) for p in expected_rows
        ),
        "expected_by_proto": by_proto,
        "expected_resolved_ifnames": resolved_ifnames,
        "expected_unresolved_rows": len(unresolved_expected),
        "expected_unresolved_tx": sum(int(p["tx"]) for p in unresolved_expected),
        "expected_unresolved_rx": sum(int(p["rx"]) for p in unresolved_expected),
        "unattributed_tx": unattributed_tx,
        "unattributed_rx": unattributed_rx,
    }


def pct(part: int, whole: int) -> float | None:
    return round(part * 100.0 / whole, 3) if whole else None


def chown_to_invoker(path: Path) -> None:
    """When run via sudo, keep generated artifacts owned by the invoking user."""
    uid = os.environ.get("SUDO_UID")
    gid = os.environ.get("SUDO_GID")
    if uid is None or gid is None:
        return
    try:
        os.chown(path, int(uid), int(gid))
    except OSError:
        pass


def fmt_ifname(value: str) -> str:
    return value if value else "(unresolved)"


def format_tgid_list(tgids: list[int]) -> str:
    if len(tgids) <= 12:
        return ", ".join(str(t) for t in tgids)
    return f"{', '.join(str(t) for t in tgids[:6])}, ... ({len(tgids)} total)"


def write_report(path: Path, ctx: dict[str, object]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    chown_to_invoker(path.parent)
    s = ctx["summary"]
    assert isinstance(s, dict)
    by_proto = s["expected_by_proto"]
    assert isinstance(by_proto, dict)

    def proto_rows(proto: int) -> tuple[int, int]:
        p = by_proto[proto]
        return int(p["tx"]), int(p["rx"])

    tcp_tx, tcp_rx = proto_rows(1)
    udp_tx, udp_rx = proto_rows(2)

    lines = [
        "# eBPF TCP+UDP attribution controlled validation",
        "",
        f"- Date (UTC): {ctx['date']}",
        f"- Kernel: `{ctx['kernel']}`",
        f"- Loader: `{ctx['loader']}` (interval {ctx['interval']} s)",
        f"- Payload per protocol: {ctx['size_bytes']} bytes ({ctx['size_mib']:.3f} MiB), {ctx['mode_description']}",
        f"- Transfers: {ctx['transfer_scope']}",
        f"- Expected process TGID(s): {format_tgid_list(s.get('expected_tgids', [s['expected_tgid']]))}",
        f"- Loader wall time: {ctx['elapsed_s']:.2f} s",
        "",
        "## Results by protocol",
        "",
        "| Protocol | Payload sent | Payload received | Attributed TX | Attributed RX | TX / sent | RX / received |",
        "| --- | ---: | ---: | ---: | ---: | ---: | ---: |",
        f"| TCP (`tcp_sendmsg`/`tcp_recvmsg`) | {ctx['tcp_sent']} | {ctx['tcp_received']} | {tcp_tx} | {tcp_rx} | {pct(tcp_tx, int(ctx['tcp_sent']))}% | {pct(tcp_rx, int(ctx['tcp_received']))}% |",
        f"| UDP (`udp_sendmsg`/`udp_recvmsg`) | {ctx['udp_sent']} | {ctx['udp_received']} | {udp_tx} | {udp_rx} | {pct(udp_tx, int(ctx['udp_sent']))}% | {pct(udp_rx, int(ctx['udp_received']))}% |",
        "",
        f"- UDP delivered / UDP sent: {pct(int(ctx['udp_received']), int(ctx['udp_sent']))}% (drops are possible; eBPF RX is compared to delivered bytes).",
        "",
        "| Aggregate | Bytes |",
        "| --- | ---: |",
        f"| Total attributed TX, expected process | {s['expected_tx']} |",
        f"| Total attributed RX, expected process | {s['expected_rx']} |",
        f"| Other attributed TX (background traffic) | {s['background_tx']} |",
        f"| Other attributed RX (background traffic) | {s['background_rx']} |",
        f"| Total attributed TX | {s['attributed_tx']} |",
        f"| Total attributed RX | {s['attributed_rx']} |",
        f"| Unattributed TX | {s['unattributed_tx']} |",
        f"| Unattributed RX | {s['unattributed_rx']} |",
    ]
    if ctx.get("lo_before") and ctx.get("lo_after"):
        lo_tx = ctx["lo_after"]["tx_bytes"] - ctx["lo_before"]["tx_bytes"]
        lo_rx = ctx["lo_after"]["rx_bytes"] - ctx["lo_before"]["rx_bytes"]
        lines += [
            f"| `lo` TX delta (IP/link, both protocols) | {lo_tx} |",
            f"| `lo` RX delta (IP/link, both protocols) | {lo_rx} |",
        ]
    if ctx.get("nic_before") and ctx.get("nic_after"):
        nic_tx = ctx["nic_after"]["tx_bytes"] - ctx["nic_before"]["tx_bytes"]
        nic_rx = ctx["nic_after"]["rx_bytes"] - ctx["nic_before"]["rx_bytes"]
        lines += [
            f"| `{ctx['nic_interface']}` TX delta (IP/link/Wi-Fi) | {nic_tx} |",
            f"| `{ctx['nic_interface']}` RX delta (IP/link/Wi-Fi) | {nic_rx} |",
        ]
    for dev in ("client", "server"):
        before = ctx.get(f"veth_{dev}_before")
        after = ctx.get(f"veth_{dev}_after")
        if before and after:
            lines += [
                f"| `{before['name']}` TX delta | {after['tx_bytes'] - before['tx_bytes']} |",
                f"| `{before['name']}` RX delta | {after['rx_bytes'] - before['rx_bytes']} |",
            ]
    lines += [
        "",
        "## Per-process rows (TGID + event-time start_ns + UID + proto + interface)",
        "",
        "| TGID | UID | Proto | Interface | Netns | TX | RX | comm | exe | start_ns | cgroup | identity verified | expected process |",
        "| ---: | ---: | --- | --- | ---: | ---: | ---: | --- | --- | ---: | --- | --- | --- |",
    ]
    expected_tgids = set(s.get("expected_tgids", [s["expected_tgid"]]))
    for p in s["processes"]:
        lines.append(
            f"| {p['tgid']} | {p['uid']} | {PROTO_NAMES.get(int(p['proto']), '?')} | "
            f"{fmt_ifname(str(p['ifname']))} | {p.get('netns_id', 0)} | {p['tx']} | {p['rx']} | {p['comm']} | {p['exe']} | "
            f"{p['start_ns']} | {p['cgroup_id']} | {'yes' if p['identity_verified'] else 'no'} | "
            f"{'yes' if int(p['tgid']) in expected_tgids else 'no'} |"
        )
    lines += [
        "",
        "## Interface attribution",
        "",
        f"- Expected interface: {ctx['expected_interface_description']}",
        f"- Expected-process resolved interface(s): {', '.join(s['expected_resolved_ifnames']) or '(none)'}",
        f"- Expected-process rows with unresolved interface: {s['expected_unresolved_rows']}",
        f"- Expected-process unresolved-interface bytes: TX {s['expected_unresolved_tx']}, RX {s['expected_unresolved_rx']}",
        f"- TCP interface name(s): {', '.join(by_proto[1]['ifnames']) or '(none)'} (TX: {', '.join(by_proto[1]['tx_ifnames']) or '(none)'}; RX: {', '.join(by_proto[1]['rx_ifnames']) or '(none)'})",
        f"- UDP interface name(s): {', '.join(by_proto[2]['ifnames']) or '(none)'} (TX: {', '.join(by_proto[2]['tx_ifnames']) or '(none)'}; RX: {', '.join(by_proto[2]['rx_ifnames']) or '(none)'})",
    ]
    if ctx.get("soak"):
        entries = ctx["soak_map_entries"]
        cpu_samples = ctx["soak_cpu_samples"]
        rss_samples = ctx["soak_rss_samples"]
        lines += [
            "",
            "## Soak / map growth observation",
            "",
            f"- Duration requested: {ctx['soak_duration_s']:.0f} s; wall time: {ctx['elapsed_s']:.2f} s",
            f"- Parent iterations: {ctx['soak_iterations']}",
            f"- Short-lived child transfers: {ctx['soak_children']}",
            f"- BPF map entries: first {entries[0] if entries else 'n/a'}, last {entries[-1] if entries else 'n/a'}, max {max(entries) if entries else 'n/a'}",
            f"- CPU samples (s): {', '.join(str(v) for v in cpu_samples) or 'n/a'}",
            f"- Peak RSS samples (KiB): {', '.join(str(v) for v in rss_samples) or 'n/a'}",
            "- The BPF map has no expiry; growth here is an observation, not a claim of bounded steady-state memory.",
        ]
    if ctx.get("netns_key"):
        lines += [
            "",
            "## Netns-key collision observation",
            "",
            f"- Payload per netns: {ctx['netns_key_bytes']}",
            f"- Expected-process TCP rows: {len(ctx['netns_key_rows'])}",
        ]
        for row in ctx["netns_key_rows"]:
            lines.append(
                f"  - netns {row['netns_id']}: TX {row['tx']} / RX {row['rx']} on {row['ifname']}"
            )
        lines += [
            "- The rows must differ only by netns id; a single merged row would mean the identity key still merges namespaces.",
        ]
    if ctx.get("map_churn"):
        entries = [s["entries"] for s in ctx["churn_samples"] if s["entries"] is not None]
        cpu_samples = [s["cpu_s"] for s in ctx["churn_samples"] if s["cpu_s"] is not None]
        rss_samples = [s["rss_kb"] for s in ctx["churn_samples"] if s["rss_kb"] is not None]
        expected_bytes = ctx["churn_successes"] * ctx["churn_child_bytes"]
        lines += [
            "",
            "## Map churn observation",
            "",
            f"- Target map entries: {ctx['churn_target']}; reached: {ctx['check_map_target']}",
            f"- Children spawned: {ctx['churn_children']}; successful: {ctx['churn_successes']}; batches: {ctx['churn_batches']}",
            f"- Expected bytes per protocol/direction (successes × child payload): {expected_bytes}",
            f"- Exact total shortfall (2 × expected per protocol − attributed TX): {2 * expected_bytes - int(s['expected_tx'])}",
            f"- Attributed expected TX/RX: {s['expected_tx']} / {s['expected_rx']}",
            f"- BPF map entries: first {entries[0] if entries else 'n/a'}, last {entries[-1] if entries else 'n/a'}, max {max(entries) if entries else 'n/a'}",
            f"- CPU samples (s): {', '.join(str(v) for v in cpu_samples) or 'n/a'}",
            f"- Peak RSS samples (KiB): {', '.join(str(v) for v in rss_samples) or 'n/a'}",
            "- The map has no expiry; growth and any accounting shortfall near capacity are observations, not a bounded-memory claim.",
        ]
    if ctx.get("wildcard"):
        counts = ctx["wildcard_counts"]
        lines += [
            "",
            "## Wildcard multi-interface observation",
            "",
            f"- Payload per source: {ctx['wildcard_bytes']}",
            f"- Server received: loopback {counts.get('root', 0)}, veth {counts.get('peer', 0)}",
            f"- flowvA ifindex in root: {ctx['wildcard_client_ifindex']}; flowvB ifindex in peer netns: {ctx['wildcard_server_ifindex']}",
            f"- Wildcard per-datagram interface attribution check: {ctx['check_wildcard']}",
            "- The same server socket received traffic on two interfaces; rows must differ by interface name.",
        ]
    if ctx.get("tunnel"):
        counters = ctx["tunnel_counters"]
        def delta(key: str) -> str:
            before = counters.get(f"{key}_before")
            after = counters.get(f"{key}_after")
            if not before or not after:
                return "n/a"
            return f"TX {after['tx_bytes'] - before['tx_bytes']}, RX {after['rx_bytes'] - before['rx_bytes']}"

        lines += [
            "",
            "## Tunnel observation",
            "",
            f"- Tunnel mode: {ctx['tunnel_mode']} ({ctx['tunnel_name']}) above the veth underlay",
            f"- Tunnel check (overlay device only, no underlay rows): {ctx['check_tunnel']}",
            f"- Root tunnel counter delta: {delta('tunnel')}",
            f"- Root underlay counter delta: {delta('underlay')}",
            f"- Peer-netns tunnel counter delta: {delta('peer_tunnel')}",
            "- Overlay and underlay both carried the packets; only the tunnel device is attributed to the process. The underlay delta documents the overlap.",
        ]
    if ctx.get("wireguard"):
        counters = ctx["wg_counters"]

        def wg_delta(key: str) -> str:
            before = counters.get(f"{key}_before")
            after = counters.get(f"{key}_after")
            if not before or not after:
                return "n/a"
            return f"TX {after['tx_bytes'] - before['tx_bytes']}, RX {after['rx_bytes'] - before['rx_bytes']}"

        lines += [
            "",
            "## WireGuard observation",
            "",
            f"- WireGuard check (wg0 only, no underlay rows): {ctx['check_wireguard']}",
            f"- Root wg0 counter delta: {wg_delta('wg')}",
            f"- Root underlay counter delta: {wg_delta('underlay')}",
            f"- Peer-netns wg0 counter delta: {wg_delta('peer_wg')}",
            "- WireGuard and underlay both carried the packets; only wg0 is attributed to the process.",
        ]
    if ctx.get("prod_soak"):
        entries = [s["map_entries"] for s in ctx["prod_samples"] if s["map_entries"] is not None]
        cpu_samples = [s["cpu_s"] for s in ctx["prod_samples"] if s["cpu_s"] is not None]
        rss_samples = [s["rss_kb"] for s in ctx["prod_samples"] if s["rss_kb"] is not None]
        lines += [
            "",
            "## Production-load soak observation",
            "",
            f"- Duration requested: {ctx['soak_duration_s']:.0f} s; wall time: {ctx['elapsed_s']:.2f} s",
            f"- Workers: {ctx['prod_workers']}; transfers: {ctx['prod_transfers']}; children: {ctx['prod_children']}",
            f"- Attributed expected TX/RX: {s['expected_tx']} / {s['expected_rx']}",
            f"- BPF map entries: first {entries[0] if entries else 'n/a'}, last {entries[-1] if entries else 'n/a'}, max {max(entries) if entries else 'n/a'}",
            f"- CPU samples (s): {', '.join(str(v) for v in cpu_samples) or 'n/a'}",
            f"- Peak RSS samples (KiB): {', '.join(str(v) for v in rss_samples) or 'n/a'}",
            "- This is a loopback concurrency/duration proxy, not a real Internet workload or production SLA evidence.",
        ]
    if ctx.get("identity_diag"):
        clk = int(ctx["clk_tck"])
        proc_ticks = ctx.get("proc_startticks")
        proc_exe = read_proc_exe(s["expected_tgid"])
        lines += [
            "",
            "## Identity diagnostic",
            "",
            f"- clk_tck from loader M line: {clk}",
            f"- /proc/{s['expected_tgid']}/stat starttime: {proc_ticks}",
            f"- /proc/{s['expected_tgid']}/exe: {proc_exe or 'unavailable'}",
        ]
        for row in s["processes"]:
            if int(row["tgid"]) != int(s["expected_tgid"]):
                continue
            computed = int(row["start_ns"]) * clk // 1_000_000_000
            stored = int(row.get("startticks") or 0)
            raw = int(row.get("raw_startticks") or 0)
            lines.append(
                f"- TGID {row['tgid']} proto {row['proto']} comm={row['comm']} exe={row['exe']}: "
                f"start_ns={row['start_ns']} cached_ticks={stored} raw_ticks={raw} "
                f"computed={computed} computed−raw={computed - raw} verified={row['identity_verified']}"
            )
        lines.append(
            "- If computed≈stored but verified is false, investigate /proc exe enrichment; "
            "if they differ, investigate event-time start-time/leader handling."
        )
    if ctx.get("overflow") and (
        ctx["overflow"]["tx_total"] or ctx["overflow"]["rx_total"]
    ):
        of = ctx["overflow"]
        lines += [
            "",
            "## BPF map overflow",
            "",
            f"- Cumulative overflow TX: {of['tx_total']} bytes ({of['tx_events']} events)",
            f"- Cumulative overflow RX: {of['rx_total']} bytes ({of['rx_events']} events)",
            "- These bytes could not get a process-generation row and are counted as unattributed overflow, not silently dropped.",
        ]
    lines += [
        "",
        "## Loader overhead (first datapoint)",
        "",
        f"- CPU time: {ctx['cpu_s'] if ctx['cpu_s'] is not None else 'n/a'} s",
        f"- Average CPU: {ctx['cpu_pct'] if ctx['cpu_pct'] is not None else 'n/a'}% of one core during the run",
        f"- Peak RSS: {ctx['rss_kb'] if ctx['rss_kb'] is not None else 'n/a'} KiB",
        "",
        "## Checks",
        "",
        f"- TCP attributed TX within ±1% of payload sent: {ctx['check_tcp_tx']}",
        f"- TCP attributed RX within ±1% of payload received: {ctx['check_tcp_rx']}",
        f"- UDP attributed TX within ±1% of payload accepted by send(): {ctx['check_udp_tx'] if ctx['check_udp_tx'] is not None else 'n/a (TCP-only mode)'}",
        f"- UDP attributed RX within ±1% of payload delivered to recvfrom(): {ctx['check_udp_rx'] if ctx['check_udp_rx'] is not None else 'n/a (TCP-only mode)'}",
        f"- Expected TGID has exactly one process generation (no thread/start-time split): {ctx['check_single_generation']}",
        f"- Expected process identity verified against live `/proc`: {ctx['check_identity'] if ctx['check_identity'] is not None else 'n/a (short-lived emitting threads)'}",
        f"- Expected-process traffic on expected interface(s): {ctx['check_interface']}",
        f"- No unattributed bytes in a fully controlled transfer: {ctx['check_unattributed']}",
    ]
    if ctx.get("netns_key"):
        lines.append(
            f"- Same TGID/start/uid in two netns produced distinct rows with exact bytes: {ctx['check_netns_collision']}"
        )
    if ctx.get("map_churn"):
        lines += [
            f"- Child byte totals matched expected payload: {ctx['check_churn_bytes']}",
            f"- Map target reached: {ctx['check_map_target']}",
        ]
    if ctx.get("wildcard"):
        lines.append(
            f"- Wildcard server received on two interfaces with correct per-interface bytes: {ctx['check_wildcard']}"
        )
    if ctx.get("tunnel"):
        lines.append(
            f"- TCP echo attributed to tunnel devices with no underlay rows: {ctx['check_tunnel']}"
        )
    if ctx.get("wireguard"):
        lines.append(
            f"- TCP echo attributed to WireGuard devices with no underlay rows: {ctx['check_wireguard']}"
        )
    if ctx.get("nic"):
        tcp_note = (
            f"{ctx['nic_peer_bytes']} bytes supplied via --peer-bytes"
            if ctx.get("nic_peer_bytes") is not None
            else "not supplied; compare manually with the phone TCP listener output"
        )
        udp_note = (
            f"{ctx['nic_peer_udp_bytes']} bytes supplied via --peer-udp-bytes"
            if ctx.get("nic_peer_udp_bytes") is not None
            else "not supplied; compare manually with the phone UDP listener output"
        )
        lines += [
            f"- Peer TCP listener byte count ({tcp_note}): {ctx['check_peer_bytes'] if ctx['check_peer_bytes'] is not None else 'n/a'}",
            f"- Peer UDP listener byte count ({udp_note}): {ctx['check_peer_udp_bytes'] if ctx['check_peer_udp_bytes'] is not None else 'n/a'}",
        ]
    lines += [
        "",
        "## Interpretation and caveats",
        "",
        "- eBPF counts TCP and UDP payload bytes at the send/recv function return; UDP/QUIC",
        "  framing is not identified, so QUIC traffic is expected to appear as UDP payload.",
        f"  {ctx['counter_caveat']} Ratios near 100% are expected but exact equality",
        "  is not.",
        f"- {ctx['process_scope_caveat']}",
        "- Background traffic from other processes is reported separately; unresolved `-`",
        "  rows for unrelated background traffic do not affect the expected-process gate.",
        "- UDP datagrams may be dropped by the receiver; the UDP RX comparison uses bytes actually",
        "  delivered to recvfrom(), and dropped bytes are not counted as attributed RX.",
        f"- {ctx['interface_caveat']}",
        "- This is a controlled sanity check, not a claim of complete attribution coverage.",
        "",
    ]
    path.write_text("\n".join(lines), encoding="utf-8")
    chown_to_invoker(path)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--client-helper", nargs=4, metavar=("PROTO", "HOST", "PORT", "SIZE"))
    parser.add_argument("--wg-server", nargs=2, metavar=("HOST", "PORT"),
                        help=argparse.SUPPRESS)  # internal: WireGuard peer echo server
    parser.add_argument("--tunnel-server", nargs=2, metavar=("PORT", "SIZE"),
                        help=argparse.SUPPRESS)  # internal: tunnel peer echo server
    parser.add_argument("--wildcard-helper", nargs=3, metavar=("HOST", "PORT", "SIZE"),
                        help=argparse.SUPPRESS)  # internal: peer-netns UDP sender
    parser.add_argument("--netns-helper", nargs=3, metavar=("PATH_A", "PATH_B", "SIZE"),
                        help=argparse.SUPPRESS)  # internal: same-PID two-netns collision helper
    parser.add_argument("--soak-child", type=int, metavar="SIZE_BYTES",
                        help=argparse.SUPPRESS)  # internal: short-lived churn process
    parser.add_argument("--peer-host", help="nic mode: peer address (default: default-route gateway)")
    parser.add_argument("--peer-port", type=int, default=9000,
                        help="nic mode: peer TCP echo port (default: 9000)")
    parser.add_argument("--peer-udp-port", type=int, default=9001,
                        help="nic mode: peer UDP echo port (default: 9001)")
    parser.add_argument("--interface", help="nic mode: physical interface to assert (default: default-route device)")
    parser.add_argument("--peer-bytes", type=int,
                        help="nic mode: optional TCP byte count reported by the peer listener")
    parser.add_argument("--peer-udp-bytes", type=int,
                        help="nic mode: optional UDP byte count reported by the peer listener")
    parser.add_argument("--size-mib", type=float, default=16.0, help="payload per protocol")
    parser.add_argument("--interval", type=float, default=1.0)
    parser.add_argument(
        "--mode",
        choices=("lo", "veth", "soak", "nic", "netns-key", "map-churn", "wildcard", "tunnel", "prod-soak", "identity-diag", "wireguard"),
        default="lo",
        help="lo: one loopback interface for TX/RX (default). veth: two-interface veth across two netns. soak: repeated transfers plus short-lived child churn for overhead/map-growth evidence. nic: TCP/UDP echo through a physical interface to a peer. netns-key: same process in two netns to validate netns in the identity key. map-churn: high-rate short-lived children to drive the BPF map toward capacity.",
    )
    parser.add_argument("--duration", type=float, default=300.0,
                        help="soak duration in seconds (default: 300)")
    parser.add_argument("--soak-size-mib", type=float, default=1.0,
                        help="payload per protocol per soak iteration/child (default: 1)")
    parser.add_argument("--soak-child-every", type=int, default=5,
                        help="spawn a short-lived child after this many soak iterations (default: 5)")
    parser.add_argument("--netns-size-mib", type=float, default=1.0,
                        help="netns-key mode: payload per netns (default: 1)")
    parser.add_argument("--churn-target", type=int, default=9000,
                        help="map-churn mode: stop after map entries reach this value (default: 9000)")
    parser.add_argument("--churn-child-size-kib", type=int, default=4,
                        help="map-churn mode: TCP+UDP payload per child in KiB (default: 4)")
    parser.add_argument("--churn-batch", type=int, default=64,
                        help="map-churn mode: children spawned per batch (default: 64)")
    parser.add_argument("--churn-timeout", type=float, default=180.0,
                        help="map-churn mode: maximum run seconds (default: 180)")
    parser.add_argument("--wildcard-size-mib", type=float, default=1.0,
                        help="wildcard mode: UDP payload per source (default: 1)")
    parser.add_argument("--tunnel-size-mib", type=float, default=16.0,
                        help="tunnel mode: TCP echo payload (default: 16)")
    parser.add_argument("--prod-workers", type=int, default=8,
                        help="prod-soak mode: concurrent transfer workers (default: 8)")
    parser.add_argument("--prod-child-every", type=int, default=0,
                        help="prod-soak mode: spawn a short-lived child every N transfers (0 = none)")
    parser.add_argument("--loader", type=Path, default=DEFAULT_LOADER)
    parser.add_argument("--report", type=Path, default=None)
    parser.add_argument("--json", action="store_true", help="only print the JSON summary")
    args = parser.parse_args()

    if args.client_helper:
        return run_client_helper(args.client_helper)
    if args.soak_child is not None:
        return run_soak_child(args.soak_child)
    if args.wg_server:
        return run_wg_server(args.wg_server[0], int(args.wg_server[1]))
    if args.tunnel_server:
        return run_tunnel_server(int(args.tunnel_server[0]), int(args.tunnel_server[1]))
    if args.wildcard_helper:
        return run_wildcard_helper(args.wildcard_helper[0], int(args.wildcard_helper[1]), int(args.wildcard_helper[2]))
    if args.netns_helper:
        return run_netns_key_helper(
            args.netns_helper[0], args.netns_helper[1], int(args.netns_helper[2])
        )

    if os.geteuid() != 0:
        print("error: this validation loads BPF programs and must run as root", file=sys.stderr)
        return 2

    size_bytes = int(args.size_mib * 1024 * 1024)
    if size_bytes <= 0:
        print("error: --size-mib must be positive", file=sys.stderr)
        return 2

    soak = args.mode == "soak"
    nic = args.mode == "nic"
    netns_key = args.mode == "netns-key"
    map_churn = args.mode == "map-churn"
    wildcard = args.mode == "wildcard"
    tunnel = args.mode == "tunnel"
    prod_soak = args.mode == "prod-soak"
    identity_diag = args.mode == "identity-diag"
    wireguard = args.mode == "wireguard"
    tunnel_bytes = int(args.tunnel_size_mib * 1024 * 1024)
    if (tunnel or wireguard) and tunnel_bytes <= 0:
        print("error: --tunnel-size-mib must be positive", file=sys.stderr)
        return 2
    if prod_soak and (args.duration <= 0 or args.prod_workers <= 0 or args.prod_child_every < 0):
        print("error: prod-soak needs positive --duration/--prod-workers and non-negative --prod-child-every",
              file=sys.stderr)
        return 2
    wildcard_bytes = int(args.wildcard_size_mib * 1024 * 1024)
    if wildcard and wildcard_bytes <= 0:
        print("error: --wildcard-size-mib must be positive", file=sys.stderr)
        return 2
    netns_key_bytes = int(args.netns_size_mib * 1024 * 1024)
    if netns_key and netns_key_bytes <= 0:
        print("error: --netns-size-mib must be positive", file=sys.stderr)
        return 2
    churn_child_bytes = args.churn_child_size_kib * 1024
    if map_churn and (
        args.churn_target <= 0
        or churn_child_bytes <= 0
        or args.churn_batch <= 0
        or args.churn_timeout <= 0
    ):
        print("error: map-churn mode needs positive --churn-target/--churn-child-size-kib/--churn-batch/--churn-timeout",
              file=sys.stderr)
        return 2
    peer_host = args.peer_host
    peer_interface = args.interface
    if nic:
        gw, dev = default_route()
        peer_host = peer_host or gw
        peer_interface = peer_interface or dev
        if not peer_host or not peer_interface:
            raise SystemExit(
                "nic mode needs --peer-host and --interface (could not infer them from the default route)"
            )
    soak_bytes = int(args.soak_size_mib * 1024 * 1024)
    if soak and (args.duration <= 0 or soak_bytes <= 0 or args.soak_child_every <= 0):
        print("error: soak mode needs positive --duration, --soak-size-mib, and --soak-child-every",
              file=sys.stderr)
        return 2

    report_path = args.report or (
        DEFAULT_REPORT_DIR / f"ebpf-controlled-{datetime.now(timezone.utc):%Y%m%dT%H%M%SZ}.md"
    )

    veth = VethPair() if args.mode in ("veth", "wildcard") else None
    tunnel_topology = TunnelTopology() if tunnel else None
    wg_topology = WireGuardTopology() if wireguard else None
    netns_pair = NetnsKeyPair() if netns_key else None
    loader: LoaderRun | None = None
    helpers: list[subprocess.Popen[str]] = []
    soak_children: list[subprocess.Popen[str]] = []
    soak_iterations = 0
    soak_samples: list[dict[str, object]] = []
    churn_children: list[subprocess.Popen[str]] = []
    churn_samples: list[dict[str, object]] = []
    churn_batches = 0
    churn_successes = 0
    prod_samples: list[dict[str, object]] = []
    prod_transfers = 0
    prod_children: list[subprocess.Popen[str]] = []
    wildcard_counts: dict[str, int] = {}
    wildcard_port = 17001
    tunnel_port = 17002
    tunnel_helper_netns = 0
    tunnel_payload = 0
    wg_helper_netns = 0
    wg_counters: dict[str, dict[str, int] | None] = {}
    tunnel_counters: dict[str, dict[str, int] | None] = {}
    started = time.monotonic()
    cpu_before = None
    lo_before = None
    lo_after = None
    nic_before: dict[str, int] | None = None
    nic_after: dict[str, int] | None = None
    veth_before: dict[str, dict[str, int] | None] = {}
    veth_after: dict[str, dict[str, int] | None] = {}
    tcp_sent = udp_sent = tcp_received = udp_received = 0
    tcp_s = udp_s = 0.0
    expected_tgids: set[int] = set()
    helper_netns = 0
    try:
        if veth:
            veth.start()
        if netns_pair:
            netns_pair.start()
        if tunnel_topology:
            tunnel_topology.start()
        if wg_topology:
            wg_topology.start()
        loader = LoaderRun(args.loader, args.interval)
        loader.start()
        cpu_before = loader.cpu_seconds()
        # Let the first snapshot establish a baseline (and prove the loader attached).
        time.sleep(max(1.0, args.interval * 1.5))

        if not nic and not netns_key and not map_churn and not tunnel and not wireguard:
            lo_before = read_net_dev("lo")
        if veth and args.mode == "veth":
            veth_before = {"client": read_net_dev(VETH_CLIENT)}
        if soak:
            expected_tgids.add(os.getpid())
            sample_every = max(args.interval, 5.0)
            deadline = time.monotonic() + args.duration
            next_sample = time.monotonic() + sample_every
            while time.monotonic() < deadline:
                t_sent, t_recv, _ = run_tcp_transfer(soak_bytes)
                u_sent, u_recv, _ = run_udp_transfer(soak_bytes)
                tcp_sent += t_sent
                tcp_received += t_recv
                udp_sent += u_sent
                udp_received += u_recv
                soak_iterations += 1
                if soak_iterations % args.soak_child_every == 0:
                    soak_children.append(spawn_soak_child(soak_bytes))
                now = time.monotonic()
                if now >= next_sample:
                    last_map = loader.map_samples[-1] if loader.map_samples else {}
                    soak_samples.append(
                        {
                            "elapsed_s": round(now - started, 1),
                            "cpu_s": loader.cpu_seconds(),
                            "rss_kb": loader.peak_rss_kb(),
                            "map_entries": last_map.get("entries"),
                            "unattributed_tx": last_map.get("utx"),
                            "unattributed_rx": last_map.get("urx"),
                        }
                    )
                    next_sample = now + sample_every
                time.sleep(0.1)
            for child in soak_children:
                try:
                    child.wait(timeout=30)
                except subprocess.TimeoutExpired:
                    child.kill()
                    child.wait(timeout=5)
            last_map = loader.map_samples[-1] if loader.map_samples else {}
            soak_samples.append(
                {
                    "elapsed_s": round(time.monotonic() - started, 1),
                    "cpu_s": loader.cpu_seconds(),
                    "rss_kb": loader.peak_rss_kb(),
                    "map_entries": last_map.get("entries"),
                    "unattributed_tx": last_map.get("utx"),
                    "unattributed_rx": last_map.get("urx"),
                }
            )
        elif prod_soak or identity_diag:
            expected_tgids.add(os.getpid())
            size = int(args.soak_size_mib * 1024 * 1024)
            deadline = time.monotonic() + args.duration
            results: list[dict[str, int] | None] = [None] * args.prod_workers
            prod_children_list = prod_children  # alias for worker closure
            lock = threading.Lock()
            threads = [
                threading.Thread(
                    target=prod_worker,
                    args=(i, size, deadline, results, prod_children_list, lock, args.prod_child_every),
                    daemon=True,
                )
                for i in range(args.prod_workers)
            ]
            for t in threads:
                t.start()
            next_sample = time.monotonic() + max(5.0, args.interval)
            while any(t.is_alive() for t in threads):
                now = time.monotonic()
                if now >= next_sample:
                    last_map = loader.map_samples[-1] if loader.map_samples else {}
                    prod_samples.append(
                        {
                            "elapsed_s": round(now - started, 1),
                            "cpu_s": loader.cpu_seconds(),
                            "rss_kb": loader.peak_rss_kb(),
                            "map_entries": last_map.get("entries"),
                        }
                    )
                    next_sample = now + max(5.0, args.interval)
                time.sleep(0.25)
            for t in threads:
                t.join(timeout=5)
            for child in prod_children_list:
                try:
                    child.wait(timeout=30)
                except subprocess.TimeoutExpired:
                    child.kill()
                    child.wait(timeout=5)
                expected_tgids.add(child.pid)
            totals = [r for r in results if r is not None]
            tcp_sent = sum(int(r["tcp_sent"]) for r in totals)
            tcp_received = sum(int(r["tcp_recv"]) for r in totals)
            udp_sent = sum(int(r["udp_sent"]) for r in totals)
            udp_received = sum(int(r["udp_recv"]) for r in totals)
            prod_transfers = sum(int(r["transfers"]) for r in totals)
        elif map_churn:
            deadline = time.monotonic() + args.churn_timeout
            while time.monotonic() < deadline:
                if loader.map_samples and loader.map_samples[-1]["entries"] >= args.churn_target:
                    break
                batch: list[subprocess.Popen[str]] = []
                for _ in range(args.churn_batch):
                    child = spawn_soak_child(churn_child_bytes)
                    batch.append(child)
                    churn_children.append(child)
                    expected_tgids.add(child.pid)
                for child in batch:
                    try:
                        child.wait(timeout=30)
                    except subprocess.TimeoutExpired:
                        child.kill()
                        child.wait(timeout=5)
                    if child.returncode == 0:
                        churn_successes += 1
                churn_batches += 1
                last_map = loader.map_samples[-1] if loader.map_samples else {}
                churn_samples.append(
                    {
                        "elapsed_s": round(time.monotonic() - started, 1),
                        "entries": last_map.get("entries"),
                        "children": len(churn_children),
                        "successes": churn_successes,
                        "cpu_s": loader.cpu_seconds(),
                        "rss_kb": loader.peak_rss_kb(),
                    }
                )
                time.sleep(args.interval)
            tcp_sent = tcp_received = churn_successes * churn_child_bytes
            udp_sent = udp_received = churn_successes * churn_child_bytes
        elif wireguard:
            assert wg_topology is not None
            expected_tgids.add(os.getpid())
            wg_counters["wg_before"] = read_net_dev(WG_NAME)
            wg_counters["underlay_before"] = read_net_dev(VETH_CLIENT)
            wg_counters["peer_wg_before"] = read_net_dev_netns(WG_NAME, VETH_NS)
            server, server_pid = spawn_wg_server(WG_PEER_IP, tunnel_port)
            helpers.append(server)
            expected_tgids.add(server_pid)
            try:
                wg_helper_netns = os.stat(f"/proc/{server_pid}/ns/net").st_ino
            except FileNotFoundError:
                detail = server.stdout.read() if server.stdout else ""
                raise SystemExit(f"WireGuard server exited before netns inspection: {detail.strip()}")
            tcp_sent, tcp_received, tcp_s = run_tcp_transfer_remote(
                tunnel_bytes, WG_PEER_IP, tunnel_port
            )
            time.sleep(max(2.0, args.interval * 2.5))
            wg_counters["wg_after"] = read_net_dev(WG_NAME)
            wg_counters["underlay_after"] = read_net_dev(VETH_CLIENT)
            wg_counters["peer_wg_after"] = read_net_dev_netns(WG_NAME, VETH_NS)
            tcp_sent = tcp_received = 2 * tunnel_bytes
            udp_sent = udp_received = 0
        elif tunnel:
            assert tunnel_topology is not None
            expected_tgids.add(os.getpid())
            tunnel_counters["tunnel_before"] = read_net_dev(TUNNEL_NAME)
            tunnel_counters["underlay_before"] = read_net_dev(VETH_CLIENT)
            tunnel_counters["peer_tunnel_before"] = read_net_dev_netns(TUNNEL_NAME, VETH_NS)
            server, server_pid = spawn_tunnel_server(tunnel_port, tunnel_bytes)
            helpers.append(server)
            expected_tgids.add(server_pid)
            try:
                tunnel_helper_netns = os.stat(f"/proc/{server_pid}/ns/net").st_ino
            except FileNotFoundError:
                detail = server.stdout.read() if server.stdout else ""
                raise SystemExit(f"tunnel server exited before netns inspection: {detail.strip()}")
            tcp_sent, tcp_received, tcp_s = run_tcp_transfer_remote(
                tunnel_bytes, TUNNEL_PEER_IP, tunnel_port
            )
            time.sleep(max(2.0, args.interval * 2.5))
            tunnel_counters["tunnel_after"] = read_net_dev(TUNNEL_NAME)
            tunnel_counters["underlay_after"] = read_net_dev(VETH_CLIENT)
            tunnel_counters["peer_tunnel_after"] = read_net_dev_netns(TUNNEL_NAME, VETH_NS)
            tcp_sent = tcp_received = 2 * tunnel_bytes
            tunnel_payload = tunnel_bytes
            udp_sent = udp_received = 0
        elif wildcard:
            assert veth is not None
            expected_tgids.add(os.getpid())
            server_result: dict[str, dict[str, int]] = {}

            def wildcard_server() -> None:
                server_result["counts"] = receive_wildcard_udp(
                    wildcard_port, wildcard_bytes, wildcard_bytes, timeout=30
                )

            server_thread = threading.Thread(target=wildcard_server, daemon=True)
            server_thread.start()
            time.sleep(0.2)
            send_udp_once(wildcard_bytes, "127.0.0.1", wildcard_port)
            helper, helper_pid = spawn_wildcard_helper(
                VETH_CLIENT_IP, wildcard_port, wildcard_bytes
            )
            helpers.append(helper)
            expected_tgids.add(helper_pid)
            helper_netns = os.stat(f"/proc/{helper_pid}/ns/net").st_ino
            server_thread.join(timeout=35)
            wildcard_counts = server_result.get("counts", {})
            time.sleep(max(2.0, args.interval * 2.5))
            tcp_sent = tcp_received = 0
            udp_sent = udp_received = 2 * wildcard_bytes
        elif netns_key:
            assert netns_pair is not None
            helper, helper_pid = spawn_netns_key_helper(
                netns_pair.paths[0], netns_pair.paths[1], netns_key_bytes
            )
            helpers.append(helper)
            expected_tgids.add(helper_pid)
            # Helper does two transfers with short holds; allow several snapshots.
            time.sleep(8)
            tcp_sent = tcp_received = 2 * netns_key_bytes
            udp_sent = udp_received = 0
        elif nic:
            expected_tgids.add(os.getpid())
            nic_before = read_net_dev(peer_interface)
            try:
                tcp_sent, tcp_received, tcp_s = run_tcp_transfer_remote(
                    size_bytes, peer_host, args.peer_port
                )
                time.sleep(max(1.0, args.interval * 1.5))
                udp_sent, udp_received, udp_s = run_udp_transfer_remote(
                    size_bytes, peer_host, args.peer_udp_port
                )
            except OSError as exc:
                raise SystemExit(
                    f"NIC echo transfer failed: {exc}. Check that the Termux listener is "
                    f"still running on {peer_host}:{args.peer_port} (TCP) and "
                    f"{args.peer_udp_port} (UDP)."
                ) from exc
            time.sleep(max(1.0, args.interval * 1.5))
            nic_after = read_net_dev(peer_interface)
        elif veth:
            expected_tgids.add(os.getpid())
            # The server runs in the root namespace, where only flowvA
            # (192.0.2.1) exists; 192.0.2.2 lives in flowvns.
            tcp_sent, tcp_received, tcp_s, helper, helper_pid = run_tcp_transfer_netns(
                size_bytes, VETH_CLIENT_IP
            )
            helpers.append(helper)
            expected_tgids.add(helper_pid)
            if not helper_netns:
                helper_netns = os.stat(f"/proc/{helper_pid}/ns/net").st_ino
            time.sleep(max(1.0, args.interval * 1.5))
            udp_sent, udp_received, udp_s, helper, helper_pid = run_udp_transfer_netns(
                size_bytes, VETH_CLIENT_IP
            )
            helpers.append(helper)
            expected_tgids.add(helper_pid)
        else:
            tcp_sent, tcp_received, tcp_s = run_tcp_transfer(size_bytes)
            time.sleep(max(1.0, args.interval * 1.5))
            udp_sent, udp_received, udp_s = run_udp_transfer(size_bytes)
            expected_tgids.add(os.getpid())
        # Allow at least two more snapshots so all deltas are emitted.
        time.sleep(max(1.0, args.interval * 2.5))
        lo_after = read_net_dev("lo") if not veth and not nic and not netns_key and not map_churn and not tunnel and not wireguard else None
        if veth and args.mode == "veth":
            veth_after = {"client": read_net_dev(VETH_CLIENT)}
    finally:
        cpu_after = loader.cpu_seconds() if loader else None
        rss_kb = loader.peak_rss_kb() if loader else None
        if loader:
            loader.stop()
        for helper in helpers:
            try:
                if helper.stdin and not helper.stdin.closed:
                    helper.stdin.write("\n")
                    helper.stdin.flush()
            except OSError:
                pass
            try:
                helper.wait(timeout=5)
            except subprocess.TimeoutExpired:
                helper.kill()
                helper.wait(timeout=5)
        for child in soak_children:
            if child.poll() is None:
                child.kill()
                child.wait(timeout=5)
        for child in churn_children:
            if child.poll() is None:
                child.kill()
                child.wait(timeout=5)
        for child in prod_children:
            if child.poll() is None:
                child.kill()
                child.wait(timeout=5)
        if veth:
            veth.stop()
        if netns_pair:
            netns_pair.stop()
        if tunnel_topology:
            tunnel_topology.stop()
        if wg_topology:
            wg_topology.stop()

    elapsed = time.monotonic() - started
    for role, counters in veth_before.items():
        if counters is not None:
            counters["name"] = VETH_CLIENT  # type: ignore[assignment]
    summary = summarize(loader.entries if loader else [], loader.unattributed if loader else [], expected_tgids)
    cpu_s = round(cpu_after - cpu_before, 3) if cpu_before is not None and cpu_after is not None else None
    cpu_pct = round(cpu_s * 100.0 / elapsed, 3) if cpu_s is not None and elapsed > 0 else None

    by_proto = summary["expected_by_proto"]
    tcp_attributed_tx = int(by_proto[1]["tx"])
    tcp_attributed_rx = int(by_proto[1]["rx"])
    udp_attributed_tx = int(by_proto[2]["tx"])
    udp_attributed_rx = int(by_proto[2]["rx"])

    check_tcp_tx = abs(tcp_attributed_tx - tcp_sent) <= tcp_sent * 0.01
    check_tcp_rx = abs(tcp_attributed_rx - tcp_received) <= tcp_received * 0.01
    if nic or netns_key or tunnel:
        check_udp_tx = None
        check_udp_rx = None
    else:
        check_udp_tx = udp_sent > 0 and abs(udp_attributed_tx - udp_sent) <= udp_sent * 0.01
        check_udp_rx = udp_received > 0 and abs(udp_attributed_rx - udp_received) <= udp_received * 0.01
    check_peer_bytes = (
        None
        if not nic or args.peer_bytes is None
        else abs(int(args.peer_bytes) - tcp_sent) <= tcp_sent * 0.01
    )
    check_peer_udp_bytes = (
        None
        if not nic or args.peer_udp_bytes is None
        else abs(int(args.peer_udp_bytes) - udp_sent) <= udp_sent * 0.01
    )
    generations = summary.get("expected_generations_by_tgid", {})
    check_single_generation = bool(expected_tgids) and all(
        int(v) == 1 for v in generations.values()
    ) and len(generations) == len(expected_tgids)
    check_identity = bool(summary["expected_identity_verified"])
    check_netns_collision: bool | None = None
    check_churn_bytes: bool | None = None
    check_map_target: bool | None = None
    check_wildcard: bool | None = None
    check_tunnel: bool | None = None
    check_wireguard: bool | None = None
    if nic:
        root_netns = os.stat("/proc/self/ns/net").st_ino
        expected_interface_description = (
            f"TCP and UDP TX/RX on `{peer_interface}` in the root netns via peer {peer_host}"
        )
        transfer_scope = (
            f"TCP echo to {peer_host}:{args.peer_port} and UDP echo to {peer_host}:{args.peer_udp_port} via {peer_interface}"
        )
        counter_caveat = f"`{peer_interface}` counters include IP/link and Wi-Fi overhead."
        process_scope_caveat = (
            "The expected rows are the parent client process; peer byte counts are "
            "reported separately when supplied via --peer-bytes/--peer-udp-bytes."
        )
        interface_caveat = (
            f"TCP and UDP echo through physical {peer_interface} to a peer on the phone hotspot. "
            "This validates hardware-interface attribution only; it does not cover VPN/tunnel "
            "paths, multiple simultaneous interfaces, or wildcard multi-interface sockets. "
            "Traffic terminates at the phone, so no cellular payload is used for this test."
        )

        def nic_proto_ok(proto: int) -> bool:
            rows = [
                p for p in summary["processes"]
                if int(p["proto"]) == proto and int(p["tgid"]) in expected_tgids
            ]
            return (
                len(rows) == 1
                and rows[0]["ifname"] == peer_interface
                and int(rows[0]["netns_id"]) == root_netns
                and int(rows[0]["tx"]) > 0
                and int(rows[0]["rx"]) > 0
                and int(by_proto[proto]["unresolved_tx"]) == 0
                and int(by_proto[proto]["unresolved_rx"]) == 0
            )

        check_interface = nic_proto_ok(1) and nic_proto_ok(2)
    elif wireguard:
        assert wg_topology is not None
        root_netns = os.stat("/proc/self/ns/net").st_ino
        expected_peer_ifname = f"{wg_topology.peer_ifindex}@{wg_helper_netns}"
        expected_interface_description = (
            f"client TX on `{WG_NAME}` (root) and server RX on {expected_peer_ifname}"
        )
        transfer_scope = f"TCP echo over WireGuard above veth underlay"
        counter_caveat = "WireGuard and underlay counters are sampled separately."
        process_scope_caveat = (
            "The parent client and the peer-netns WireGuard server are expected processes. "
            "Server identity is n/a after exit; kernel-captured identity is retained."
        )
        interface_caveat = (
            "A real WireGuard kernel device above a veth underlay exercises tunnel-device "
            "attribution. Overlay and underlay both carry packets; double-counting is "
            "reported, not treated as additional process traffic. No public VPN provider."
        )
        parent_tx = [
            p for p in summary["processes"]
            if int(p["tgid"]) == os.getpid() and int(p["proto"]) == 1 and int(p["tx"]) > 0
        ]
        peer_rx = [
            p for p in summary["processes"]
            if int(p["tgid"]) == server_pid and int(p["proto"]) == 1 and int(p["rx"]) > 0
        ]
        underlay_rows = [
            p for p in summary["processes"]
            if int(p["tgid"]) in expected_tgids and p["ifname"] == VETH_CLIENT
        ]
        check_wireguard = (
            len(parent_tx) == 1
            and parent_tx[0]["ifname"] == WG_NAME
            and int(parent_tx[0]["netns_id"]) == root_netns
            and int(parent_tx[0]["tx"]) == tunnel_bytes
            and len(peer_rx) == 1
            and peer_rx[0]["ifname"] == expected_peer_ifname
            and int(peer_rx[0]["netns_id"]) == wg_helper_netns
            and int(peer_rx[0]["rx"]) == tunnel_bytes
            and not underlay_rows
        )
        check_interface = check_wireguard
        check_identity = None
    elif tunnel:
        assert tunnel_topology is not None
        root_netns = os.stat("/proc/self/ns/net").st_ino
        helper_netns = tunnel_helper_netns
        expected_peer_ifname = f"{tunnel_topology.peer_ifindex}@{helper_netns}"
        expected_interface_description = (
            f"client TX on `{TUNNEL_NAME}` (root) and server RX on {expected_peer_ifname}"
        )
        transfer_scope = f"TCP echo over {tunnel_topology.mode} tunnel above a veth underlay"
        counter_caveat = "tunnel and underlay counters are sampled separately."
        process_scope_caveat = (
            "The parent client and the peer-netns server are expected processes. "
            "Server identity is n/a after exit; kernel-captured identity is retained."
        )
        interface_caveat = (
            f"A {tunnel_topology.mode} tunnel above a veth underlay exercises tunnel-device "
            "attribution. Overlay and underlay both carry the packets; double-counting is "
            "reported, not treated as additional process traffic. No physical NIC or VPN daemon."
        )
        parent_tx = [
            p for p in summary["processes"]
            if int(p["tgid"]) == os.getpid() and int(p["proto"]) == 1 and int(p["tx"]) > 0
        ]
        peer_rx = [
            p for p in summary["processes"]
            if int(p["tgid"]) == server_pid and int(p["proto"]) == 1 and int(p["rx"]) > 0
        ]
        underlay_rows = [
            p for p in summary["processes"]
            if int(p["tgid"]) in expected_tgids and p["ifname"] == VETH_CLIENT
        ]
        check_tunnel = (
            len(parent_tx) == 1
            and parent_tx[0]["ifname"] == TUNNEL_NAME
            and int(parent_tx[0]["netns_id"]) == root_netns
            and int(parent_tx[0]["tx"]) == tunnel_payload
            and len(peer_rx) == 1
            and peer_rx[0]["ifname"] == expected_peer_ifname
            and int(peer_rx[0]["netns_id"]) == helper_netns
            and int(peer_rx[0]["rx"]) == tunnel_payload
            and not underlay_rows
        )
        check_interface = check_tunnel
        check_identity = None
    elif prod_soak or identity_diag:
        expected_interface_description = "all expected rows on `lo`"
        transfer_scope = (
            f"{args.prod_workers} concurrent workers, {args.duration:.0f}s, "
            f"{args.soak_size_mib:.3f} MiB per protocol per transfer"
        )
        counter_caveat = "`lo` counters include IP/link bytes above socket payload."
        process_scope_caveat = (
            "Expected rows cover the parent workers and any short-lived children "
            "spawned during the soak; per-process byte totals are aggregated."
        )
        interface_caveat = (
            "Loopback-only production-load proxy: high concurrency and longer duration, "
            "not a real Internet workload, physical NIC, or VPN."
        )
        check_interface = (
            summary["expected_resolved_ifnames"] == ["lo"]
            and int(summary["expected_unresolved_rows"]) == 0
        )
        check_identity = (
            None if (identity_diag or prod_children) else bool(summary["expected_identity_verified"])
        )
    elif wildcard:
        assert veth is not None
        root_netns = os.stat("/proc/self/ns/net").st_ino
        expected_helper_ifname = f"{veth.server_ifindex}@{helper_netns}"
        expected_interface_description = (
            f"one wildcard UDP listener on `lo` and `{VETH_CLIENT}` in root netns; "
            f"client TX from flowvB ({expected_helper_ifname})"
        )
        transfer_scope = "wildcard UDP server receiving from loopback and veth clients"
        counter_caveat = "per-interface counters are not sampled for this probe."
        process_scope_caveat = (
            "The parent wildcard server and the peer-netns client are expected processes. "
            "Client identity is n/a after exit; kernel-captured identity is retained."
        )
        interface_caveat = (
            "A temporary veth pair plus loopback in the root netns exercises one "
            "wildcard-bound UDP socket receiving on two interfaces. This is not a VPN, "
            "physical-NIC, or multiple-simultaneous-interface workload."
        )
        parent_rx = [
            p for p in summary["processes"]
            if int(p["tgid"]) == os.getpid() and int(p["proto"]) == 2 and int(p["rx"]) > 0
        ]
        parent_tx = [
            p for p in summary["processes"]
            if int(p["tgid"]) == os.getpid() and int(p["proto"]) == 2 and int(p["tx"]) > 0
        ]
        helper_tx = [
            p for p in summary["processes"]
            if int(p["tgid"]) in (expected_tgids - {os.getpid()})
            and int(p["proto"]) == 2 and int(p["tx"]) > 0
        ]
        check_wildcard = (
            len(parent_rx) == 2
            and {p["ifname"] for p in parent_rx} == {"lo", VETH_CLIENT}
            and any(int(p["rx"]) == wildcard_counts.get("root", -1) and p["ifname"] == "lo" for p in parent_rx)
            and any(int(p["rx"]) == wildcard_counts.get("peer", -1) and p["ifname"] == VETH_CLIENT for p in parent_rx)
            and len(parent_tx) == 1
            and parent_tx[0]["ifname"] == "lo"
            and int(parent_tx[0]["tx"]) == wildcard_counts.get("root", -1)
            and len(helper_tx) == 1
            and helper_tx[0]["ifname"] == expected_helper_ifname
            and int(helper_tx[0]["tx"]) == wildcard_counts.get("peer", -1)
        )
        check_interface = (
            all(p["ifname"] in ("lo", VETH_CLIENT, expected_helper_ifname) for p in summary["processes"]
                if int(p["tgid"]) in expected_tgids)
            and int(by_proto[2]["unresolved_tx"]) == 0
            and int(by_proto[2]["unresolved_rx"]) == 0
        )
        check_identity = None
    elif map_churn:
        expected_interface_description = "all expected child rows on `lo`"
        transfer_scope = (
            f"batches of {args.churn_batch} short-lived TCP+UDP children, "
            f"{churn_child_bytes} bytes per protocol each"
        )
        counter_caveat = "child traffic uses per-netns loopback; counters are not sampled."
        process_scope_caveat = (
            "Each short-lived child is an expected process generation. Identity verification "
            "is n/a because children exit before snapshot enrichment; kernel-captured identity "
            "is retained."
        )
        interface_caveat = (
            "Loopback-only churn in the root netns. This probes map capacity and accounting "
            "under churn, not physical interfaces, VPNs, or multi-interface behavior."
        )
        check_interface = (
            summary["expected_resolved_ifnames"] == ["lo"]
            and int(summary["expected_unresolved_rows"]) == 0
        )
        check_identity = None
        last_entries = churn_samples[-1]["entries"] if churn_samples else None
        check_map_target = last_entries is not None and int(last_entries) >= args.churn_target
        expected_churn_bytes = churn_successes * churn_child_bytes
        check_churn_bytes = (
            expected_churn_bytes > 0
            and abs(int(by_proto[1]["tx"]) - expected_churn_bytes) <= expected_churn_bytes * 0.01
            and abs(int(by_proto[1]["rx"]) - expected_churn_bytes) <= expected_churn_bytes * 0.01
            and abs(int(by_proto[2]["tx"]) - expected_churn_bytes) <= expected_churn_bytes * 0.01
            and abs(int(by_proto[2]["rx"]) - expected_churn_bytes) <= expected_churn_bytes * 0.01
        )
    elif netns_key:
        expected_interface_description = "both transfers on `lo` in two distinct netns"
        transfer_scope = "one helper process moves between two netns via setns()"
        counter_caveat = "per-netns `lo` counters are not sampled; exactness is checked against rows."
        process_scope_caveat = (
            "One TGID/start/uid sends in two netns; two rows with distinct netns ids "
            "must appear, otherwise the identity key is still merging namespaces. "
            "Live /proc verification is not expected here because the emitting server "
            "threads exit before snapshots; kernel-captured identity is retained."
        )
        interface_caveat = (
            "Two temporary netns with loopback only. Interfaces in a netns other than "
            "the loader's are reported as ifindex@netns, which is expected here. This "
            "validates netns in the identity key, not VPN/tunnel paths or multiple "
            "simultaneous interfaces."
        )
        netns_rows = [
            p for p in summary["processes"]
            if int(p["tgid"]) in expected_tgids and int(p["proto"]) == 1 and int(p["tx"]) > 0
        ]
        netns_ids = {int(p["netns_id"]) for p in netns_rows}
        check_netns_collision = (
            len(netns_rows) == 2
            and len(netns_ids) == 2
            and 0 not in netns_ids
            and all(int(p["tx"]) == netns_key_bytes and int(p["rx"]) == netns_key_bytes for p in netns_rows)
            and len({int(p["start_ns"]) for p in netns_rows}) == 1
            and len({int(p["uid"]) for p in netns_rows}) == 1
        )
        check_interface = (
            len(netns_rows) == 2
            and all(str(p["ifname"]).startswith("1@") for p in netns_rows)
            and all(str(p["ifname"]) == f"1@{p['netns_id']}" for p in netns_rows)
            and int(by_proto[1]["unresolved_tx"]) == 0
            and int(by_proto[1]["unresolved_rx"]) == 0
        )
        # Not a gate: the emitting server threads exit before /proc enrichment.
        check_identity = None
    elif veth:
        root_netns = os.stat("/proc/self/ns/net").st_ino
        helper_tgids = expected_tgids - {os.getpid()}
        expected_helper_ifname = f"{veth.server_ifindex}@{helper_netns}"
        expected_interface_description = (
            f"RX via `{VETH_CLIENT}` in the root netns; TX via flowvB ifindex "
            f"{veth.server_ifindex} in netns {helper_netns}"
        )
        transfer_scope = "TCP then UDP, root server plus client helpers in flowvns"
        counter_caveat = "`flowvA` counters count IP/link bytes including headers."
        process_scope_caveat = (
            "The expected rows include the root server and the client helpers in "
            "flowvns; each TGID has exactly one verified process generation."
        )
        interface_caveat = (
            "A temporary veth pair spans two network namespaces, forcing packets over "
            "both interfaces. ifindex is namespace-scoped, so the helper namespace's "
            "interface is reported as ifindex@netns rather than a resolved name. This "
            "does not cover physical NICs, VPN/tunnel paths, or virtual-interface "
            "double-counting."
        )

        def veth_direction_ok(proto: int) -> bool:
            rows = [p for p in summary["processes"] if int(p["proto"]) == proto]
            parent = [p for p in rows if int(p["tgid"]) == os.getpid() and int(p["rx"]) > 0]
            helper = [p for p in rows if int(p["tgid"]) in helper_tgids and int(p["tx"]) > 0]
            return (
                len(parent) == 1
                and parent[0]["ifname"] == VETH_CLIENT
                and int(parent[0]["netns_id"]) == root_netns
                and int(parent[0]["tx"]) == 0
                and len(helper) == 1
                and helper[0]["ifname"] == expected_helper_ifname
                and int(helper[0]["netns_id"]) == helper_netns
                and int(helper[0]["rx"]) == 0
                and int(by_proto[proto]["unresolved_tx"]) == 0
                and int(by_proto[proto]["unresolved_rx"]) == 0
            )

        check_interface = veth_direction_ok(1) and veth_direction_ok(2)
    elif soak:
        expected_interface_description = "all expected-process rows on `lo`"
        transfer_scope = (
            f"TCP and UDP repeated loopback transfers for {args.duration:.0f}s with short-lived child churn"
        )
        counter_caveat = "`lo` counters count IP/link bytes including headers."
        process_scope_caveat = (
            f"Exactness checks cover only the parent TGID ({os.getpid()}); "
            f"{len(soak_children)} short-lived child transfer(s) are reported as "
            "background rows and are not asserted."
        )
        interface_caveat = (
            "Loopback traffic appears once in each interface direction. Child churn "
            "rows may have unverified identity after exit; map growth is reported as "
            "observation only, since the BPF map has no expiry."
        )
        check_interface = (
            summary["expected_resolved_ifnames"] == ["lo"]
            and int(summary["expected_unresolved_rows"]) == 0
        )
    else:
        expected_interface_description = "all expected-process rows on `lo`"
        transfer_scope = "TCP then UDP, same process"
        counter_caveat = "`lo` counters count IP/link bytes including headers."
        process_scope_caveat = (
            "The expected-process rows are the load generator itself (its server "
            "threads share the TGID)."
        )
        interface_caveat = (
            "Loopback traffic appears once in each interface direction; this run does "
            "not exercise physical-interface, namespace, VPN, or container paths, and "
            "it does not prove that unconnected/uncached UDP sockets always resolve "
            "an interface."
        )
        check_interface = (
            summary["expected_resolved_ifnames"] == ["lo"]
            and int(summary["expected_unresolved_rows"]) == 0
        )
    check_unattributed = int(summary["unattributed_tx"]) == 0 and int(summary["unattributed_rx"]) == 0

    if map_churn:
        summary["process_count"] = len(summary["processes"])
        summary["processes"] = summary["processes"][:20]

    if identity_diag:
        mode_description = f"identity diagnostic, {args.prod_workers} workers × {args.duration:.0f}s"
    elif prod_soak:
        mode_description = f"production-load soak, {args.prod_workers} workers × {args.duration:.0f}s"
    elif tunnel:
        mode_description = f"TCP echo over {tunnel_topology.mode} tunnel above veth underlay"
    elif wireguard:
        mode_description = "TCP echo over WireGuard above veth underlay"
    elif wildcard:
        mode_description = "wildcard UDP listener on two interfaces"
    elif map_churn:
        mode_description = "high-rate child churn toward BPF map capacity"
    elif netns_key:
        mode_description = "same-PID two-netns identity collision"
    elif veth:
        mode_description = "temporary veth pair across two network namespaces (192.0.2.1/24 <-> 192.0.2.2/24)"
    elif nic:
        mode_description = f"TCP+UDP echo via physical `{peer_interface}` to {peer_host}:{args.peer_port} (UDP {args.peer_udp_port})"
    elif soak:
        mode_description = f"loopback soak, {args.duration:.0f}s, {args.soak_size_mib:.3f} MiB per protocol per iteration"
    else:
        mode_description = "loopback `lo`"

    ctx = {
        "date": datetime.now(timezone.utc).isoformat(timespec="seconds"),
        "kernel": os.uname().release,
        "loader": str(args.loader),
        "interval": args.interval,
        "mode": args.mode,
        "mode_description": mode_description,
        "expected_interface_description": expected_interface_description,
        "interface_caveat": interface_caveat,
        "transfer_scope": transfer_scope,
        "counter_caveat": counter_caveat,
        "process_scope_caveat": process_scope_caveat,
        "root_netns_id": os.stat("/proc/self/ns/net").st_ino,
        "helper_netns_id": helper_netns,
        "nic": nic,
        "netns_key": netns_key,
        "map_churn": map_churn,
        "wildcard": wildcard,
        "tunnel": tunnel,
        "wireguard": wireguard,
        "wg_counters": wg_counters if wireguard else {},
        "check_wireguard": check_wireguard,
        "prod_soak": prod_soak,
        "identity_diag": identity_diag,
        "clk_tck": loader.meta.get("clk_tck", 100) if loader else 100,
        "proc_startticks": read_proc_startticks(os.getpid()),
        "prod_workers": args.prod_workers if prod_soak else None,
        "prod_transfers": prod_transfers,
        "prod_children": len(prod_children),
        "prod_samples": prod_samples,
        "tunnel_mode": tunnel_topology.mode if tunnel else None,
        "tunnel_name": TUNNEL_NAME if tunnel else None,
        "tunnel_counters": tunnel_counters if tunnel else {},
        "check_tunnel": check_tunnel,
        "wildcard_counts": wildcard_counts if wildcard else {},
        "wildcard_bytes": wildcard_bytes if wildcard else None,
        "wildcard_client_ifindex": veth.client_ifindex if wildcard and veth else None,
        "wildcard_server_ifindex": veth.server_ifindex if wildcard and veth else None,
        "check_wildcard": check_wildcard,
        "churn_target": args.churn_target if map_churn else None,
        "churn_batch": args.churn_batch if map_churn else None,
        "churn_child_bytes": churn_child_bytes if map_churn else None,
        "churn_children": len(churn_children),
        "churn_successes": churn_successes,
        "churn_batches": churn_batches,
        "churn_samples": churn_samples,
        "check_map_target": check_map_target if map_churn else None,
        "check_churn_bytes": check_churn_bytes if map_churn else None,
        "overflow": loader.overflow_samples[-1] if loader and loader.overflow_samples else None,
        "netns_key_bytes": netns_key_bytes if netns_key else None,
        "netns_key_rows": netns_rows if netns_key else [],
        "check_netns_collision": check_netns_collision if netns_key else None,
        "nic_interface": peer_interface if nic else None,
        "nic_peer_host": peer_host if nic else None,
        "nic_peer_port": args.peer_port if nic else None,
        "nic_peer_udp_port": args.peer_udp_port if nic else None,
        "nic_peer_bytes": args.peer_bytes if nic else None,
        "nic_peer_udp_bytes": args.peer_udp_bytes if nic else None,
        "nic_before": nic_before,
        "nic_after": nic_after,
        "soak": soak,
        "soak_duration_s": args.duration,
        "soak_iterations": soak_iterations,
        "soak_children": len(soak_children),
        "soak_samples": soak_samples,
        "soak_map_entries": [s["map_entries"] for s in soak_samples if s["map_entries"] is not None],
        "soak_cpu_samples": [s["cpu_s"] for s in soak_samples if s["cpu_s"] is not None],
        "soak_rss_samples": [s["rss_kb"] for s in soak_samples if s["rss_kb"] is not None],
        "size_bytes": (
            tunnel_bytes if tunnel
            else (wildcard_bytes if wildcard else (churn_child_bytes if map_churn else (int(args.soak_size_mib * 1024 * 1024) if prod_soak else size_bytes)))
        ),
        "size_mib": (
            (args.tunnel_size_mib if tunnel else
             (args.wildcard_size_mib if wildcard else
              (args.churn_child_size_kib / 1024.0 if map_churn else
               (args.soak_size_mib if prod_soak else args.size_mib))))
        ),
        "tcp_sent": tcp_sent,
        "tcp_received": tcp_received,
        "tcp_elapsed_s": round(tcp_s, 3),
        "udp_sent": udp_sent,
        "udp_received": udp_received,
        "udp_elapsed_s": round(udp_s, 3),
        "elapsed_s": elapsed,
        "lo_before": lo_before,
        "lo_after": lo_after,
        "veth_client_before": veth_before.get("client"),
        "veth_client_after": veth_after.get("client"),
        "veth_server_before": veth_before.get("server"),
        "veth_server_after": veth_after.get("server"),
        "summary": summary,
        "cpu_s": cpu_s,
        "cpu_pct": cpu_pct,
        "rss_kb": rss_kb,
        "check_tcp_tx": check_tcp_tx,
        "check_tcp_rx": check_tcp_rx,
        "check_udp_tx": check_udp_tx,
        "check_udp_rx": check_udp_rx,
        "check_single_generation": check_single_generation,
        "check_identity": check_identity,
        "check_interface": check_interface,
        "check_unattributed": check_unattributed,
        "check_peer_bytes": check_peer_bytes,
        "check_peer_udp_bytes": check_peer_udp_bytes,
    }

    write_report(report_path, ctx)
    payload = {**ctx, "report": str(report_path)}
    print(json.dumps(payload, indent=2))
    if not args.json:
        print(f"\nreport: {report_path}", file=sys.stderr)
    passed = (
        check_tcp_tx
        and check_tcp_rx
        and (check_udp_tx if check_udp_tx is not None else True)
        and (check_udp_rx if check_udp_rx is not None else True)
        and check_single_generation
        and (check_identity if check_identity is not None else True)
        and check_interface
        and (check_peer_bytes if check_peer_bytes is not None else True)
        and (check_peer_udp_bytes if check_peer_udp_bytes is not None else True)
        and (check_netns_collision if check_netns_collision is not None else True)
        and (check_churn_bytes if check_churn_bytes is not None else True)
        and (check_wildcard if check_wildcard is not None else True)
        and (check_tunnel if check_tunnel is not None else True)
        and (check_wireguard if check_wireguard is not None else True)
    )
    return 0 if passed else 1


if __name__ == "__main__":
    raise SystemExit(main())
