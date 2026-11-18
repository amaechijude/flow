# TCP byte attribution prototype (BPF)

This prototype counts bytes returned by kernel TCP send/recv functions:

- `fexit/tcp_sendmsg` → upload/TX bytes (`ret > 0`)
- `kretprobe/tcp_recvmsg` → download/RX bytes (`ret > 0`)

Counters are aggregated in a BPF hash map and printed periodically by a userspace loader.

## Identity semantics (event-time)

- **Key:** `{start_ns, tgid, uid}`. `start_ns` is the thread-group leader's
  `task_struct.start_time` (nanoseconds since boot, read with CO-RE), captured
  when the byte event is processed. Using the leader (not the current thread)
  keeps all threads of one process in one row. PID reuse therefore creates a
  distinct counter row instead of merging two process generations.
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

- **What is counted:** bytes returned by `tcp_sendmsg` / `tcp_recvmsg` at hook
  time (TCP-path bytes observed by these functions), cumulative while attached.
- **Not counted:** UDP, QUIC-over-UDP, ICMP, non-TCP kernel paths, traffic
  before attach/after detach.
- **Attribution scope:** current task TGID/UID plus event-time start time;
  identity captured at first observation in the kernel, with guarded `/proc`
  enrichment.
- **Unattributed (prototype):** when `tgid==0` in hook context, bytes/events are
  counted in an explicit unattributed map/counter instead of being dropped
  silently.
- **Per-interface:** the send/recv hooks do not expose an interface binding.
  The event stream therefore has no interface attribution; interface totals
  must come from a separate provider (e.g. `/proc/net/dev`).
- **This is a prototype, not final Flow attribution.** Context/ownership
  nuances, namespaces, tunnels/VPN stacks, protocol coverage, and accuracy
  still require validation.

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
E  <tgid> <uid> <tx_total> <rx_total> <tx_delta> <rx_delta> <comm> <exe> <startticks> <identity_verified> <start_ns> <cgroup_id>
U  <tx_total> <rx_total> <tx_delta> <rx_delta> <tx_events> <rx_events>
```

- `identity_verified=1` means `/proc` enrichment matched the event-time start
  time; `0` means the process exited or was reused, and `comm` is the
  kernel-captured fallback.
- `start_ns`/`cgroup_id` are appended fields. Consumers must accept 11-field
  `E` lines from older loaders.
- Fields are tab-separated; tabs/newlines inside `comm`/`exe` are sanitized to
  spaces.

## Controlled validation (privileged)

`scripts/validate-ebpf-attribution.py` runs a known-size loopback TCP transfer
while the loader samples, then compares:

- payload sent/received in userspace,
- expected-process attributed TX/RX from the loader (summed deltas),
- `lo` RX/TX deltas from `/proc/net/dev`,
- unattributed and background traffic, loader CPU time, and peak RSS.

It also asserts that the expected TGID maps to exactly one process generation
(no thread/start-time split) and that its identity was verified against
`/proc`.

```bash
sudo python3 scripts/validate-ebpf-attribution.py --size-mib 16
```

The script writes a Markdown report under `docs/measurements/` and prints a
JSON summary. It must run as root because it loads BPF programs. It is a
controlled sanity check, not proof of complete attribution: it does not
exercise UDP, physical interfaces, namespaces, containers, VPNs/tunnels, or
virtual-interface double-counting.
