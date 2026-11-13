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

## 7. Current status and open questions

- **Validated prototype:** none yet in this repo (technical gate open).
- **Hook/byte semantics:** undecided pending evidence on kernel 7.0.0-43-generic.
- **Namespace/container attribution fidelity:** unknown until tested.
- **Application grouping confidence rules:** to be designed after reliable process-to-app mapping sources are evaluated.

Updates to this document must track prototype results and recorded decisions in `docs/decisions/`.
