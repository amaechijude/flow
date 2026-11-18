# TCP+UDP per-interface attribution prototype (BPF)

This prototype counts payload bytes returned by kernel transport send/recv
functions:

- `fexit/tcp_sendmsg` → TCP upload/TX bytes (`ret > 0`)
- `kretprobe/tcp_recvmsg` → TCP download/RX bytes (`ret > 0`)
- `fexit/udp_sendmsg` → UDP upload/TX bytes (`ret > 0`)
- `kprobe/kretprobe udp_recvmsg` → UDP download/RX bytes (`ret > 0`)
- `kretprobe/__skb_recv_udp` → per-datagram UDP RX interface (`skb_iif`)

QUIC is counted as UDP payload; QUIC framing is not identified. Counters are
aggregated in a BPF hash map and printed periodically by a userspace loader.
The file/type names retain the historical `flow_tcp_bytes`/`tcp` spelling.

## Key and interface semantics

- **Key:** `{start_ns, netns_id, tgid, uid, ifindex, proto}`. `start_ns` is the
  thread-group leader's `task_struct.start_time` (nanoseconds since boot, read
  with CO-RE), captured when the byte event is processed. Using the leader (not
  the current thread) keeps all threads of one process in one row. `netns_id`
  is the netns inode captured at event time (`nsproxy->net_ns->ns.inum`), so
  identical process generations in different netns stay distinct. PID reuse
  therefore creates a distinct counter row instead of merging two process
  generations.
- **Protocol:** `proto` is `1` for TCP and `2` for UDP, so per-protocol
  counters remain separate in the map.
- **Interface:** `ifindex` is resolved from the socket at hook time in this
  order: `skc_bound_dev_if`, `sk_rx_dst_ifindex`, `sk_dst_cache->dev->ifindex`.
  For UDP receive, the dequeued datagram's `skb_iif` (captured from
  `__skb_recv_udp`) is preferred, because unconnected UDP sockets do not
  reliably expose a cached route. `0` means the interface could not be resolved;
  the loader reports it as `-` and Flow stores it without an interface rather
  than guessing.
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
  silently. That fallback is a single aggregate and is not split by protocol or
  interface.
- **Per-interface:** resolved from socket state as described above. This is
  not the same as interface counters: tunnels/VPNs and virtual interfaces can
  make the socket's device differ from the device that ultimately carries the
  packets, and that behavior is unmeasured.
- **This is a prototype, not final Flow attribution.** Context/ownership
  nuances, namespaces, tunnels/VPN stacks, and accuracy still require
  validation.
- **Attach failures are fatal.** The loader refuses to run unless all TCP+UDP
  TX/RX hooks attach, so a partial attachment cannot silently back a
  full-coverage claim. Interface resolution failure is different: bytes are
  still counted with `ifindex 0` (reported as unresolved).

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
E  <tgid> <uid> <tx_total> <rx_total> <tx_delta> <rx_delta> <comm> <exe> <startticks> <identity_verified> <start_ns> <cgroup_id> <proto> <ifname> <netns_id>
U  <tx_total> <rx_total> <tx_delta> <rx_delta> <tx_events> <rx_events>
X  <snapshot_index> <map_entries> <unattributed_tx_total> <unattributed_rx_total>
```

- `identity_verified=1` means `/proc` enrichment matched the event-time start
  time; `0` means the process exited or was reused, and `comm` is the
  kernel-captured fallback.
- `proto` is `1` (TCP) or `2` (UDP). `ifname` is the resolved interface or
  `-` when `ifindex==0`.
- `netns_id` is the network namespace inode captured at event time in the
  kernel (not inferred from `/proc` at snapshot time). It participates in the
  BPF key and in `ProcessIdentity.StableKey`. When it differs from the loader's
  namespace, `ifname` is `ifindex@netns_id` because `ifindex` is namespace-scoped
  and cannot be named from the loader's namespace.
- `start_ns`/`cgroup_id` are appended after event-time identity;
  `proto`/`ifname` after UDP/interface support; `netns_id` last. Consumers must
  accept 11-field, 13-field, 15-field, and 16-field `E` lines from older loaders.
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
sudo python3 scripts/validate-ebpf-attribution.py --size-mib 16 --mode veth
sudo python3 scripts/validate-ebpf-attribution.py --mode soak --duration 300 --soak-size-mib 1
sudo python3 scripts/validate-ebpf-attribution.py --mode nic --peer-host <gateway-ip> --peer-port 9000 --peer-udp-port 9001 --interface wlp0s20f3 --peer-bytes 16777216 --peer-udp-bytes 16777216
sudo python3 scripts/validate-ebpf-attribution.py --mode netns-key --netns-size-mib 1
sudo python3 scripts/validate-ebpf-attribution.py --mode map-churn --churn-target 9000
```

