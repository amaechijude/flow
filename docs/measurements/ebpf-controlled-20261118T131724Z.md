# eBPF TCP+UDP attribution controlled validation

> **Status: accepted physical-NIC evidence (2026-11-18, TCP over `wlp0s20f3`).**
> All applicable checks passed. The expected process accounted for exactly
> **16,777,216 TX / 16,777,216 RX** bytes, its row resolved to `wlp0s20f3` in
> the root netns, one verified process generation, 0 unresolved/unattributed
> bytes. The phone's Termux listener independently reported **16,777,216 bytes**,
> matching the payload and `--peer-bytes`.
>
> This is TCP-only: UDP through the hotspot was not exercised. It validates
> hardware-interface attribution on this Wi-Fi path; it does not cover
> VPN/tunnel paths, multiple simultaneous interfaces, wildcard multi-interface
> sockets, or cellular-offload behavior. Wi-Fi/IP link counters include overhead,
> so they are not expected to equal socket payload exactly.

- Date (UTC): 2026-11-18T13:18:22+00:00
- Kernel: `7.0.0-43-generic`
- Loader: `/home/amaechi/code/flow/prototype/bpf/flow_tcp_bytes_loader` (interval 1.0 s)
- Payload per protocol: 16777216 bytes (16.000 MiB), TCP echo via physical `wlp0s20f3` to 10.61.133.76:9000
- Transfers: TCP echo through wlp0s20f3 to 10.61.133.76:9000 (TCP only; no UDP)
- Expected process TGID(s): 8053
- Loader wall time: 57.07 s

## Results by protocol

| Protocol | Payload sent | Payload received | Attributed TX | Attributed RX | TX / sent | RX / received |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| TCP (`tcp_sendmsg`/`tcp_recvmsg`) | 16777216 | 16777216 | 16777216 | 16777216 | 100.0% | 100.0% |
| UDP (`udp_sendmsg`/`udp_recvmsg`) | 0 | 0 | 0 | 0 | None% | None% |

- UDP delivered / UDP sent: None% (drops are possible; eBPF RX is compared to delivered bytes).

| Aggregate | Bytes |
| --- | ---: |
| Total attributed TX, expected process | 16777216 |
| Total attributed RX, expected process | 16777216 |
| Other attributed TX (background traffic) | 1838 |
| Other attributed RX (background traffic) | 1899 |
| Total attributed TX | 16779054 |
| Total attributed RX | 16779115 |
| Unattributed TX | 0 |
| Unattributed RX | 0 |
| `wlp0s20f3` TX delta (IP/link/Wi-Fi) | 17224856 |
| `wlp0s20f3` RX delta (IP/link/Wi-Fi) | 17739281 |

## Per-process rows (TGID + event-time start_ns + UID + proto + interface)

| TGID | UID | Proto | Interface | Netns | TX | RX | comm | exe | start_ns | cgroup | identity verified | expected process |
| ---: | ---: | --- | --- | ---: | ---: | ---: | --- | --- | ---: | --- | --- | --- |
| 8053 | 0 | tcp | wlp0s20f3 | 4026531833 | 16777216 | 16777216 | python3 | /usr/bin/python3.14 | 352783865762 | 18827 | yes | yes |
| 6571 | 1000 | tcp | wlp0s20f3 | 4026531833 | 460 | 1289 | opencode | /home/amaechi/.opencode/bin/opencode | 117947341413 | 18303 | yes | no |
| 1547 | 988 | udp | wlp0s20f3 | 4026531833 | 1024 | 256 | chronyd | /usr/sbin/chronyd | 7190162472 | 6369 | yes | no |
| 1328 | 103 | udp | - | 4026531833 | 282 | 0 | avahi-daemon | /usr/sbin/avahi-daemon | 6818219986 | 6245 | yes | no |
| 1328 | 103 | udp | wlp0s20f3 | 4026531833 | 0 | 282 | avahi-daemon | /usr/sbin/avahi-daemon | 6818219986 | 6245 | yes | no |
| 6553 | 1000 | tcp | lo | 4026531833 | 0 | 72 | opencode | /home/amaechi/.opencode/bin/opencode | 112319649427 | 18303 | yes | no |
| 6571 | 1000 | tcp | lo | 4026531833 | 72 | 0 | opencode | /home/amaechi/.opencode/bin/opencode | 117947341413 | 18303 | yes | no |

## Interface attribution

- Expected interface: TCP TX and RX on `wlp0s20f3` in the root netns via peer 10.61.133.76:9000
- Expected-process resolved interface(s): wlp0s20f3
- Expected-process rows with unresolved interface: 0
- Expected-process unresolved-interface bytes: TX 0, RX 0
- TCP interface name(s): wlp0s20f3 (TX: wlp0s20f3; RX: wlp0s20f3)
- UDP interface name(s): (none) (TX: (none); RX: (none))

## Loader overhead (first datapoint)

- CPU time: 0.05 s
- Average CPU: 0.088% of one core during the run
- Peak RSS: 3884 KiB

## Checks

- TCP attributed TX within ±1% of payload sent: True
- TCP attributed RX within ±1% of payload received: True
- UDP attributed TX within ±1% of payload accepted by send(): n/a (TCP-only mode)
- UDP attributed RX within ±1% of payload delivered to recvfrom(): n/a (TCP-only mode)
- Expected TGID has exactly one process generation (no thread/start-time split): True
- Expected process identity verified against live `/proc`: True
- Expected-process traffic on expected interface(s): True
- No unattributed bytes in a fully controlled transfer: True
- Peer listener byte count (16777216 bytes supplied via --peer-bytes): True

## Interpretation and caveats

- eBPF counts TCP and UDP payload bytes at the send/recv function return; UDP/QUIC
  framing is not identified, so QUIC traffic is expected to appear as UDP payload.
  `wlp0s20f3` counters include IP/link and Wi-Fi overhead. Ratios near 100% are expected but exact equality
  is not.
- The expected row is the parent client process; the peer listener's byte count is reported separately when supplied via --peer-bytes.
- Background traffic from other processes is reported separately; unresolved `-`
  rows for unrelated background traffic do not affect the expected-process gate.
- UDP datagrams may be dropped by the receiver; the UDP RX comparison uses bytes actually
  delivered to recvfrom(), and dropped bytes are not counted as attributed RX.
- TCP echo through physical wlp0s20f3 to a peer on the phone hotspot. This validates hardware-interface attribution only; it does not cover VPN/tunnel paths, multiple simultaneous interfaces, or wildcard multi-interface sockets. Traffic terminates at the phone, so no cellular payload is used for this test.
- This is a controlled sanity check, not a claim of complete attribution coverage.
