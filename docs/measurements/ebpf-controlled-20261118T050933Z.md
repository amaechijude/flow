# eBPF TCP+UDP attribution controlled validation

> **Status (2026-11-18 review): acceptable byte-accuracy evidence, but the interface gate fails.**
> TCP TX/RX and UDP TX/RX each matched the 16 MiB payload exactly with one verified
> process generation and zero unattributed bytes. TCP and UDP TX resolved to `lo`;
> **UDP RX (16,777,216 bytes) had `ifindex=0`** and was grouped without an interface.
> Per-interface coverage is therefore accepted only for TCP and UDP TX. A follow-up
> used per-datagram RX interface correlation and needs a rerun.

- Date (UTC): 2026-11-18T05:09:39+00:00
- Kernel: `7.0.0-43-generic`
- Loader: `/home/amaechi/code/flow/prototype/bpf/flow_tcp_bytes_loader` (interval 1.0 s)
- Payload per protocol: 16777216 bytes (16.000 MiB), loopback `lo`
- Transfers: TCP then UDP, same process
- Expected process TGID: 53413
- Loader wall time: 6.59 s

## Results by protocol

| Protocol | Payload sent | Payload received | Attributed TX | Attributed RX | TX / sent | RX / received |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| TCP (`tcp_sendmsg`/`tcp_recvmsg`) | 16777216 | 16777216 | 16777216 | 16777216 | 100.0% | 100.0% |
| UDP (`udp_sendmsg`/`udp_recvmsg`) | 16777216 | 16777216 | 16777216 | 16777216 | 100.0% | 100.0% |

- UDP delivered / UDP sent: 100.0% (drops are possible; eBPF RX is compared to delivered bytes).

| Aggregate | Bytes |
| --- | ---: |
| Total attributed TX, expected process | 33554432 |
| Total attributed RX, expected process | 33554432 |
| Other attributed TX (background traffic) | 0 |
| Other attributed RX (background traffic) | 0 |
| Total attributed TX | 33554432 |
| Total attributed RX | 33554432 |
| Unattributed TX | 0 |
| Unattributed RX | 0 |
| `lo` TX delta (IP/link, both protocols) | 33686232 |
| `lo` RX delta (IP/link, both protocols) | 33686232 |

## Per-process rows (TGID + event-time start_ns + UID + proto + interface)

| TGID | UID | Proto | Interface | TX | RX | comm | exe | start_ns | cgroup | identity verified | expected process |
| ---: | ---: | --- | --- | ---: | ---: | --- | --- | ---: | --- | --- | --- |
| 53413 | 0 | tcp | lo | 16777216 | 16777216 | python3 | /usr/bin/python3.14 | 18434041394626 | 23979 | yes | yes |
| 53413 | 0 | udp | - | 0 | 16777216 | python3 | /usr/bin/python3.14 | 18434041394626 | 23979 | yes | yes |
| 53413 | 0 | udp | lo | 16777216 | 0 | python3 | /usr/bin/python3.14 | 18434041394626 | 23979 | yes | yes |

## Interface attribution

- Expected-process resolved interface(s): lo
- Expected-process rows with unresolved interface: 1
- Expected-process unresolved-interface bytes: TX 0, RX 16777216
- Expected-process TCP interface name(s): lo
- Expected-process UDP interface name(s): -, lo

## Loader overhead (first datapoint)

- CPU time: 0.005 s
- Average CPU: 0.076% of one core during the run
- Peak RSS: 3900 KiB

## Checks

- TCP attributed TX within ±1% of payload sent: True
- TCP attributed RX within ±1% of payload received: True
- UDP attributed TX within ±1% of payload accepted by send(): True
- UDP attributed RX within ±1% of payload delivered to recvfrom(): True
- Expected TGID has exactly one process generation (no thread/start-time split): True
- Expected process identity verified against live `/proc`: True
- Expected-process traffic attributed to `lo`: False
- No unattributed bytes in a fully controlled transfer: True

## Interpretation and caveats

- eBPF counts TCP and UDP payload bytes at the send/recv function return; UDP/QUIC
  framing is not identified, so QUIC traffic is expected to appear as UDP payload.
  `lo` counters count IP/link bytes including headers. Ratios near 100% are expected
  but exact equality is not.
- The expected-process rows are the load generator itself (its server threads share the TGID);
  background traffic from other processes during the run is reported separately.
- UDP datagrams may be dropped by the receiver; the UDP RX comparison uses bytes actually
  delivered to recvfrom(), and dropped bytes are not counted as attributed RX.
- Loopback traffic appears once in each interface direction; this run does not exercise
  physical-interface, namespace, VPN, or container paths, and it does not prove that
  unconnected/uncached UDP sockets always resolve an interface.
- This is a controlled sanity check, not a claim of complete attribution coverage.
