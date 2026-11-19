# Flow attribution model (initial)

> Status: **Phase 0 baseline / planning**, with event-time process identity implemented (ADR 0005), network-namespace identity validated in a two-netns collision run (ADR 0007), and accepted controlled TCP, TCP+UDP, per-interface loopback, two-interface veth, 60 s/300 s soak, high-churn map-growth, and physical-NIC TCP+UDP evidence (ADR 0006, `docs/measurements/`). A real VPN daemon/physical NIC and identity verification under sustained multithreaded load are still outstanding. The hard map cap is now measured and shows silent under-attribution (ADR 0008); a fix design is required. a GRE tunnel above veth is accepted for overlay behavior. Wildcard multi-interface attribution is accepted for UDP, and the map has been driven to 10,107/10,240 entries without byte loss, but the hard cap itself remains untested; do not treat the gate as closed.

## 1. Why attribution is non-trivial

- Linux **interface counters** report bytes per interface, not per process/application.
- **Netlink** helps discover interfaces/routes/sockets metadata but is not, by itself, a complete per-process byte accounting source.
- Byte counts differ by **observation point** (application payload, TCP/UDP payload, IP packets, link-layer frames). Totals from different layers/attach points are not directly interchangeable.

## 2. Identity model (required)

A PID is not stable (PIDs are reused). Flow must represent process identity with a stable composite, at minimum:

- PID
- Process start time or another reliable process-generation identifier
- Executable path when available
- UID
- Relevant cgroup/container metadata when available

Where useful and supported, investigate **socket cookies** or equivalent identifiers to correlate socket events with process/task identity. Application grouping must only merge processes when supported by reliable metadata (XDG desktop entries/IDs, executable paths, Snap/Flatpak metadata, icons) and must keep unknown/system processes inspectable.

### Implemented identity behavior (development increment, ADR 0005)

- The eBPF provider keys counters by `{start_ns, tgid, uid}` where `start_ns` is
  the **thread-group leader's** `task_struct.start_time` captured at event time.
  PID reuse creates a separate counter row/process generation instead of merging
  bytes, while all threads of one process stay in one row.
- `start_ns`, `cgroup_id`, and task `comm` are captured on first observation in
  the kernel, so short-lived processes retain event-time identity after exit.
