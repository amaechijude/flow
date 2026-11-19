# eBPF TCP+UDP attribution controlled validation

> **Status: accepted duplicate early WireGuard run (2026-11-19).** This was the
> first successful real-WireGuard run; it shows the same result as
> `ebpf-controlled-20261119T043314Z.md` (16 MiB TCP echo, client on `flowwg0`,
> server on netns-local wg0, exact bytes, no underlay rows, `check_wireguard: True`).
> The later report is the annotated primary reference.

- Date (UTC): 2026-11-19T04:32:36+00:00
- Kernel: `7.0.0-43-generic`
- Loader: `/home/amaechi/code/flow/prototype/bpf/flow_tcp_bytes_loader` (interval 1.0 s)
- Payload per protocol: 16777216 bytes (16.000 MiB), TCP echo over WireGuard above veth underlay
- Transfers: TCP echo over WireGuard above veth underlay
- Expected process TGID(s): 175126, 175300
- Loader wall time: 8.11 s

## Results by protocol

| Protocol | Payload sent | Payload received | Attributed TX | Attributed RX | TX / sent | RX / received |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| TCP (`tcp_sendmsg`/`tcp_recvmsg`) | 33554432 | 33554432 | 33554432 | 33554432 | 100.0% | 100.0% |
| UDP (`udp_sendmsg`/`udp_recvmsg`) | 0 | 0 | 0 | 0 | None% | None% |

- UDP delivered / UDP sent: None% (drops are possible; eBPF RX is compared to delivered bytes).

| Aggregate | Bytes |
| --- | ---: |
| Total attributed TX, expected process | 33554432 |
| Total attributed RX, expected process | 33554432 |
| Other attributed TX (background traffic) | 8790 |
| Other attributed RX (background traffic) | 12387 |
| Total attributed TX | 33563222 |
| Total attributed RX | 33566819 |
| Unattributed TX | 0 |
| Unattributed RX | 0 |

## Per-process rows (TGID + event-time start_ns + UID + proto + interface)

| TGID | UID | Proto | Interface | Netns | TX | RX | comm | exe | start_ns | cgroup | identity verified | expected process |
| ---: | ---: | --- | --- | ---: | ---: | ---: | --- | --- | ---: | --- | --- | --- |
| 175126 | 0 | tcp | flowwg0 | 4026531833 | 16777216 | 16777216 | python3 | - | 43541570711834 | 23051 | no | yes |
| 175300 | 0 | tcp | 6@4026533051 | 4026533051 | 16777216 | 16777216 | python3 | - | 43544092626140 | 23051 | no | yes |
| 107711 | 1000 | tcp | wlp0s20f3 | 4026531833 | 3010 | 9465 | Chrome_ChildIOT | - | 31180949983939 | 25707 | no | no |
| 6724 | 1000 | udp | - | 4026531833 | 3208 | 0 | python3 | /usr/bin/python3.14 | 785079719945 | 15791 | yes | no |
| 1403 | 103 | udp | flowvA | 4026531833 | 0 | 1543 | avahi-daemon | /usr/sbin/avahi-daemon | 6990183514 | 6245 | yes | no |
| 1403 | 103 | udp | - | 4026531833 | 1543 | 0 | avahi-daemon | /usr/sbin/avahi-daemon | 6990183514 | 6245 | yes | no |
| 494 | 989 | udp | lo | 4026531833 | 661 | 700 | systemd-resolve | /usr/lib/systemd/systemd-resolved | 4474133849 | 2746 | yes | no |
| 107711 | 1000 | udp | lo | 4026531833 | 350 | 661 | Chrome_ChildIOT | - | 31180949983939 | 25707 | no | no |
| 22691 | 1000 | tcp | lo | 4026531833 | 0 | 18 | HTTP Client | - | 7554278408042 | 22527 | no | no |
| 7292 | 1000 | tcp | lo | 4026531833 | 18 | 0 | opencode | /home/amaechi/.opencode/bin/opencode | 821871857043 | 18956 | yes | no |

## Interface attribution

- Expected interface: client TX on `flowwg0` (root) and server RX on 6@4026533051
- Expected-process resolved interface(s): 6@4026533051, flowwg0
- Expected-process rows with unresolved interface: 0
- Expected-process unresolved-interface bytes: TX 0, RX 0
- TCP interface name(s): 6@4026533051, flowwg0 (TX: 6@4026533051, flowwg0; RX: 6@4026533051, flowwg0)
- UDP interface name(s): (none) (TX: (none); RX: (none))

## WireGuard observation

- WireGuard check (wg0 only, no underlay rows): True
- Root wg0 counter delta: TX 17883672, RX 17856160
- Root underlay counter delta: TX 18434183, RX 18392626
- Peer-netns wg0 counter delta: TX 17856160, RX 17883672
- WireGuard and underlay both carried the packets; only wg0 is attributed to the process.

## Loader overhead (first datapoint)

- CPU time: 0.012 s
- Average CPU: 0.148% of one core during the run
- Peak RSS: 3864 KiB

## Checks

- TCP attributed TX within ±1% of payload sent: True
- TCP attributed RX within ±1% of payload received: True
- UDP attributed TX within ±1% of payload accepted by send(): False
- UDP attributed RX within ±1% of payload delivered to recvfrom(): False
- Expected TGID has exactly one process generation (no thread/start-time split): True
- Expected process identity verified against live `/proc`: n/a (short-lived emitting threads)
- Expected-process traffic on expected interface(s): True
- No unattributed bytes in a fully controlled transfer: True
- TCP echo attributed to WireGuard devices with no underlay rows: True

## Interpretation and caveats

- eBPF counts TCP and UDP payload bytes at the send/recv function return; UDP/QUIC
  framing is not identified, so QUIC traffic is expected to appear as UDP payload.
  WireGuard and underlay counters are sampled separately. Ratios near 100% are expected but exact equality
  is not.
- The parent client and the peer-netns WireGuard server are expected processes. Server identity is n/a after exit; kernel-captured identity is retained.
- Background traffic from other processes is reported separately; unresolved `-`
  rows for unrelated background traffic do not affect the expected-process gate.
- UDP datagrams may be dropped by the receiver; the UDP RX comparison uses bytes actually
  delivered to recvfrom(), and dropped bytes are not counted as attributed RX.
- A real WireGuard kernel device above a veth underlay exercises tunnel-device attribution. Overlay and underlay both carry packets; double-counting is reported, not treated as additional process traffic. No public VPN provider.
- This is a controlled sanity check, not a claim of complete attribution coverage.
