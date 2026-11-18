# ADR 0006: TCP+UDP coverage and per-interface attribution keys

- Status: Accepted (implementation), validation pending
- Date: 2026-11-18

## Context

The TCP-only prototype and the accepted controlled run
(`docs/measurements/ebpf-controlled-20261118T044634Z.md`) left two explicit
coverage gaps:

1. **UDP/QUIC was not counted.** Many desktop applications (including browsers
   and voice/video calls) move significant traffic over UDP. QUIC runs over
   UDP, so a TCP-only attribution provider silently misses it.
2. **Events had no interface attribution.** The TCP send/recv hooks do not
   directly expose a bound interface, so persisted `usage_buckets` could not
   answer which interface attributed bytes used. `procfs` reports interfaces
   but without process attribution.

The gate requires coverage "where feasible" with honest measurement, not
claims that are not observed.

## Decision

1. **Count TCP and UDP payload at the transport hooks.**
   - TCP TX: `fexit/tcp_sendmsg` (`ret > 0`).
   - TCP RX: `kprobe`/`kretprobe` on `tcp_recvmsg`; the entry probe stashes
     `struct sock *` keyed by `bpf_get_current_pid_tgid()` for the return probe.
   - UDP TX: `fexit/udp_sendmsg` (`ret > 0`).
   - UDP RX: `kprobe`/`kretprobe` on `udp_recvmsg` using the same stash pattern.
   - UDP RX interface: `kretprobe/__skb_recv_udp` captures the dequeued skb's
     `skb_iif` per `pid_tgid`; the `udp_recvmsg` return probe consumes it. This
     is per-datagram, so wildcard-bound sockets receiving on multiple
     interfaces are not reduced to one "last seen" interface.
   - QUIC is counted as UDP payload. QUIC framing is **not** identified.
   - ICMP and other non-TCP/UDP paths remain uncounted and are documented.
2. **Add `proto` and `ifindex` to the counter key.** The key becomes
   `{start_ns, tgid, uid, ifindex, proto}` with `proto` = `1` (TCP) or `2`
   (UDP). This keeps per-interface and per-protocol counters distinct in the
   kernel map.
3. **Resolve the interface from socket state, not from packet inspection.**
   Preference order: `skc_bound_dev_if`, then `sk_rx_dst_ifindex`, then
   `sk_dst_cache->dev->ifindex`. For UDP RX, prefer the received datagram's
   `skb_iif` captured via `__skb_recv_udp`, falling back to the socket state.
   `ifindex == 0` means the interface could not be resolved and is reported as
   unresolved (`-` in TSV / no interface in Flow), never guessed.
4. **Extend the TSV `E` line backward-compatibly:**
   `E  tgid  uid  tx_total  rx_total  tx_delta  rx_delta  comm  exe  startticks  verified  start_ns  cgroup_id  proto  ifname`.
   Consumers must keep accepting 11-field and 13-field lines.
5. **Expose interface attribution through existing persistence.**
   `Flow.Linux.Monitoring.EbpfSnapshotParser` sets
   `NetworkUsageEvent.InterfaceName` from `ifname`, and the existing ingestion
   pipeline stores it as `interface_id`. No schema migration is required for
   interface.
6. **Keep minute buckets aggregating TCP+UDP for the same process/interface.**
   The loader stream carries `proto`, but `usage_buckets` has no protocol
   dimension. Splitting protocols would require a schema/key migration and is
   deferred; this ADR records the aggregation explicitly rather than implying
   protocol-level history exists.
7. **Rename the provider to `ebpf` and the C# type to `EbpfMonitor`.** The old
   provider name `ebpf-tcp` is now inaccurate. `ebpf-tcp` remains a
   configuration alias for continuity. Prototype file names
   (`flow_tcp_bytes*`) are retained as historical artifacts.

## Consequences

- The provider now has source-level coverage for TCP and UDP, including QUIC
  bytes as UDP payload, and per-interface event attribution when the socket
  route is resolvable.
- **Controlled rerun 2026-11-18 (`docs/measurements/ebpf-controlled-20261118T084900Z.md`):**
  after the `skb_iif` change, TCP and UDP again accounted for exactly
  16,777,216 TX/RX bytes with one verified process generation and 0
  unattributed bytes, and all four protocol/direction rows resolved to `lo`.
  This accepts per-interface attribution for controlled loopback TCP and UDP,
  including the previously failing unconnected UDP RX case. It does not cover
  physical interfaces, namespaces, VPN/tunnel paths, wildcard sockets
  receiving on multiple interfaces, or sustained load.
- **Byte accuracy and the loopback interface fix are validated for one
  controlled run.** Broader per-interface behavior, protocols, and load
  conditions remain unproven (`docs/attribution.md`).
- Loader attachment is all-or-nothing: if any TCP/UDP TX/RX hook fails to
  attach, the loader exits instead of running with silent partial coverage.
- The first controlled run showed unconnected UDP RX resolving to `ifindex=0`
  via socket state alone (16,777,216 bytes). The per-datagram `skb_iif` path was
  added and validated for loopback UDP RX in the rerun. Other cases where an
  interface cannot be resolved are still counted without an interface and
  noted rather than guessed.
- The unattributed fallback (`tgid==0`) remains a single aggregate; it is not
  split by protocol or interface, so unresolved hook-context bytes cannot be
  attributed to TCP vs UDP.
- The interface resolved from the socket is not necessarily the device that
  ultimately carries encapsulated/VPN traffic. Tunnel and virtual-interface
  behavior remains an open measurement item.
- The provider/hook strings persisted with new rows are `ebpf` and the
  TCP+UDP hook list. Existing rows keep `ebpf-tcp`; no rewrite is performed, so
  a database can contain both provider labels for historical and new data.
- Persisted history cannot distinguish TCP from UDP. A later schema change can
  add a protocol dimension if protocol-level history becomes required.
- The validation harness now runs both TCP and UDP transfers and checks
  per-protocol totals and `lo` attribution in one pass:
  `sudo python3 scripts/validate-ebpf-attribution.py --size-mib 16`.
- This ADR does **not** close the attribution gate. Namespaces/containers,
  VPN/tunnel/virtual-interface behavior, physical interfaces, sustained
  overhead, and map expiry still require evidence (`docs/attribution.md`).
