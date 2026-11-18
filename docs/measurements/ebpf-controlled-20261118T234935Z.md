# eBPF TCP+UDP attribution controlled validation

> **Status: accepted tunnel/overlay attribution evidence (2026-11-18).** A GRE
> tunnel above a veth underlay carried a 16 MiB TCP echo. The parent client's
> TX/RX resolved to `flowtun0` in the root netns, and the peer server's TX/RX
> resolved to `6@4026532906` in the peer netns — each exactly **16,777,216**
> bytes. `check_tunnel: True`, no expected rows on the underlay, 0
> unattributed. Overlay and underlay counters both grew by ~16.8 MB, documenting
> the overlap; only the tunnel device is attributed to the processes.
>
> The raw TCP/UDP protocol checks were harness expectation mismatches: expected
> rows include both client and server, so protocol totals are 33,554,432, while
> the harness compared against a single 16 MiB payload; UDP was not exercised.
> The harness was fixed afterwards to aggregate both endpoints and treat UDP as
> n/a for this TCP-only probe.

- Date (UTC): 2026-11-18T23:49:43+00:00
- Kernel: `7.0.0-43-generic`
- Loader: `/home/amaechi/code/flow/prototype/bpf/flow_tcp_bytes_loader` (interval 1.0 s)
- Payload per protocol: 16777216 bytes (16.000 MiB), TCP echo over gre tunnel above veth underlay
- Transfers: TCP echo over gre tunnel above a veth underlay
- Expected process TGID(s): 76760, 76829
- Loader wall time: 7.53 s

## Results by protocol

| Protocol | Payload sent | Payload received | Attributed TX | Attributed RX | TX / sent | RX / received |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| TCP (`tcp_sendmsg`/`tcp_recvmsg`) | 16777216 | 16777216 | 33554432 | 33554432 | 200.0% | 200.0% |
| UDP (`udp_sendmsg`/`udp_recvmsg`) | 0 | 0 | 0 | 0 | None% | None% |

- UDP delivered / UDP sent: None% (drops are possible; eBPF RX is compared to delivered bytes).

| Aggregate | Bytes |
| --- | ---: |
| Total attributed TX, expected process | 33554432 |
| Total attributed RX, expected process | 33554432 |
| Other attributed TX (background traffic) | 6499 |
| Other attributed RX (background traffic) | 1687 |
| Total attributed TX | 33560931 |
| Total attributed RX | 33556119 |
| Unattributed TX | 0 |
| Unattributed RX | 0 |

## Per-process rows (TGID + event-time start_ns + UID + proto + interface)

| TGID | UID | Proto | Interface | Netns | TX | RX | comm | exe | start_ns | cgroup | identity verified | expected process |
| ---: | ---: | --- | --- | ---: | ---: | ---: | --- | --- | ---: | --- | --- | --- |
| 76760 | 0 | tcp | flowtun0 | 4026531833 | 16777216 | 16777216 | python3 | - | 27644066472059 | 23051 | no | yes |
| 76829 | 0 | tcp | 6@4026532906 | 4026532906 | 16777216 | 16777216 | python3 | - | 27646267288648 | 23051 | no | yes |
| 6724 | 1000 | udp | - | 4026531833 | 4812 | 0 | python3 | /usr/bin/python3.14 | 785079719945 | 15791 | yes | no |
| 1403 | 103 | udp | flowvA | 4026531833 | 0 | 1687 | avahi-daemon | /usr/sbin/avahi-daemon | 6990183514 | 6245 | yes | no |
| 1403 | 103 | udp | - | 4026531833 | 1687 | 0 | avahi-daemon | /usr/sbin/avahi-daemon | 6990183514 | 6245 | yes | no |

## Interface attribution

- Expected interface: client TX on `flowtun0` (root) and server RX on 6@4026532906
- Expected-process resolved interface(s): 6@4026532906, flowtun0
- Expected-process rows with unresolved interface: 0
- Expected-process unresolved-interface bytes: TX 0, RX 0
- TCP interface name(s): 6@4026532906, flowtun0 (TX: 6@4026532906, flowtun0; RX: 6@4026532906, flowtun0)
- UDP interface name(s): (none) (TX: (none); RX: (none))

## Tunnel observation

- Tunnel mode: gre (flowtun0) above the veth underlay
- Tunnel check (overlay device only, no underlay rows): True
- Root tunnel counter delta: TX 16798650, RX 16794280
- Root underlay counter delta: TX 16816384, RX 16806834
- Peer-netns tunnel counter delta: TX 16794280, RX 16798650
- Overlay and underlay both carried the packets; only the tunnel device is attributed to the process. The underlay delta documents the overlap.

## Loader overhead (first datapoint)

- CPU time: 0.008 s
- Average CPU: 0.106% of one core during the run
- Peak RSS: 3872 KiB

## Checks

- TCP attributed TX within ±1% of payload sent: True (both endpoints: 33,554,432)
- TCP attributed RX within ±1% of payload received: True (both endpoints: 33,554,432)
- UDP attributed TX within ±1% of payload accepted by send(): n/a (TCP-only probe)
- UDP attributed RX within ±1% of payload delivered to recvfrom(): n/a (TCP-only probe)
- Expected TGID has exactly one process generation (no thread/start-time split): True
- Expected process identity verified against live `/proc`: n/a (short-lived emitting threads)
- Expected-process traffic on expected interface(s): True
- No unattributed bytes in a fully controlled transfer: True
- TCP echo attributed to tunnel devices with no underlay rows: True

## Interpretation and caveats

- eBPF counts TCP and UDP payload bytes at the send/recv function return; UDP/QUIC
  framing is not identified, so QUIC traffic is expected to appear as UDP payload.
  tunnel and underlay counters are sampled separately. Ratios near 100% are expected but exact equality
  is not.
- The parent client and the peer-netns server are expected processes. Server identity is n/a after exit; kernel-captured identity is retained.
- Background traffic from other processes is reported separately; unresolved `-`
  rows for unrelated background traffic do not affect the expected-process gate.
- UDP datagrams may be dropped by the receiver; the UDP RX comparison uses bytes actually
  delivered to recvfrom(), and dropped bytes are not counted as attributed RX.
- A gre tunnel above a veth underlay exercises tunnel-device attribution. Overlay and underlay both carry the packets; double-counting is reported, not treated as additional process traffic. No physical NIC or VPN daemon.
- This is a controlled sanity check, not a claim of complete attribution coverage.
