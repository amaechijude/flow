# eBPF TCP+UDP attribution controlled validation

> **Status: accepted for controlled loopback coverage (2026-11-18).** All harness
> checks passed: TCP and UDP each matched 16,777,216 TX/RX exactly, one verified
> process generation, 0 unattributed bytes, and every expected-process row (TCP
> and UDP, TX and RX) resolved to `lo`. Background `opencode` traffic (18 bytes
> each direction) was separated and identity-verified. This is evidence for this
> loopback case only — physical interfaces, namespaces, containers, VPN/tunnel
> and virtual-interface behavior, wildcard sockets receiving on multiple
> interfaces, and sustained overhead remain unmeasured. The CPU/RSS figures are
> a single short-run datapoint.

- Date (UTC): 2026-11-18T08:49:07+00:00
- Kernel: `7.0.0-43-generic`
- Loader: `/home/amaechi/code/flow/prototype/bpf/flow_tcp_bytes_loader` (interval 1.0 s)
- Payload per protocol: 16777216 bytes (16.000 MiB), loopback `lo`
- Transfers: TCP then UDP, same process
- Expected process TGID: 10979
- Loader wall time: 6.71 s

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
| Other attributed TX (background traffic) | 18 |
| Other attributed RX (background traffic) | 18 |
| Total attributed TX | 33554450 |
| Total attributed RX | 33554450 |
| Unattributed TX | 0 |
| Unattributed RX | 0 |
| `lo` TX delta (IP/link, both protocols) | 33685638 |
| `lo` RX delta (IP/link, both protocols) | 33685638 |

## Per-process rows (TGID + event-time start_ns + UID + proto + interface)

| TGID | UID | Proto | Interface | TX | RX | comm | exe | start_ns | cgroup | identity verified | expected process |
| ---: | ---: | --- | --- | ---: | ---: | --- | --- | ---: | --- | --- | --- |
| 10979 | 0 | tcp | lo | 16777216 | 16777216 | python3 | /usr/bin/python3.14 | 545630292274 | 18750 | yes | yes |
| 10979 | 0 | udp | lo | 16777216 | 16777216 | python3 | /usr/bin/python3.14 | 545630292274 | 18750 | yes | yes |
| 6019 | 1000 | tcp | lo | 0 | 18 | opencode | /home/amaechi/.opencode/bin/opencode | 114587790130 | 18270 | yes | no |
| 6030 | 1000 | tcp | lo | 18 | 0 | opencode | /home/amaechi/.opencode/bin/opencode | 114950780258 | 18270 | yes | no |

## Interface attribution

- Expected-process resolved interface(s): lo
- Expected-process rows with unresolved interface: 0
- Expected-process unresolved-interface bytes: TX 0, RX 0
- Expected-process TCP interface name(s): lo
- Expected-process UDP interface name(s): lo

## Loader overhead (first datapoint)

- CPU time: 0.005 s
- Average CPU: 0.075% of one core during the run
- Peak RSS: 3880 KiB

## Checks

- TCP attributed TX within ±1% of payload sent: True
- TCP attributed RX within ±1% of payload received: True
- UDP attributed TX within ±1% of payload accepted by send(): True
- UDP attributed RX within ±1% of payload delivered to recvfrom(): True
- Expected TGID has exactly one process generation (no thread/start-time split): True
- Expected process identity verified against live `/proc`: True
- Expected-process traffic attributed to `lo`: True
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
