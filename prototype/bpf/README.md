# TCP+UDP per-interface attribution prototype (BPF)

This prototype counts payload bytes returned by kernel transport send/recv
functions:

- `fexit/tcp_sendmsg` → TCP upload/TX bytes (`ret > 0`)
- `kretprobe/tcp_recvmsg` → TCP download/RX bytes (`ret > 0`)
- `fexit/udp_sendmsg` → UDP upload/TX bytes (`ret > 0`)
- `kprobe/kretprobe udp_recvmsg` → UDP download/RX bytes (`ret > 0`)

QUIC is counted as UDP payload; QUIC framing is not identified. Counters are
aggregated in a BPF hash map and printed periodically by a userspace loader.
The file/type names retain the historical `flow_tcp_bytes`/`tcp` spelling.

## Key and interface semantics

- **Key:** `{start_ns, tgid, uid, ifindex, proto}`. `start_ns` is the
  thread-group leader's `task_struct.start_time` (nanoseconds since boot, read
  with CO-RE), captured when the byte event is processed. Using the leader (not
  the current thread) keeps all threads of one process in one row. PID reuse
  therefore creates a distinct counter row instead of merging two process
  generations.
- **Protocol:** `proto` is `1` for TCP and `2` for UDP, so per-protocol
  counters remain separate in the map.
- **Interface:** `ifindex` is resolved from the socket at hook time in this
  order: `skc_bound_dev_if`, `sk_rx_dst_ifindex`, `sk_dst_cache->dev->ifindex`.
  `0` means the interface could not be resolved; the loader reports it as `-`
  and Flow stores it without an interface rather than guessing. Unconnected or
  uncached UDP sockets may fall into this bucket.
- **Value:** cumulative TX/RX bytes, plus the first-observed `cgroup_id` and
  task `comm`. These are captured in the kernel so short-lived processes keep
  attribution after they exit.
- **Userspace enrichment:** the loader reads `/proc/<tgid>/{comm,exe,stat}`
  only when the live PID's start ticks match the event-time `start_ns`
  (tolerance ±2 ticks). A reused PID never contributes its executable to
  historical counters. When verification fails, the kernel-captured `comm` is
  used and the executable is reported as `-`.
- **Limitation:** this is process-generation identity, not a cryptographic or
  namespace-qualified identity. The cgroup id is an opaque cgroup v2 id, and
  the loader does not yet map it to a cgroup path or container name. There is
  no map expiry: rows for dead generations remain until the loader detaches.

## Measurement semantics (important)

- **What is counted:** bytes returned by the TCP/UDP send/recv functions at
  hook time (transport payload observed by these functions), cumulative while
  attached.
- **Not counted:** ICMP and non-TCP/UDP kernel paths; traffic before attach or
  after detach.
- **Attribution scope:** current task TGID/UID plus event-time start time;
  identity captured at first observation in the kernel, with guarded `/proc`
  enrichment.
- **Unattributed (prototype):** when `tgid==0` in hook context, bytes/events are
  counted in an explicit unattributed map/counter instead of being dropped
  silently.
- **Per-interface:** resolved from socket state as described above. This is
  not the same as interface counters: tunnels/VPNs and virtual interfaces can
  make the socket's device differ from the device that ultimately carries the
  packets, and that behavior is unmeasured.
- **This is a prototype, not final Flow attribution.** Context/ownership
  nuances, namespaces, tunnels/VPN stacks, and accuracy still require
  validation.

## Build artifacts

- `flow_tcp_bytes.bpf.c` — BPF program source
- `flow_tcp_bytes.bpf.o` — compiled BPF object
- `flow_tcp_bytes.skel.h` — generated libbpf skeleton (`bpftool gen skeleton`)
- `flow_tcp_bytes_loader.c` / `flow_tcp_bytes_loader` — userspace loader/monitor

Build artifacts are not committed. Rebuild them locally:

```bash
clang -O2 -g -target bpf -D__TARGET_ARCH_x86 -I prototype/bpf \
  -c prototype/bpf/flow_tcp_bytes.bpf.c -o prototype/bpf/flow_tcp_bytes.bpf.o
bpftool gen skeleton prototype/bpf/flow_tcp_bytes.bpf.o > prototype/bpf/flow_tcp_bytes.skel.h
cc -O2 -g -Wall -Wextra -I prototype/bpf \
  -o prototype/bpf/flow_tcp_bytes_loader prototype/bpf/flow_tcp_bytes_loader.c -lbpf -lelf -lz
```

## Run (requires sufficient BPF privileges)

On systems with `unprivileged_bpf_disabled=2`, loading/attaching typically
requires elevated privileges (root or appropriate capabilities depending on
policy/kernel config):

```bash
sudo ./prototype/bpf/flow_tcp_bytes_loader 2 15
```

Args: `interval_seconds rounds` (defaults: `2 10`). Add `tsv` as the third
argument for machine-readable output.

## TSV protocol

```
M  <clk_tck>  <boot_unix_ms>
S  <snapshot_index>
E  <tgid> <uid> <tx_total> <rx_total> <tx_delta> <rx_delta> <comm> <exe> <startticks> <identity_verified> <start_ns> <cgroup_id> <proto> <ifname>
U  <tx_total> <rx_total> <tx_delta> <rx_delta> <tx_events> <rx_events>
```

- `identity_verified=1` means `/proc` enrichment matched the event-time start
  time; `0` means the process exited or was reused, and `comm` is the
  kernel-captured fallback.
- `proto` is `1` (TCP) or `2` (UDP). `ifname` is the resolved interface or
  `-` when `ifindex==0`.
- `start_ns`/`cgroup_id` are appended after event-time identity;
  `proto`/`ifname` are appended after UDP/interface support. Consumers must
  accept 11-field and 13-field `E` lines from older loaders.
- Fields are tab-separated; tabs/newlines inside `comm`/`exe`/`ifname` are
  sanitized to spaces.

## Controlled validation (privileged)

`scripts/validate-ebpf-attribution.py` runs known-size loopback TCP and UDP
transfers while the loader samples, then compares:

- payload sent/received in userspace, per protocol,
- expected-process attributed TX/RX from the loader (summed deltas, per
  protocol),
- expected-process interface attribution (must be `lo` for this run),
- `lo` RX/TX deltas from `/proc/net/dev`,
- unattributed and background traffic, loader CPU time, and peak RSS.

It also asserts that the expected TGID maps to exactly one process generation
(no thread/start-time split) and that its identity was verified against
`/proc`.

```bash
sudo python3 scripts/validate-ebpf-attribution.py --size-mib 16
```

`--size-mib` is the payload per protocol (so the default run sends 16 MiB over
TCP then 16 MiB over UDP). The script writes a Markdown report under
`docs/measurements/` and prints a JSON summary. It must run as root because it
loads BPF programs. It is a controlled sanity check, not proof of complete
attribution: it does not exercise physical interfaces, namespaces, containers,
VPNs/tunnels, or virtual-interface double-counting.

Accepted TCP-loopback result (2026-11-18, kernel `7.0.0-43-generic`, TCP-only
harness before the UDP/per-interface change): one verified process generation
for the expected TGID, exactly 16,777,216 TX/RX for a 16 MiB payload,
background traffic separated, 0 unattributed bytes, and 0.004 s loader CPU /
3,672 KiB peak RSS. See `docs/measurements/ebpf-controlled-20261118T044634Z.md`.
The CPU/RSS sample is short-run only; do not treat it as steady-state overhead
evidence. The TCP+UDP/per-interface build has compiled but has **not** been
privileged-run yet.
