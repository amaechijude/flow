# eBPF TCP+UDP attribution controlled validation

> **Status: accepted physical-NIC TCP+UDP evidence (2026-11-18).** All checks
> passed. Parent TGID 16534 produced exactly **16,777,216 TX / 16,777,216 RX**
> for **both** TCP and UDP, with both expected rows resolving to `wlp0s20f3` in
> the root netns, one verified process generation, and 0 unresolved/unattributed
> bytes. Peer byte counts were supplied via `--peer-bytes`/`--peer-udp-bytes`
> and matched the sent payloads; the Termux count lines were not pasted for this
> particular run, so peer confirmation here is operator-supplied rather than
> independently archived (the earlier TCP-only run did include phone output).
>
> This validates hardware-interface attribution for TCP and UDP on this Wi-Fi
> path. It does not cover VPN/tunnel paths, multiple simultaneous interfaces,
> wildcard multi-interface sockets, or cellular-offload behavior. `wlp0s20f3`
> counters exceed socket payload because of IP/link/Wi-Fi overhead.

- Date (UTC): 2026-11-18T14:42:41+00:00
- Kernel: `7.0.0-43-generic`
- Loader: `/home/amaechi/code/flow/prototype/bpf/flow_tcp_bytes_loader` (interval 1.0 s)
- Payload per protocol: 16777216 bytes (16.000 MiB), TCP+UDP echo via physical `wlp0s20f3` to 10.61.133.76:9000 (UDP 9001)
- Transfers: TCP echo to 10.61.133.76:9000 and UDP echo to 10.61.133.76:9001 via wlp0s20f3
- Expected process TGID(s): 16534
- Loader wall time: 27.17 s

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
| Other attributed TX (background traffic) | 325 |
| Other attributed RX (background traffic) | 395 |
| Total attributed TX | 33554757 |
| Total attributed RX | 33554827 |
| Unattributed TX | 0 |
| Unattributed RX | 0 |
| `wlp0s20f3` TX delta (IP/link/Wi-Fi) | 34562093 |
| `wlp0s20f3` RX delta (IP/link/Wi-Fi) | 34980595 |

## Per-process rows (TGID + event-time start_ns + UID + proto + interface)

| TGID | UID | Proto | Interface | Netns | TX | RX | comm | exe | start_ns | cgroup | identity verified | expected process |
| ---: | ---: | --- | --- | ---: | ---: | ---: | --- | --- | ---: | --- | --- | --- |
| 16534 | 0 | tcp | wlp0s20f3 | 4026531833 | 16777216 | 16777216 | python3 | /usr/bin/python3.14 | 5442085979852 | 20222 | yes | yes |
| 16534 | 0 | udp | wlp0s20f3 | 4026531833 | 16777216 | 16777216 | python3 | /usr/bin/python3.14 | 5442085979852 | 20222 | yes | yes |
| 1547 | 988 | udp | wlp0s20f3 | 4026531833 | 188 | 188 | chronyd | /usr/sbin/chronyd | 7190162472 | 6369 | yes | no |
| 11348 | 1000 | tcp | wlp0s20f3 | 4026531833 | 101 | 171 | msedge | /opt/microsoft/msedge/msedge | 2001097554860 | 20266 | yes | no |
| 6571 | 1000 | tcp | lo | 4026531833 | 36 | 0 | opencode | /home/amaechi/.opencode/bin/opencode | 117947341413 | 18303 | yes | no |
| 6553 | 1000 | tcp | lo | 4026531833 | 0 | 36 | opencode | /home/amaechi/.opencode/bin/opencode | 112319649427 | 18303 | yes | no |

## Interface attribution

- Expected interface: TCP and UDP TX/RX on `wlp0s20f3` in the root netns via peer 10.61.133.76
- Expected-process resolved interface(s): wlp0s20f3
- Expected-process rows with unresolved interface: 0
- Expected-process unresolved-interface bytes: TX 0, RX 0
- TCP interface name(s): wlp0s20f3 (TX: wlp0s20f3; RX: wlp0s20f3)
- UDP interface name(s): wlp0s20f3 (TX: wlp0s20f3; RX: wlp0s20f3)

## Loader overhead (first datapoint)

- CPU time: 0.021 s
- Average CPU: 0.077% of one core during the run
- Peak RSS: 3808 KiB

## Checks

- TCP attributed TX within ±1% of payload sent: True
- TCP attributed RX within ±1% of payload received: True
- UDP attributed TX within ±1% of payload accepted by send(): True
- UDP attributed RX within ±1% of payload delivered to recvfrom(): True
- Expected TGID has exactly one process generation (no thread/start-time split): True
- Expected process identity verified against live `/proc`: True
- Expected-process traffic on expected interface(s): True
- No unattributed bytes in a fully controlled transfer: True
- Peer TCP listener byte count (16777216 bytes supplied via --peer-bytes): True
- Peer UDP listener byte count (16777216 bytes supplied via --peer-udp-bytes): True

## Interpretation and caveats

- eBPF counts TCP and UDP payload bytes at the send/recv function return; UDP/QUIC
  framing is not identified, so QUIC traffic is expected to appear as UDP payload.
  `wlp0s20f3` counters include IP/link and Wi-Fi overhead. Ratios near 100% are expected but exact equality
  is not.
- The expected rows are the parent client process; peer byte counts are reported separately when supplied via --peer-bytes/--peer-udp-bytes.
- Background traffic from other processes is reported separately; unresolved `-`
  rows for unrelated background traffic do not affect the expected-process gate.
- UDP datagrams may be dropped by the receiver; the UDP RX comparison uses bytes actually
  delivered to recvfrom(), and dropped bytes are not counted as attributed RX.
- TCP and UDP echo through physical wlp0s20f3 to a peer on the phone hotspot. This validates hardware-interface attribution only; it does not cover VPN/tunnel paths, multiple simultaneous interfaces, or wildcard multi-interface sockets. Traffic terminates at the phone, so no cellular payload is used for this test.
- This is a controlled sanity check, not a claim of complete attribution coverage.
