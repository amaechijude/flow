#!/usr/bin/env python3
"""Controlled validation for the Flow eBPF TCP+UDP attribution prototype.

Runs known-size loopback TCP and UDP transfers while the privileged loader
samples, then compares per-process attributed bytes against the expected
payloads and the kernel's loopback interface counters. It also verifies that
the expected process's traffic is attributed to the `lo` interface, and
records loader CPU time and peak RSS as a first overhead datapoint.

This script MUST run as root (or with CAP_BPF/CAP_PERFMON/CAP_SYS_ADMIN) because
it loads and attaches BPF programs. It does not modify the system beyond loading
the prototype program for the duration of the run.

Usage:
    sudo python3 scripts/validate-ebpf-attribution.py --size-mib 16
    sudo python3 scripts/validate-ebpf-attribution.py --size-mib 64 --interval 1 --report out.md

Output: a Markdown report (default under docs/measurements/) plus a JSON summary on stdout.
"""

from __future__ import annotations

import argparse
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


def read_proc_cpu_ns(pid: int) -> int | None:
    """Return CPU time in nanoseconds from /proc/<pid>/schedstat (run time field)."""
    try:
        first = Path(f"/proc/{pid}/schedstat").read_text(encoding="utf-8").split()[0]
        return int(first)
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


def read_proc_peak_rss_kb(pid: int) -> int | None:
    try:
        for line in Path(f"/proc/{pid}/status").read_text(encoding="utf-8").splitlines():
            if line.startswith("VmHWM:"):
                return int(line.split()[1])
    except (OSError, ValueError):
        pass
    return None