`--size-mib` is the payload per protocol (so the default run sends 16 MiB over
TCP then 16 MiB over UDP). Accepted two-interface run
(`docs/measurements/ebpf-controlled-20261118T101857Z.md`): with `--mode veth`,
TCP and UDP again matched exactly 16,777,216 TX/RX. Root RX resolved to
`flowvA`; helper TX resolved to `22@4026532907` (flowvB's ifindex in `flowvns`).
All checks passed, one verified generation per TGID, expected unresolved bytes
0. Background UDP from unrelated processes still produced unresolved-interface
rows. This accepts controlled two-interface separation across two virtual
interfaces only; physical NICs, VPN/tunnel paths, wildcard multi-interface
sockets, and namespace-level key merging remain unmeasured.

`--mode soak` repeats `--soak-size-mib` transfers for `--duration` seconds and
spawns a short-lived child every `--soak-child-every` iterations. It samples
loader CPU/RSS and loader-emitted map-entry counts to provide steady-state
overhead and map-growth evidence. Byte-exactness checks cover only the parent
TGID; child churn and unresolved background rows are reported separately. The
BPF map has no expiry, so growth is an observation, not a claim of bounded
memory.

Accepted 60 s run (`docs/measurements/ebpf-controlled-20261118T115504Z.md`): 70
iterations × 1 MiB per protocol matched exactly, all checks passed, loader CPU
~0.2% of one core, peak RSS constant 3,916 KiB, map entries grew 6→34 with no
expiry. Child churn rows were background only; some exited before `/proc`
enrichment.

Accepted 300 s run (`docs/measurements/ebpf-controlled-20261118T121017Z.md`): 348
iterations × 1 MiB per protocol matched exactly, all checks passed, loader CPU
~0.616% of one core, peak RSS constant 3,960 KiB, map entries grew 10→161 with
no expiry, 69 child-churn background rows. These are sustained observations,
not capacity/high-churn/production-load evidence.

`--mode nic` sends known TCP and UDP payloads through the default-route physical
interface to peer echo listeners (e.g. Termux on an Android phone hotspot) and
requires the expected rows to resolve to that interface in the root netns.
Peer/interface are inferred from the default route or supplied with
`--peer-host`/`--interface`; TCP uses `--peer-port` (9000) and UDP uses
`--peer-udp-port` (9001). Pass peer-reported counts with `--peer-bytes` and
`--peer-udp-bytes` to include them as checks.

A combined Termux listener helper is in `scripts/termux_echo_listener.py`. Copy
it to the phone and run `python3 termux_echo_listener.py` with `termux-wake-lock`
so the screen/session stays alive. It prints `tcp <bytes>` and `udp <bytes>`.

Accepted physical-NIC runs are recorded at
`docs/measurements/ebpf-controlled-20261118T131724Z.md` (TCP, with archived
Termux peer output) and `docs/measurements/ebpf-controlled-20261118T144214Z.md`
(TCP+UDP, peer byte counts supplied via flags). Both matched exact payloads with
expected rows on `wlp0s20f3`; the TCP+UDP run extends hardware-interface
attribution to UDP. VPN/tunnel, multiple simultaneous interfaces, and wildcard
multi-interface sockets remain unmeasured. The script writes a Markdown report
under `docs/measurements/` and prints a JSON summary. It must run as root
because it loads BPF programs. It is a controlled sanity check, not proof of
complete attribution.

Accepted TCP-loopback result (2026-11-18, kernel `7.0.0-43-generic`, TCP-only
harness before the UDP/per-interface change): one verified process generation
for the expected TGID, exactly 16,777,216 TX/RX for a 16 MiB payload,
background traffic separated, 0 unattributed bytes, and 0.004 s loader CPU /
3,672 KiB peak RSS. See `docs/measurements/ebpf-controlled-20261118T044634Z.md`.
The CPU/RSS sample is short-run only; do not treat it as steady-state overhead
evidence.

TCP+UDP run without per-datagram RX correlation
(`docs/measurements/ebpf-controlled-20261118T050933Z.md`): TCP and UDP each
produced exactly 16,777,216 TX/RX with one verified generation and 0
unattributed bytes. TCP and UDP TX resolved to `lo`; UDP RX did not resolve an
interface via socket state.

Accepted rerun after the `__skb_recv_udp`/`skb_iif` fix
(`docs/measurements/ebpf-controlled-20261118T084900Z.md`): all four
protocol/direction rows for the expected process resolved to `lo`, all harness
checks passed, and byte totals again matched exactly. Background `opencode`
traffic (18 bytes each direction) was separated and identity-verified. This
accepts the loopback case only; physical interfaces, namespaces, containers,
VPN/tunnel and virtual-interface paths, and sustained overhead remain unmeasured.
