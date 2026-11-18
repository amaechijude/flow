#!/usr/bin/env python3
"""Controlled validation for the Flow eBPF TCP attribution prototype.

Runs a known-size loopback TCP transfer while the privileged loader samples, then
compares per-process attributed bytes against the expected payload and the kernel's
loopback interface counters. Also records loader CPU time and peak RSS as a first
overhead datapoint.

This script MUST run as root (or with CAP_BPF/CAP_PERFMON/CAP_SYS_ADMIN) because it
loads and attaches BPF programs. It does not modify the system beyond loading the
prototype program for the duration of the run.

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
        return read_proc_cpu_seconds(self.proc.pid) if self.proc else None

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


def run_transfer(size_bytes: int, host: str = "127.0.0.1") -> tuple[int, int, float]:
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


def summarize(entries: list[dict[str, object]], unattributed: list[dict[str, int]]) -> dict[str, object]:
    processes: dict[tuple[int, int, int], dict[str, object]] = {}
    for e in entries:
        key = (int(e["tgid"]), int(e["start_ns"]), int(e["uid"]))
        cur = processes.setdefault(
            key,
            {
                "tgid": key[0],
                "start_ns": key[1],
                "uid": key[2],
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

    attributed_tx = sum(int(p["tx"]) for p in processes.values())
    attributed_rx = sum(int(p["rx"]) for p in processes.values())
    unattributed_tx = sum(u["tx_delta"] for u in unattributed)
    unattributed_rx = sum(u["rx_delta"] for u in unattributed)
    return {
        "processes": sorted(processes.values(), key=lambda p: int(p["tx"]) + int(p["rx"]), reverse=True),
        "attributed_tx": attributed_tx,
        "attributed_rx": attributed_rx,
        "unattributed_tx": unattributed_tx,
        "unattributed_rx": unattributed_rx,
    }


def pct(part: int, whole: int) -> float | None:
    return round(part * 100.0 / whole, 3) if whole else None


def write_report(path: Path, ctx: dict[str, object]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    s = ctx["summary"]
    assert isinstance(s, dict)
    lines = [
        "# eBPF TCP attribution controlled validation",
        "",
        f"- Date (UTC): {ctx['date']}",
        f"- Kernel: `{ctx['kernel']}`",
        f"- Loader: `{ctx['loader']}` (interval {ctx['interval']} s)",
        f"- Transfer: {ctx['size_bytes']} bytes ({ctx['size_mib']:.3f} MiB) over loopback `lo`",
        f"- Loader wall time: {ctx['elapsed_s']:.2f} s",
        "",
        "## Results",
        "",
        "| Metric | Bytes |",
        "| --- | ---: |",
        f"| Payload sent (userspace) | {ctx['sent']} |",
        f"| Payload received (userspace) | {ctx['received']} |",
        f"| Attributed TX (eBPF, `tcp_sendmsg`) | {s['attributed_tx']} |",
        f"| Attributed RX (eBPF, `tcp_recvmsg`) | {s['attributed_rx']} |",
        f"| Unattributed TX | {s['unattributed_tx']} |",
        f"| Unattributed RX | {s['unattributed_rx']} |",
    ]
    if ctx.get("lo_before") and ctx.get("lo_after"):
        lo_tx = ctx["lo_after"]["tx_bytes"] - ctx["lo_before"]["tx_bytes"]
        lo_rx = ctx["lo_after"]["rx_bytes"] - ctx["lo_before"]["rx_bytes"]
        lines += [
            f"| `lo` TX delta (IP/link, both directions on lo) | {lo_tx} |",
            f"| `lo` RX delta (IP/link, both directions on lo) | {lo_rx} |",
        ]
    lines += [
        "",
        "| Ratio | Value |",
        "| --- | ---: |",
        f"| Attributed TX / payload sent | {pct(int(s['attributed_tx']), int(ctx['sent']))}% |",
        f"| Attributed RX / payload received | {pct(int(s['attributed_rx']), int(ctx['received']))}% |",
        "",
        "## Per-process rows (TGID + event-time start_ns + UID)", "",
        "| TGID | UID | TX | RX | comm | exe | start_ns | cgroup | identity verified |",
        "| ---: | ---: | ---: | ---: | --- | --- | ---: | --- | --- |",
    ]
    for p in s["processes"]:
        lines.append(
            f"| {p['tgid']} | {p['uid']} | {p['tx']} | {p['rx']} | {p['comm']} | {p['exe']} | "
            f"{p['start_ns']} | {p['cgroup_id']} | {'yes' if p['identity_verified'] else 'no'} |"
        )
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
        f"- Attributed TX within ±5% of payload: {ctx['check_tx']}",
        f"- Attributed RX within ±5% of payload: {ctx['check_rx']}",
        f"- No unattributed bytes in a fully controlled single-process transfer: {ctx['check_unattributed']}",
        "",
        "## Interpretation and caveats",
        "",
        "- eBPF counts TCP payload bytes at `tcp_sendmsg`/`tcp_recvmsg` return; `lo` counters count",
        "  IP/link bytes including headers. Ratios near 100% are expected but exact equality is not.",
        "- Loopback traffic appears once in each interface direction; this run does not exercise",
        "  physical-interface, UDP/QUIC, namespace, VPN, or container paths.",
        "- The load generator runs in a single process, so its TX and RX rows are both expected.",
        "- This is a controlled sanity check, not a claim of complete attribution coverage.",
        "",
    ]
    path.write_text("\n".join(lines), encoding="utf-8")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--size-mib", type=float, default=16.0)
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
        sent, received, transfer_s = run_transfer(size_bytes)
        # Allow at least two more snapshots so all deltas are emitted.
        time.sleep(max(1.0, args.interval * 2.5))
    finally:
        cpu_after = loader.cpu_seconds()
        rss_kb = loader.peak_rss_kb()
        loader.stop()

    elapsed = time.monotonic() - started
    lo_after = read_net_dev("lo")
    summary = summarize(loader.entries, loader.unattributed)
    cpu_s = round(cpu_after - cpu_before, 3) if cpu_before is not None and cpu_after is not None else None
    cpu_pct = round(cpu_s * 100.0 / elapsed, 3) if cpu_s is not None and elapsed > 0 else None

    attributed_tx = int(summary["attributed_tx"])
    attributed_rx = int(summary["attributed_rx"])
    check_tx = abs(attributed_tx - sent) <= sent * 0.05
    check_rx = abs(attributed_rx - received) <= received * 0.05
    check_unattributed = int(summary["unattributed_tx"]) == 0 and int(summary["unattributed_rx"]) == 0

    ctx = {
        "date": datetime.now(timezone.utc).isoformat(timespec="seconds"),
        "kernel": os.uname().release,
        "loader": str(args.loader),
        "interval": args.interval,
        "size_bytes": size_bytes,
        "size_mib": args.size_mib,
        "sent": sent,
        "received": received,
        "transfer_s": round(transfer_s, 3),
        "elapsed_s": elapsed,
        "lo_before": lo_before,
        "lo_after": lo_after,
        "summary": summary,
        "cpu_s": cpu_s,
        "cpu_pct": cpu_pct,
        "rss_kb": rss_kb,
        "check_tx": check_tx,
        "check_rx": check_rx,
        "check_unattributed": check_unattributed,
    }

    write_report(report_path, ctx)
    payload = {**ctx, "report": str(report_path)}
    print(json.dumps(payload, indent=2))
    if not args.json:
        print(f"\nreport: {report_path}", file=sys.stderr)
    return 0 if (check_tx and check_rx) else 1


if __name__ == "__main__":
    raise SystemExit(main())
