# eBPF TCP+UDP attribution controlled validation

> **Status: accepted wildcard multi-interface evidence (2026-11-18).** One
> wildcard-bound UDP server (TGID 56339) produced **two RX rows with the same
> process identity**: `lo` and `flowvA`, each with exactly **1,048,576 bytes**.
> The loopback client TX row on `lo` and the peer-netns client TX row on
> `8@4026532905` were also exact. `check_wildcard: True`, 0 unresolved/unattributed,
> all checks true. This accepts per-datagram interface attribution for a
> wildcard socket receiving on two interfaces.
>
> Scope: one physical/virtual pair in the root netns, UDP only; no VPN/tunnel,
> physical NIC, or multi-simultaneous-interface workload. Identity verification
> is n/a for the exited peer client; kernel-captured identity was retained.
> The payload line below was corrected from 16 MiB to the actual 1 MiB per source.

- Date (UTC): 2026-11-18T22:50:18+00:00
- Kernel: `7.0.0-43-generic`
- Loader: `/home/amaechi/code/flow/prototype/bpf/flow_tcp_bytes_loader` (interval 1.0 s)
- Payload per source: 1048576 bytes (1.000 MiB), wildcard UDP listener on two interfaces
- Transfers: wildcard UDP server receiving from loopback and veth clients
- Expected process TGID(s): 56339, 56385
- Loader wall time: 7.73 s

## Results by protocol

| Protocol | Payload sent | Payload received | Attributed TX | Attributed RX | TX / sent | RX / received |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| TCP (`tcp_sendmsg`/`tcp_recvmsg`) | 0 | 0 | 0 | 0 | None% | None% |
| UDP (`udp_sendmsg`/`udp_recvmsg`) | 2097152 | 2097152 | 2097152 | 2097152 | 100.0% | 100.0% |

- UDP delivered / UDP sent: 100.0% (drops are possible; eBPF RX is compared to delivered bytes).

| Aggregate | Bytes |
| --- | ---: |
| Total attributed TX, expected process | 2097152 |
| Total attributed RX, expected process | 2097152 |
| Other attributed TX (background traffic) | 3383 |
| Other attributed RX (background traffic) | 1779 |
| Total attributed TX | 2100535 |
| Total attributed RX | 2098931 |
| Unattributed TX | 0 |
| Unattributed RX | 0 |

## Per-process rows (TGID + event-time start_ns + UID + proto + interface)

| TGID | UID | Proto | Interface | Netns | TX | RX | comm | exe | start_ns | cgroup | identity verified | expected process |
| ---: | ---: | --- | --- | ---: | ---: | ---: | --- | --- | ---: | --- | --- | --- |
| 56339 | 0 | udp | lo | 4026531833 | 1048576 | 1048576 | python3 | - | 24078915132401 | 23051 | no | yes |
| 56339 | 0 | udp | flowvA | 4026531833 | 0 | 1048576 | Thread-3 (wildc | - | 24078915132401 | 23051 | no | yes |
| 56385 | 0 | udp | 8@4026532905 | 4026532905 | 1048576 | 0 | python3 | - | 24081235154800 | 23051 | no | yes |
| 1403 | 103 | udp | flowvA | 4026531833 | 0 | 1779 | avahi-daemon | /usr/sbin/avahi-daemon | 6990183514 | 6245 | yes | no |
| 1403 | 103 | udp | - | 4026531833 | 1779 | 0 | avahi-daemon | /usr/sbin/avahi-daemon | 6990183514 | 6245 | yes | no |
| 6724 | 1000 | udp | - | 4026531833 | 1604 | 0 | python3 | /usr/bin/python3.14 | 785079719945 | 15791 | yes | no |

## Interface attribution

- Expected interface: one wildcard UDP listener on `lo` and `flowvA` in root netns; client TX from flowvB (8@4026532905)
- Expected-process resolved interface(s): 8@4026532905, flowvA, lo
- Expected-process rows with unresolved interface: 0
- Expected-process unresolved-interface bytes: TX 0, RX 0
- TCP interface name(s): (none) (TX: (none); RX: (none))
- UDP interface name(s): 8@4026532905, flowvA, lo (TX: 8@4026532905, lo; RX: flowvA, lo)

## Wildcard multi-interface observation

- Payload per source: 1048576
- Server received: loopback 1048576, veth 1048576
- flowvA ifindex in root: 9; flowvB ifindex in peer netns: 8
- Wildcard per-datagram interface attribution check: True
- The same server socket received traffic on two interfaces; rows must differ by interface name.

## Loader overhead (first datapoint)

- CPU time: 0.01 s
- Average CPU: 0.129% of one core during the run
- Peak RSS: 3972 KiB

## Checks

- TCP attributed TX within ±1% of payload sent: True
- TCP attributed RX within ±1% of payload received: True
- UDP attributed TX within ±1% of payload accepted by send(): True
- UDP attributed RX within ±1% of payload delivered to recvfrom(): True
- Expected TGID has exactly one process generation (no thread/start-time split): True
- Expected process identity verified against live `/proc`: n/a (short-lived emitting threads)
- Expected-process traffic on expected interface(s): True
- No unattributed bytes in a fully controlled transfer: True
- Wildcard server received on two interfaces with correct per-interface bytes: True

## Interpretation and caveats

- eBPF counts TCP and UDP payload bytes at the send/recv function return; UDP/QUIC
  framing is not identified, so QUIC traffic is expected to appear as UDP payload.
  per-interface counters are not sampled for this probe. Ratios near 100% are expected but exact equality
  is not.
- The parent wildcard server and the peer-netns client are expected processes. Client identity is n/a after exit; kernel-captured identity is retained.
- Background traffic from other processes is reported separately; unresolved `-`
  rows for unrelated background traffic do not affect the expected-process gate.
- UDP datagrams may be dropped by the receiver; the UDP RX comparison uses bytes actually
  delivered to recvfrom(), and dropped bytes are not counted as attributed RX.
- A temporary veth pair plus loopback in the root netns exercises one wildcard-bound UDP socket receiving on two interfaces. This is not a VPN, physical-NIC, or multiple-simultaneous-interface workload.
- This is a controlled sanity check, not a claim of complete attribution coverage.
