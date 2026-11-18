# eBPF TCP+UDP attribution controlled validation

- Date (UTC): 2026-11-18T09:42:38+00:00
- Kernel: `7.0.0-43-generic`
- Loader: `/home/amaechi/code/flow/prototype/bpf/flow_tcp_bytes_loader` (interval 1.0 s)
- Payload per protocol: 16777216 bytes (16.000 MiB), loopback `lo`
- Transfers: TCP then UDP, same process
- Expected process TGID: 39415
- Loader wall time: 6.87 s

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
| Other attributed TX (background traffic) | 13341 |
| Other attributed RX (background traffic) | 168663 |
| Total attributed TX | 33567773 |
| Total attributed RX | 33723095 |
| Unattributed TX | 0 |
| Unattributed RX | 0 |
| `lo` TX delta (IP/link, both protocols) | 33685950 |
| `lo` RX delta (IP/link, both protocols) | 33685950 |

## Per-process rows (TGID + event-time start_ns + UID + proto + interface)

| TGID | UID | Proto | Interface | Netns | TX | RX | comm | exe | start_ns | cgroup | identity verified | expected process |
| ---: | ---: | --- | --- | ---: | ---: | ---: | --- | --- | ---: | --- | --- | --- |
| 39415 | 0 | tcp | lo | 4026531833 | 16777216 | 16777216 | python3 | /usr/bin/python3.14 | 3755843271572 | 19055 | yes | yes |
| 39415 | 0 | udp | lo | 4026531833 | 16777216 | 16777216 | python3 | /usr/bin/python3.14 | 3755843271572 | 19055 | yes | yes |
| 14753 | 1000 | tcp | wlp0s20f3 | 4026531833 | 755 | 166659 | copilot-runtime | /snap/code/268/usr/share/code/resources/app/node_modules.asar.unpacked/@github/copilot-sdk-linux-x64/prebuilds/linux-x64/copilot-runtime | 1412689901765 | 19360 | yes | no |
| 14689 | 1000 | tcp | wlp0s20f3 | 4026531833 | 12072 | 605 | code | /snap/code/268/usr/share/code/code | 1412020853334 | 19360 | yes | no |
| 6030 | 1000 | tcp | wlp0s20f3 | 4026531833 | 460 | 1282 | opencode | /home/amaechi/.opencode/bin/opencode | 114950780258 | 18270 | yes | no |
| 14324 | 1000 | tcp | wlp0s20f3 | 4026531833 | 0 | 99 | code | /snap/code/268/usr/share/code/code | 1410938384496 | 19360 | yes | no |
| 488 | 989 | udp | wlp0s20f3 | 4026531833 | 36 | 0 | systemd-resolve | /usr/lib/systemd/systemd-resolved | 3909591732 | 2746 | yes | no |
| 6030 | 1000 | tcp | lo | 4026531833 | 18 | 0 | opencode | /home/amaechi/.opencode/bin/opencode | 114950780258 | 18270 | yes | no |
| 6019 | 1000 | tcp | lo | 4026531833 | 0 | 18 | opencode | /home/amaechi/.opencode/bin/opencode | 114587790130 | 18270 | yes | no |

## Interface attribution

- Expected interface: all expected-process rows on `lo`
- Expected-process resolved interface(s): lo
- Expected-process rows with unresolved interface: 0
- Expected-process unresolved-interface bytes: TX 0, RX 0
- TCP interface name(s): lo (TX: lo; RX: lo)
- UDP interface name(s): lo (TX: lo; RX: lo)

## Loader overhead (first datapoint)

- CPU time: 0.009 s
- Average CPU: 0.131% of one core during the run
- Peak RSS: 3924 KiB

## Checks

- TCP attributed TX within ±1% of payload sent: True
- TCP attributed RX within ±1% of payload received: True
- UDP attributed TX within ±1% of payload accepted by send(): True
- UDP attributed RX within ±1% of payload delivered to recvfrom(): True
- Expected TGID has exactly one process generation (no thread/start-time split): True
- Expected process identity verified against live `/proc`: True
- Expected-process traffic on expected interface(s): True
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
- Loopback traffic appears once in each interface direction; this run does not exercise physical-interface, namespace, VPN, or container paths, and it does not prove that unconnected/uncached UDP sockets always resolve an interface.
- This is a controlled sanity check, not a claim of complete attribution coverage.