- `ProcessIdentity.StartTimeUtc` prefers `boot_unix_ms + start_ns`, then
  `startticks / clk_tck`, then the current time (degraded, documented in the
  event's attribution note).
- `ProcessIdentity.StableKey` is `pid:startUnixMs:uid:netns` (ADR 0007; missing netns renders as `-`). Executable path is
  metadata, not key material, so later enrichment cannot split one process
  generation.
- The loader enriches `exe` from `/proc` only when the live PID's start ticks
  match the event-time start time (±2 ticks). Otherwise the executable remains
  unknown and the kernel-captured `comm` is used; the event note says identity
  was not verified.

## 3. Measurement semantics (to be documented precisely)

Flow must explicitly define and persist/report:

- **What is counted:** bytes observed at the chosen provider hook/attach point (exact definition TBD by prototype).
- **Direction:** upload vs download classification rules for that hook.
- **Scope:** which interfaces/namespaces/traffic classes are included or excluded.
- **Granularity:** per-process, per-application (aggregated from confidently associated processes), per-interface, and unattributed buckets.

The UI and exports must state the measurement method and caveats.

## 4. Attribution coverage and confidence

- Every accounted unit of traffic presented in totals must belong to either an **attributed** entity or an explicit **unattributed** bucket.
- Unknown attribution must be distinguishable from **zero usage**.
- Expose attribution coverage/confidence where meaningful (e.g. percentage/indicators, diagnostics) so users understand completeness.
- Do not claim perfect attribution when kernel/provider limitations prevent it.

## 5. Virtual interfaces, VPNs, and double-counting risks

- Traffic may traverse multiple logical interfaces (veth/bridge/tun/tap/WireGuard/etc.) and a physical interface.
- Flow must **avoid double-counting** the same bytes across layers/paths in a way that inflates user-visible totals.
- VPN and virtual-interface accounting can differ from physical-interface accounting; the product must explain this and allow interface-scoped views/filters.
- Aggregate comparisons against independent interface counters are for validation/diagnostics, not proof of per-app correctness by themselves.

## 6. Process lifecycle and edge cases

Prototype and product must handle (to supported extent):

- Short-lived processes and process exit during/after traffic
- PID reuse
- IPv4 and IPv6
- TCP and UDP
- Network namespaces and containerized workloads where observable
- Provider start/stop gaps and event loss (must be surfaced)

## 7. Prototype evidence (2026-11-13, kernel 7.0.0-43-generic)

### Validated (initial, privileged run)
- **Hooks:** `fexit/tcp_sendmsg` (TX) and `kretprobe/tcp_recvmsg` (RX) loaded and attached successfully via libbpf skeleton with `sudo` (`unprivileged_bpf_disabled=2`).
- **Observed attribution:** cumulative and per-interval delta per-(TGID, UID) TCP byte counters reported during live sampling runs.
  - Initial run: attributed traffic observed for TGIDs 64022/64042.
  - Enriched run (`2s x 20`): attributed traffic observed for multiple applications including `msedge`, `code` (Snap path), and `opencode`, with changing deltas over time.
- **Process-exit visibility issue:** TGIDs observed with counters but unresolved `/proc` enrichment (`COMM=?`, `EXE=-`) at snapshot time (likely exited/short-lived), demonstrating that enrichment must be captured at event/first-seen time and retained independently of process liveness.
- **Measurement point (current prototype):** bytes taken from positive return values of `tcp_sendmsg`/`tcp_recvmsg` (TCP) and `udp_sendmsg`/`udp_recvmsg` (UDP) kernel functions; UDP RX interface comes from the dequeued skb's `skb_iif` (`__skb_recv_udp`). Not equivalent to IP/link/interface counters.
- **Tooling note:** loader `startticks` parsing bug produced `0` in an earlier run; fixed and subsequently confirmed to report non-zero startticks in rerun.

### Recent prototype enhancements (source + unit tests; privileged rerun pending)
- **Explicit unattributed accounting:** BPF increments an unattributed counter/map for hook events where `tgid==0` (instead of silently ignoring), and loader reports unattributed totals/deltas/events.
- **Event-time identity / PID-reuse safety (2026-11-18, ADR 0005):** BPF map key is now `{start_ns, tgid, uid}`; the value carries first-observed `cgroup_id` and `comm`. The loader verifies live `/proc` identity against event-time start ticks before using `exe`, caches enrichment per process generation, and emits an extended TSV `E` line with `start_ns`/`cgroup_id`. Counter rows for reused PIDs are now distinct.
- **TCP+UDP and per-interface keys (2026-11-18, ADR 0006):** the key is now `{start_ns, tgid, uid, ifindex, proto}`. `proto` is `1`=TCP, `2`=UDP; QUIC is counted as UDP payload (framing not identified). UDP send uses `fexit/udp_sendmsg`; TCP/UDP receive use entry kprobes that stash `sk` by `pid_tgid` for the return probes. `ifindex` is resolved from `skc_bound_dev_if`, then `sk_rx_dst_ifindex`, then `sk_dst_cache->dev->ifindex`; `0` means unknown and is reported as an unresolved-interface bucket, not guessed.
- **Consumers and persisted identity:** `Flow.Linux.Monitoring.EbpfSnapshotParser` reads the extended line (still accepting 11-field, 13-field, 15-field, and 16-field lines), prefers event-time `start_ns` for `StartTimeUtc`, populates `ProcessIdentity.CgroupId` and `NetworkNamespaceId` from the kernel event, sets `NetworkUsageEvent.InterfaceName` from the resolved interface (or `null` when unresolved), records `ifindex@netns` for interfaces in other network namespaces, and records explicit attribution notes for unverified identity and unresolved interfaces. `ProcessIdentity.StableKey` is `pid:startUnixMs:uid:netns` so enrichment does not split a generation and distinct netns do not merge; historical 3-part keys are not rewritten.
- **Machine-readable loader mode:** `prototype/bpf/flow_tcp_bytes_loader` supports `tsv` output (`M` meta with clk_tck/boot time, `S` snapshots, `E` per-row deltas with identity/proto/interface fields, `U` unattributed deltas/events) to enable integration.
- **Flow.Linux provider integration:** `Flow.Linux.Monitoring.EbpfMonitor` (renamed from `EbpfTcpMonitor`; loader/file names are historical) launches the privileged prototype loader in TSV mode and converts snapshot deltas into `NetworkUsageEvent`s with:
  - **Measurement semantics:** provider `ebpf` / `fexit/tcp_sendmsg+udp_sendmsg, kretprobe/tcp_recvmsg+udp_recvmsg`; bytes from positive return values of TCP/UDP send/recv functions (payload at the hook point). QUIC appears as UDP. ICMP and other non-TCP/UDP paths are not counted. `ebpf-tcp` remains accepted only as a configuration alias.
  - **Process identity:** `ProcessIdentity` built from TGID, UID, event-time start time, executable path when live-enrichment was verified, and kernel cgroup id when available.
  - **Attribution:** attributed per-process/TGID events for `E` deltas; explicit unattributed events for `U` deltas (`tgid==0` hook context). Interface is persisted when it could be resolved.
  - **Limitations:** requires privileged loader/BPF attach, subprocess-based integration (not in-process libbpf), snapshot-delta granularity, no map expiry for dead generations, cgroup id is opaque (not a path/container name), persisted minute buckets do **not** separate TCP from UDP (they aggregate both for the same process/interface), and UDP/interface coverage/accuracy/overhead still require privileged validation.

### Unit-test evidence for this increment (non-privileged)
- `dotnet test Flow.slnx` → **40/40 passed** (Domain 1, Application 4, Infrastructure 9, Linux 21, Daemon 1, System 4).
- Parser tests cover: extended identity fields and cgroup propagation, PID reuse producing distinct stable keys, stable key ignoring mutable exe enrichment, legacy 11-field loader compatibility, 13-field event-time compatibility, protocol/interface propagation, namespace-scoped `ifindex@netns` notes, unresolved-interface notes, exited-process comm fallback + attribution notes, malformed/zero-delta handling, unattributed events, metadata defaults, and loader-failure stderr surfacing.
- BPF program, skeleton, and loader were **compiled** with clang/bpftool/libbpf (`0` errors; only pre-existing vmlinux.h warnings). They were **not loaded/attached** in this increment because that requires root/CAP_BPF and no non-interactive privilege path is available here.

### Controlled validation evidence (2026-11-18)
- `scripts/validate-ebpf-attribution.py` attaches the loader, runs a known-size loopback TCP transfer then a known-size loopback UDP transfer in the same process, and compares the expected process's attributed TX/RX per protocol against userspace payload counts. It also verifies that expected-process rows resolve to `lo`, records background/unattributed bytes, loader CPU time, and peak RSS, and writes a Markdown report under `docs/measurements/`. Run as root: `sudo python3 scripts/validate-ebpf-attribution.py --size-mib 16` (`--size-mib` is the payload per protocol). An accepted `--mode veth` creates a temporary veth pair split across two network namespaces (server in root on `flowvA`, client helper in `flowvns` on `flowvB`) and requires TX/RX to resolve to two distinct interfaces; see the accepted report below. Cross-namespace interfaces are reported as `ifindex@netns` because `ifindex` is namespace-scoped. The accepted `--mode nic` sends TCP and UDP through the default-route physical interface to peer echo listeners and asserts that interface attribution; see the accepted reports below.
- **Reports before `...T050933Z.md` predate the UDP/per-interface harness** and are TCP-only evidence; they do not contain interface/protocol checks.
- **Rejected first TCP run** (`ebpf-controlled-20261118T042842Z.md`): 16 MiB payload measured as 16,777,216 TX/RX for the expected process with 0 unattributed bytes, but the same Python TGID appeared as two rows with different `start_ns` (main thread and server thread) and the server-thread row failed `/proc` verification. Cause: `task_struct.start_time` is per-thread. The BPF program now reads `group_leader->start_time`; the harness now asserts one generation and verified identity for the expected TGID.
- **Accepted TCP rerun** (`ebpf-controlled-20261118T044634Z.md`, after the fix): expected TGID 47748 produced **one row** with `identity verified: yes`, executable `/usr/bin/python3.14`, cgroup `24677`, and attributed TX/RX of exactly **16,777,216** each (100.0% of payload). Background traffic was separated: VS Code (`code`, TGID 44356) contributed 2,777 TX / 10,108 RX and was also identity-verified. Unattributed TX/RX were 0. `lo` deltas were 16,796,684 per direction (IP/link headers and ACKs above payload). Loader CPU was 0.004 s (0.097% of one core) with peak RSS 3,672 KiB.
- **TCP+UDP run, byte accuracy accepted / interface gate failed** (`ebpf-controlled-20261118T050933Z.md`): TCP and UDP each produced exactly **16,777,216 TX/RX** (100.0% of payload), one verified process generation, no background/unattributed bytes, and 100.0% UDP delivery. TCP rows and UDP TX resolved to `lo`; **UDP RX resolved to `ifindex=0`** for all 16,777,216 bytes. Loader CPU was 0.005 s (0.076% of one core) with peak RSS 3,900 KiB.
- **Response to the interface failure:** UDP RX now correlates the dequeued skb's `skb_iif` via `kretprobe/__skb_recv_udp` instead of relying only on socket route state. This is per-datagram and avoids ambiguity for wildcard-bound sockets.
- **Accepted rerun after the fix** (`ebpf-controlled-20261118T084900Z.md`): TCP and UDP each again matched exactly **16,777,216 TX/RX**; all four protocol/direction rows for the expected TGID resolved to `lo`; 0 unresolved-interface bytes; one verified process generation; 0 unattributed bytes. Background `opencode` traffic (18 TX / 18 RX) was separated and identity-verified. Loader CPU 0.005 s (0.075% of one core) with peak RSS 3,880 KiB. All harness checks passed, including `check_interface`.
- **Accepted two-interface run after the netns veth probe** (`ebpf-controlled-20261118T101857Z.md`): TCP and UDP each again matched exactly **16,777,216 TX/RX**; the root server's RX rows resolved to `flowvA` and the client helpers' TX rows to `22@4026532907` (flowvB's ifindex in `flowvns`). Expected TGIDs 59914, 59958, 59959 each had one verified generation, expected unresolved bytes were 0, and all checks passed. Background UDP from unrelated processes had unresolved-interface rows, which are reported separately. Loader CPU 0.007 s (0.096% of one core), peak RSS 3,900 KiB (short-run only).
- **Accepted 60 s soak** (`ebpf-controlled-20261118T115504Z.md`): 70 loopback iterations × 1 MiB per protocol produced exactly **73,400,320 TX/RX** per protocol for the parent TGID, one verified generation, all rows on `lo`, 0 unresolved/unattributed bytes; all checks passed. 14 short-lived child transfers created distinct process-generation background rows (some without `/proc` enrichment after exit). Loader CPU rose from 0.038 s to 0.152 s cumulatively (~0.2% of one core) and peak RSS stayed at 3,916 KiB. BPF map entries grew 6→34 with no expiry; this is an observation, not a bounded-memory claim. This is the first sustained datapoint, not production-load evidence.
- **Accepted 300 s soak** (`ebpf-controlled-20261118T121017Z.md`): 348 loopback iterations × 1 MiB per protocol produced exactly **364,904,448 TX/RX** per protocol for the parent TGID, one verified generation, all rows on `lo`, 0 unresolved/unattributed bytes; all checks passed. 69 short-lived child transfers created distinct background process generations, some with `netns=0`, `exe=-`, or unverified identity after exit. Loader CPU reached **1.879 s / 0.616% of one core** on average while peak RSS stayed constant at **3,960 KiB**. BPF map entries grew **10 → 161** with no expiry; this is a longer observation, not a bounded-memory or capacity claim. Higher churn and production-like workloads remain unmeasured.
- **Accepted physical-NIC TCP run** (`ebpf-controlled-20261118T131724Z.md`): a 16 MiB TCP echo through `wlp0s20f3` to an Android/Termux hotspot peer produced exactly **16,777,216 TX/RX** for the expected process, its row resolved to `wlp0s20f3` in the root netns, one verified generation, 0 unresolved/unattributed bytes. The peer listener independently reported **16,777,216 bytes**, matching the supplied `--peer-bytes`. Wi-Fi/link counters were higher due to overhead.
- **Accepted physical-NIC TCP+UDP run** (`ebpf-controlled-20261118T144214Z.md`): the extended probe produced exactly **16,777,216 TX/RX per protocol** for TCP and UDP, both expected rows on `wlp0s20f3` in the root netns, one verified generation, 0 unresolved/unattributed bytes, and all checks true. Peer byte counts were supplied via `--peer-bytes`/`--peer-udp-bytes` and matched payloads; the Termux count lines were not archived for this run. This accepts hardware-interface attribution for both TCP and UDP on this Wi-Fi path. VPN/tunnel and multiple simultaneous physical interfaces remain unexercised here; wildcard RX on two virtual interfaces is accepted in a separate run below.
- **Accepted two-netns identity collision** (`ebpf-controlled-20261118T190019Z.md`): one TGID/start/uid moved between two temporary netns and produced two distinct rows with netns `4026532651` and `4026532843`, each with exactly **1,048,576 TX/RX**. `check_netns_collision: True`; 0 unattributed. Raw identity/interface checks were harness expectation mismatches (short-lived emitting threads; `1@netns` naming is correct) and were fixed in the harness, not in the provider.
- **Accepted high-churn observation** (`ebpf-controlled-20261118T193832Z.md`): 3,840 short-lived TCP+UDP children in 180 s grew map entries from 130 to 7,653 (of 10,240) without byte loss (`check_churn_bytes: True`, 0 unattributed, exact 15,728,640 per protocol/direction). The 9,000 target was not reached; CPU averaged 7.815% of one core and peak RSS rose 3,940 → 6,380 KiB with retained generations. This is high-occupancy evidence, not a capacity-limit or bounded-memory claim.
- **Accepted wildcard multi-interface run** (`ebpf-controlled-20261118T225010Z.md`): one wildcard-bound UDP server produced two RX rows with identical process identity on `lo` and `flowvA`, each exactly 1,048,576 bytes; client TX rows exact; `check_wildcard: True`, 0 unresolved/unattributed.
- **Accepted hard-capacity approach** (`ebpf-controlled-20261118T225153Z.md`): 5,056 short-lived children grew map entries 128→10,107 of 10,240 with exact bytes, `check_churn_bytes: True`, 0 unattributed, map target reached. CPU 10.142%, RSS 3,788→7,224 KiB. Hard cap behavior and bounded memory remain unproven.
- **Accepted GRE tunnel run** (`ebpf-controlled-20261118T234935Z.md`): a 16 MiB TCP echo over GRE above veth attributed the client to `flowtun0` in the root netns and the server to `6@4026532906` in the peer netns, each exactly 16,777,216 TX/RX, with no expected rows on the underlay and 0 unattributed. Overlay and underlay counters both grew by ~16.8 MB, documenting double-counting; only the tunnel device is attributed to processes. TCP-only, no VPN daemon.
- **Accepted production-load proxy** (`ebpf-controlled-20261119T002316Z.md`): 8 concurrent workers × 300 s, 2,850 transfers, exact 2,988,441,600 bytes per protocol/direction, all expected rows on `lo`, 0 unattributed; loader CPU 0.144% of one core, constant RSS 3,944 KiB, map 4→14 entries. Caveat: `check_identity: False` for the expected row under sustained multithreaded load; kernel-captured identity was retained but `/proc` enrichment did not verify it.
- **Hard-cap limitation** (`ebpf-controlled-20261119T003701Z.md`): driving the map to 10,231/10,240 entries produced a 7,168-byte shortfall per protocol/direction (7 children) that was neither attributed nor unattributed. This is accepted as evidence of a correctness limitation at saturation, not as a clean pass; see ADR 0008.
- **Overflow visibility validated, capacity still limited** (`ebpf-controlled-20261119T005948Z.md`): after raising the map to 32,768 and adding an explicit overflow counter, a 16,448-child churn run reported 154,624 bytes/151 events of map-full overflow. Exact shortfall was 156,672 TX / 154,624 RX at 32,577 entries — loss is visible but not eliminated. ADR 0008 remains open for a larger map or eviction.
- **What the accepted runs prove:** on this kernel, TCP and UDP transport payload accounting matches controlled loopback and veth payloads at the process level, the process-generation key survives multithreaded traffic, the live-identity guard works, the explicit unattributed bucket stayed empty, and per-interface attribution distinguishes TX and RX across two interfaces in the controlled veth case (including namespace-scoped interfaces reported as `ifindex@netns`).
- **What they do not prove:** namespaces/containers as a fully validated attribution dimension (the collision key is validated, but real container workloads are not), real VPN daemons/physical NICs, all virtual-interface double-counting variants, correctness for wildcard-bound sockets receiving on multiple interfaces, multiple simultaneous interfaces in one process, the hard map-capacity limit, or accuracy for high-connection-count workloads. The two-netns run accepted the netns key fix but did not exercise real container lifecycle or `/proc` enrichment for short-lived server threads. Churn runs reached 10,231/10,240 entries; the last run showed a small but systematic silent shortfall, so hard-cap behavior is now measured but unfixed (ADR 0008). Some background UDP traffic had unresolved interfaces, so interface resolution is not universal.

### Not yet satisfied (gate still open)
- **Scope of accuracy evidence:** accepted evidence includes a 16 MiB loopback TCP run, TCP+UDP loopback runs, a two-interface veth run across two network namespaces, 60 s and 300 s soaks with short-lived process churn, and physical-NIC TCP+UDP echoes through `wlp0s20f3`. Production-like high-churn/high-connection workloads remain unmeasured.
- **Multi-interface semantics:** the accepted veth run proves TX/RX separation across two virtual interfaces in two namespaces at observation time; the physical-NIC runs prove TCP and UDP attribution on one hardware interface. The BPF key has no netns dimension, so identical keys in distinct namespaces would still merge. Wildcard-bound sockets receiving on multiple interfaces, VPN/tunnel paths, and multiple simultaneous physical interfaces are unmeasured. Background UDP rows with unresolved interfaces demonstrate that resolution is not universal.
- **Coverage:** ICMP and other non-TCP/UDP paths are not counted. QUIC bytes are counted as UDP payload but QUIC framing is not identified. TCP and UDP per-interface attribution is accepted for the controlled loopback case only; wildcard-bound sockets receiving on multiple interfaces and tunnel/VPN paths are unmeasured.
- **Persistence semantics:** minute buckets aggregate TCP and UDP for the same process/interface; protocol is available in the loader stream but not persisted as a separate dimension.
- **Namespaces/containers/VPNs:** cgroup id is captured but not mapped to a path/container; network namespaces and tunnel/VPN layers are not yet handled or measured.
- **Overhead/edge cases:** the 60 s and 300 s soaks are sustained datapoints (loader CPU ~0.2% and ~0.616% of one core; peak RSS constant 3,916/3,960 KiB); the high-churn run reached 7,653 map entries with CPU 7.815% and RSS growth to 6,380 KiB. Map growth has no expiry; the hard 10,240-entry capacity, short-lived process identity gaps, virtual-interface double-counting, and provider start/stop gaps are not measured.
- **Application grouping:** not implemented beyond exe/comm hints.


### Working hypothesis (subject to revision)
`fexit/tcp_sendmsg` + `kretprobe/tcp_recvmsg` remains the validated TCP core. The prototype now adds `fexit/udp_sendmsg`, UDP receive with per-datagram `skb_iif` interface correlation, and per-interface keys; a controlled loopback run validates TCP+UDP byte accuracy and per-interface attribution for that case. Broader interface, namespace, and load behavior still needs evidence.

Updates to this document must track prototype results and recorded decisions in `docs/decisions/`.
