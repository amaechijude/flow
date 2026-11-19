# eBPF TCP+UDP attribution controlled validation

> **Status: accepted real WireGuard evidence (2026-11-19).** A 16 MiB TCP echo
> over a real `wg0` kernel device above a veth underlay attributed the client to
> `flowwg0` in the root netns and the server to `6@4026532903` in the peer netns,
> each exactly 16,777,216 TX/RX, with no expected rows on the underlay and 0
> unattributed. `check_wireguard: True`. Overlay and underlay counters both grew
> ~17.9 MB, documenting the overlap; only `wg0` is attributed to processes.
>
> The raw UDP checks were false because this probe is TCP-only (`udp_sent=0`);
> the harness now treats UDP as n/a for WireGuard mode. This is a real WireGuard
> device in two netns, not a public VPN provider or physical NIC.

- Date (UTC): 2026-11-19T04:33:22+00:00
- Kernel: `7.0.0-43-generic`
- Loader: `/home/amaechi/code/flow/prototype/bpf/flow_tcp_bytes_loader` (interval 1.0 s)
- Payload per protocol: 16777216 bytes (16.000 MiB), TCP echo over WireGuard above veth underlay
- Transfers: TCP echo over WireGuard above veth underlay
- Expected process TGID(s): 175381, 175463
- Loader wall time: 8.13 s

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
| Other attributed TX (background traffic) | 15167 |
| Other attributed RX (background traffic) | 22735 |
| Total attributed TX | 33569599 |
| Total attributed RX | 33577167 |
| Unattributed TX | 0 |
| Unattributed RX | 0 |

## Per-process rows (TGID + event-time start_ns + UID + proto + interface)

| TGID | UID | Proto | Interface | Netns | TX | RX | comm | exe | start_ns | cgroup | identity verified | expected process |
| ---: | ---: | --- | --- | ---: | ---: | ---: | --- | --- | ---: | --- | --- | --- |
| 175381 | 0 | tcp | flowwg0 | 4026531833 | 16777216 | 16777216 | python3 | - | 43587400861077 | 23051 | no | yes |
| 175463 | 0 | tcp | 6@4026532903 | 4026532903 | 16777216 | 16777216 | python3 | - | 43589934407060 | 23051 | no | yes |
| 107711 | 1000 | tcp | wlp0s20f3 | 4026531833 | 8727 | 18117 | Chrome_ChildIOT | - | 31180949983939 | 25707 | no | no |
| 6724 | 1000 | udp | - | 4026531833 | 3208 | 0 | python3 | /usr/bin/python3.14 | 785079719945 | 15791 | yes | no |
| 1403 | 103 | udp | - | 4026531833 | 1949 | 0 | avahi-daemon | /usr/sbin/avahi-daemon | 6990183514 | 6245 | yes | no |
| 494 | 989 | udp | lo | 4026531833 | 915 | 700 | systemd-resolve | /usr/lib/systemd/systemd-resolved | 4474133849 | 2746 | yes | no |
| 1403 | 103 | udp | flowvA | 4026531833 | 0 | 1595 | avahi-daemon | /usr/sbin/avahi-daemon | 6990183514 | 6245 | yes | no |
| 7292 | 1000 | tcp | wlp0s20f3 | 4026531833 | 0 | 1290 | opencode | /home/amaechi/.opencode/bin/opencode | 821871857043 | 18956 | yes | no |
| 107711 | 1000 | udp | lo | 4026531833 | 350 | 661 | Chrome_ChildIOT | - | 31180949983939 | 25707 | no | no |
| 1403 | 103 | udp | lo | 4026531833 | 0 | 118 | avahi-daemon | /usr/sbin/avahi-daemon | 6990183514 | 6245 | yes | no |
| 1403 | 103 | udp | docker0 | 4026531833 | 0 | 118 | avahi-daemon | /usr/sbin/avahi-daemon | 6990183514 | 6245 | yes | no |
| 1403 | 103 | udp | br-2d646eaac65c | 4026531833 | 0 | 118 | avahi-daemon | /usr/sbin/avahi-daemon | 6990183514 | 6245 | yes | no |
| 22691 | 1000 | tcp | lo | 4026531833 | 0 | 18 | HTTP Client | - | 7554278408042 | 22527 | no | no |
| 7292 | 1000 | tcp | lo | 4026531833 | 18 | 0 | opencode | /home/amaechi/.opencode/bin/opencode | 821871857043 | 18956 | yes | no |

## Interface attribution

- Expected interface: client TX on `flowwg0` (root) and server RX on 6@4026532903
- Expected-process resolved interface(s): 6@4026532903, flowwg0
- Expected-process rows with unresolved interface: 0
- Expected-process unresolved-interface bytes: TX 0, RX 0
- TCP interface name(s): 6@4026532903, flowwg0 (TX: 6@4026532903, flowwg0; RX: 6@4026532903, flowwg0)
- UDP interface name(s): (none) (TX: (none); RX: (none))

## WireGuard observation

- WireGuard check (wg0 only, no underlay rows): True
- Root wg0 counter delta: TX 17877724, RX 17859328
- Root underlay counter delta: TX 18428070, RX 18397270
- Peer-netns wg0 counter delta: TX 17859328, RX 17877724
- WireGuard and underlay both carried the packets; only wg0 is attributed to the process.

## Loader overhead (first datapoint)

- CPU time: 0.015 s
- Average CPU: 0.185% of one core during the run
- Peak RSS: 3960 KiB

## Checks

- TCP attributed TX within ±1% of payload sent: True
- TCP attributed RX within ±1% of payload received: True
- UDP attributed TX within ±1% of payload accepted by send(): n/a (TCP-only probe)
- UDP attributed RX within ±1% of payload delivered to recvfrom(): n/a (TCP-only probe)
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
