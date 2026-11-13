# Flow attribution model (initial)

> Status: **Phase 0 baseline / planning**. Measurement semantics and attribution quality must be finalized only after a working prototype on the target Ubuntu 26.04 kernel.

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
- **Measurement point (current prototype):** bytes taken from positive return values of `tcp_sendmsg` / `tcp_recvmsg` kernel functions (TCP-path bytes at those hooks). Not equivalent to IP/link/interface counters.
- **Tooling note:** loader `startticks` parsing bug produced `0` in an earlier run; fixed and subsequently confirmed to report non-zero startticks in rerun.

### Recent prototype enhancements (source updated)
- **Explicit unattributed accounting:** BPF now increments an unattributed counter/map for hook events where `tgid==0` (instead of silently ignoring), and loader reports unattributed totals/deltas/events.
- **Identity caching:** loader caches first-seen/alive `/proc` identity (comm/exe/startticks) per TGID+UID so exited processes can retain last-known context in snapshots (partial progress; full stable identity/PID-reuse-safe persistence still pending).
- **Machine-readable loader mode:** `prototype/bpf/flow_tcp_bytes_loader` supports `tsv` output (`M` meta with clk_tck/boot time, `S` snapshots, `E` per-TGID deltas with identity fields, `U` unattributed deltas/events) to enable integration.
- **Flow.Linux provider integration (initial):** `Flow.Linux.Monitoring.EbpfTcpMonitor` launches the privileged prototype loader in TSV mode and converts snapshot deltas into `NetworkUsageEvent`s with:
  - **Measurement semantics:** `ebpf-tcp` / `fexit/tcp_sendmsg + kretprobe/tcp_recvmsg`; bytes from positive return values of TCP send/recv functions (TCP-path bytes at hook point). UDP/non-TCP not counted. Interface-level per-event attribution not provided by this hook.
  - **Process identity:** `ProcessIdentity` built from TGID, UID, executable path (when available), and start time derived from boot time + startticks/clk_tck (fallback to current time if unavailable; reduces stability).
  - **Attribution:** attributed per-process/TGID events for `E` deltas; explicit unattributed events for `U` deltas (`tgid==0` hook context). Application hint derived from exe/comm when available.
  - **Limitations:** requires privileged loader/BPF attach, subprocess-based integration (not in-process libbpf), snapshot-delta granularity, no PID-reuse-proof persisted identity lifecycle yet, no per-interface mapping, and coverage/accuracy/overhead still subject to gate validation.

### Not yet satisfied (gate still open)
- **Stable identity:** prototype keys are `(tgid, uid)` only; no PID/start-time/generation correlation, no exe/cmdline/cgroup/namespace metadata persisted for attribution records.
- **PID reuse / process exit:** no lifecycle handling or historical identity binding demonstrated.
- **Unattributed traffic:** no explicit unattributed bucket/diagnostics in current prototype maps.
- **Coverage:** UDP/QUIC, ICMP, non-TCP paths, and traffic outside attach lifetime not counted.
- **Controlled comparison:** no documented side-by-side comparison against `/proc/net/dev`/`ip -s link` for the same controlled transfers in this recorded run.
- **Overhead/edge cases:** CPU/memory overhead, namespaces/containers, VPN/tunnels, virtual interfaces, short-lived processes, and double-counting risks not yet measured/documented.
- **Application grouping:** not implemented.

### Working hypothesis (subject to revision)
`fexit/tcp_sendmsg` + `kretprobe/tcp_recvmsg` is a viable **TCP-only** starting point for per-task byte observation on this kernel; Flow’s production provider must expand/replace this based on validated semantics for broader protocol coverage and robust attribution.

Updates to this document must track prototype results and recorded decisions in `docs/decisions/`.