class LoaderRun:
    """Starts the prototype loader in TSV mode and collects its snapshot events."""

    def __init__(self, loader: Path, interval: float, rounds: int = 1_000_000) -> None:
        self.loader = loader
        self.interval = interval
        self.rounds = rounds
        self.proc: subprocess.Popen[str] | None = None
        self.entries: list[dict[str, object]] = []
        self.unattributed: list[dict[str, int]] = []
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
                    }
                )
            elif line.startswith("U\t"):
                p = line.split("\t")
                if len(p) < 5:
                    continue
                self.unattributed.append(
                    {"tx_delta": int(p[3]), "rx_delta": int(p[4])}
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


def run_tcp_transfer(size_bytes: int, host: str = "127.0.0.1") -> tuple[int, int, float]:
    """Send size_bytes over a loopback TCP connection; return sent, received, elapsed."""
    server = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    server.bind((host, 0))
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
    with socket.create_connection((host, port), timeout=30) as client:
        client.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
        while sent < size_bytes:
            n = min(len(chunk), size_bytes - sent)
            client.sendall(chunk[:n])
            sent += n
        client.shutdown(socket.SHUT_WR)

    thread.join(timeout=60)
    server.close()
    return sent, received[0], time.monotonic() - started


def run_udp_transfer(
    size_bytes: int,
    host: str = "127.0.0.1",
    datagram_size: int = 4096,
) -> tuple[int, int, float]:
    """Send size_bytes of loopback UDP payload; return sent, received, elapsed.

    UDP may drop datagrams. The caller compares eBPF TX against bytes accepted by
    send() and eBPF RX against bytes actually delivered to recvfrom(), so drops
    are reported rather than hidden. QUIC would appear here as UDP payload too.
    """
    server = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    server.setsockopt(socket.SOL_SOCKET, socket.SO_RCVBUF, 8 << 20)
    server.bind((host, 0))
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
    expected_tgid: int,
) -> dict[str, object]:
    processes: dict[tuple[int, int, int, int, str], dict[str, object]] = {}
    for e in entries:
        ifname = str(e.get("ifname") or "")
        key = (
            int(e["tgid"]),
            int(e["start_ns"]),
            int(e["uid"]),
            int(e.get("proto") or 0),
            ifname,
        )
        cur = processes.setdefault(
            key,
            {
                "tgid": key[0],
                "start_ns": key[1],
                "uid": key[2],
                "proto": key[3],
                "ifname": key[4],
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

    expected_rows = [p for p in processes.values() if int(p["tgid"]) == expected_tgid]
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
        "expected_tgid": expected_tgid,
        "expected_tx": expected_tx,
        "expected_rx": expected_rx,
        "background_tx": attributed_tx - expected_tx,
        "background_rx": attributed_rx - expected_rx,
        "expected_generations": len({int(p["start_ns"]) for p in expected_rows}),
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
        f"- Payload per protocol: {ctx['size_bytes']} bytes ({ctx['size_mib']:.3f} MiB), loopback `lo`",
        f"- Transfers: TCP then UDP, same process",
        f"- Expected process TGID: {s['expected_tgid']}",
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
    lines += [
        "",
        "## Per-process rows (TGID + event-time start_ns + UID + proto + interface)",
        "",
        "| TGID | UID | Proto | Interface | TX | RX | comm | exe | start_ns | cgroup | identity verified | expected process |",
        "| ---: | ---: | --- | --- | ---: | ---: | --- | --- | ---: | --- | --- | --- |",
    ]
    for p in s["processes"]:
        lines.append(
            f"| {p['tgid']} | {p['uid']} | {PROTO_NAMES.get(int(p['proto']), '?')} | "
            f"{fmt_ifname(str(p['ifname']))} | {p['tx']} | {p['rx']} | {p['comm']} | {p['exe']} | "
            f"{p['start_ns']} | {p['cgroup_id']} | {'yes' if p['identity_verified'] else 'no'} | "
            f"{'yes' if p['tgid'] == s['expected_tgid'] else 'no'} |"
        )
    lines += [
        "",
        "## Interface attribution",
        "",
        f"- Expected-process resolved interface(s): {', '.join(s['expected_resolved_ifnames']) or '(none)'}",
        f"- Expected-process rows with unresolved interface: {s['expected_unresolved_rows']}",
        f"- Expected-process unresolved-interface bytes: TX {s['expected_unresolved_tx']}, RX {s['expected_unresolved_rx']}",
        f"- Expected-process TCP interface name(s): {', '.join(by_proto[1]['ifnames']) or '(none)'}",
        f"- Expected-process UDP interface name(s): {', '.join(by_proto[2]['ifnames']) or '(none)'}",
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
        f"- UDP attributed TX within ±1% of payload accepted by send(): {ctx['check_udp_tx']}",
        f"- UDP attributed RX within ±1% of payload delivered to recvfrom(): {ctx['check_udp_rx']}",
        f"- Expected TGID has exactly one process generation (no thread/start-time split): {ctx['check_single_generation']}",
        f"- Expected process identity verified against live `/proc`: {ctx['check_identity']}",
        f"- Expected-process traffic attributed to `lo`: {ctx['check_interface']}",
        f"- No unattributed bytes in a fully controlled transfer: {ctx['check_unattributed']}",
        "",
        "## Interpretation and caveats",
        "",
        "- eBPF counts TCP and UDP payload bytes at the send/recv function return; UDP/QUIC",
        "  framing is not identified, so QUIC traffic is expected to appear as UDP payload.",
        "  `lo` counters count IP/link bytes including headers. Ratios near 100% are expected",
        "  but exact equality is not.",
        "- The expected-process rows are the load generator itself (its server threads share the TGID);",
        "  background traffic from other processes during the run is reported separately.",
        "- UDP datagrams may be dropped by the receiver; the UDP RX comparison uses bytes actually",
        "  delivered to recvfrom(), and dropped bytes are not counted as attributed RX.",
        "- Loopback traffic appears once in each interface direction; this run does not exercise",
        "  physical-interface, namespace, VPN, or container paths, and it does not prove that",
        "  unconnected/uncached UDP sockets always resolve an interface.",
        "- This is a controlled sanity check, not a claim of complete attribution coverage.",
        "",
    ]
    path.write_text("\n".join(lines), encoding="utf-8")
    chown_to_invoker(path)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--size-mib", type=float, default=16.0, help="payload per protocol")
    parser.add_argument("--interval", type=float, default=1.0)
    parser.add_argument("--loader", type=Path, default=DEFAULT_LOADER)
    parser.add_argument("--report", type=Path, default=None)
    parser.add_argument("--json", action="store_true", help="only print the JSON summary")
    args = parser.parse_args()

    if os.geteuid() != 0:
        print("error: this validation loads BPF programs and must run as root", file=sys.stderr)
        return 2

    size_bytes = int(args.size_mib * 1024 * 1024)
    if size_bytes <= 0:
        print("error: --size-mib must be positive", file=sys.stderr)
        return 2

    report_path = args.report or (
        DEFAULT_REPORT_DIR / f"ebpf-controlled-{datetime.now(timezone.utc):%Y%m%dT%H%M%SZ}.md"
    )

    loader = LoaderRun(args.loader, args.interval)
    started = time.monotonic()
    cpu_before = None
    try:
        loader.start()
        cpu_before = loader.cpu_seconds()
        # Let the first snapshot establish a baseline (and prove the loader attached).
        time.sleep(max(1.0, args.interval * 1.5))

        lo_before = read_net_dev("lo")
        tcp_sent, tcp_received, tcp_s = run_tcp_transfer(size_bytes)
        time.sleep(max(1.0, args.interval * 1.5))
        udp_sent, udp_received, udp_s = run_udp_transfer(size_bytes)
        # Allow at least two more snapshots so all deltas are emitted.
        time.sleep(max(1.0, args.interval * 2.5))
    finally:
        cpu_after = loader.cpu_seconds()
        rss_kb = loader.peak_rss_kb()
        loader.stop()

    elapsed = time.monotonic() - started
    lo_after = read_net_dev("lo")
    expected_tgid = os.getpid()
    summary = summarize(loader.entries, loader.unattributed, expected_tgid)
    cpu_s = round(cpu_after - cpu_before, 3) if cpu_before is not None and cpu_after is not None else None
    cpu_pct = round(cpu_s * 100.0 / elapsed, 3) if cpu_s is not None and elapsed > 0 else None

    by_proto = summary["expected_by_proto"]
    tcp_attributed_tx = int(by_proto[1]["tx"])
    tcp_attributed_rx = int(by_proto[1]["rx"])
    udp_attributed_tx = int(by_proto[2]["tx"])
    udp_attributed_rx = int(by_proto[2]["rx"])

    check_tcp_tx = abs(tcp_attributed_tx - tcp_sent) <= tcp_sent * 0.01
    check_tcp_rx = abs(tcp_attributed_rx - tcp_received) <= tcp_received * 0.01
    check_udp_tx = udp_sent > 0 and abs(udp_attributed_tx - udp_sent) <= udp_sent * 0.01
    check_udp_rx = udp_received > 0 and abs(udp_attributed_rx - udp_received) <= udp_received * 0.01
    check_single_generation = int(summary["expected_generations"]) == 1
    check_identity = bool(summary["expected_identity_verified"])
    check_interface = (
        summary["expected_resolved_ifnames"] == ["lo"]
        and int(summary["expected_unresolved_rows"]) == 0
    )
    check_unattributed = int(summary["unattributed_tx"]) == 0 and int(summary["unattributed_rx"]) == 0

    ctx = {
        "date": datetime.now(timezone.utc).isoformat(timespec="seconds"),
        "kernel": os.uname().release,
        "loader": str(args.loader),
        "interval": args.interval,
        "size_bytes": size_bytes,
        "size_mib": args.size_mib,
        "tcp_sent": tcp_sent,
        "tcp_received": tcp_received,
        "tcp_elapsed_s": round(tcp_s, 3),
        "udp_sent": udp_sent,
        "udp_received": udp_received,
        "udp_elapsed_s": round(udp_s, 3),
        "elapsed_s": elapsed,
        "lo_before": lo_before,
        "lo_after": lo_after,
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
    }

    write_report(report_path, ctx)
    payload = {**ctx, "report": str(report_path)}
    print(json.dumps(payload, indent=2))
    if not args.json:
        print(f"\nreport: {report_path}", file=sys.stderr)
    passed = (
        check_tcp_tx
        and check_tcp_rx
        and check_udp_tx
        and check_udp_rx
        and check_single_generation
        and check_identity
        and check_interface
    )
    return 0 if passed else 1


if __name__ == "__main__":
    raise SystemExit(main())
