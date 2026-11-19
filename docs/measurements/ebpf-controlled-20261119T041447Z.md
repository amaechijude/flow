# eBPF TCP+UDP attribution controlled validation

- Date (UTC): 2026-11-19T04:15:22+00:00
- Kernel: `7.0.0-43-generic`
- Loader: `/home/amaechi/code/flow/prototype/bpf/flow_tcp_bytes_loader` (interval 1.0 s)
- Payload per protocol: 16777216 bytes (16.000 MiB), identity diagnostic, 8 workers × 30s
- Transfers: 8 concurrent workers, 30s, 1.000 MiB per protocol per transfer
- Expected process TGID(s): 172373
- Loader wall time: 35.15 s

## Results by protocol

| Protocol | Payload sent | Payload received | Attributed TX | Attributed RX | TX / sent | RX / received |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| TCP (`tcp_sendmsg`/`tcp_recvmsg`) | 307232768 | 307232768 | 307232768 | 307232768 | 100.0% | 100.0% |
| UDP (`udp_sendmsg`/`udp_recvmsg`) | 307232768 | 307232768 | 307232768 | 307232768 | 100.0% | 100.0% |

- UDP delivered / UDP sent: 100.0% (drops are possible; eBPF RX is compared to delivered bytes).

| Aggregate | Bytes |
| --- | ---: |
| Total attributed TX, expected process | 614465536 |
| Total attributed RX, expected process | 614465536 |
| Other attributed TX (background traffic) | 543 |
| Other attributed RX (background traffic) | 1372 |
| Total attributed TX | 614466079 |
| Total attributed RX | 614466908 |
| Unattributed TX | 0 |
| Unattributed RX | 0 |
| `lo` TX delta (IP/link, both protocols) | 618213989 |
| `lo` RX delta (IP/link, both protocols) | 618213989 |

## Per-process rows (TGID + event-time start_ns + UID + proto + interface)

| TGID | UID | Proto | Interface | Netns | TX | RX | comm | exe | start_ns | cgroup | identity verified | expected process |
| ---: | ---: | --- | --- | ---: | ---: | ---: | --- | --- | ---: | --- | --- | --- |
| 172373 | 0 | udp | lo | 4026531833 | 307232768 | 307232768 | Thread-3 (prod_ | - | 42480389592063 | 23051 | no | yes |
| 172373 | 0 | tcp | lo | 4026531833 | 307232768 | 307232768 | Thread-7 (prod_ | - | 42480389592063 | 23051 | no | yes |
| 7292 | 1000 | tcp | wlp0s20f3 | 4026531833 | 460 | 1293 | opencode | /home/amaechi/.opencode/bin/opencode | 821871857043 | 18956 | yes | no |
| 7292 | 1000 | tcp | lo | 4026531833 | 54 | 0 | opencode | /home/amaechi/.opencode/bin/opencode | 821871857043 | 18956 | yes | no |
| 22691 | 1000 | tcp | lo | 4026531833 | 0 | 54 | HTTP Client | - | 7554278408042 | 22527 | no | no |
| 107711 | 1000 | tcp | wlp0s20f3 | 4026531833 | 29 | 25 | Chrome_ChildIOT | - | 31180949983939 | 25707 | no | no |

## Interface attribution

- Expected interface: all expected rows on `lo`
- Expected-process resolved interface(s): lo
- Expected-process rows with unresolved interface: 0
- Expected-process unresolved-interface bytes: TX 0, RX 0
- TCP interface name(s): lo (TX: lo; RX: lo)
- UDP interface name(s): lo (TX: lo; RX: lo)

## Identity diagnostic

- clk_tck from loader M line: 100
- /proc/172373/stat starttime: 4378613
- /proc/172373/exe: /usr/bin/python3.14
- TGID 172373 proto 2 comm=Thread-3 (prod_ exe=-: start_ns=42480389592063 startticks=0 computed=4248038 diff=4248038 verified=False
- TGID 172373 proto 1 comm=Thread-7 (prod_ exe=-: start_ns=42480389592063 startticks=0 computed=4248038 diff=4248038 verified=False
- If computed≈stored but verified is false, investigate /proc exe enrichment; if they differ, investigate event-time start-time/leader handling.

## Loader overhead (first datapoint)

- CPU time: 0.039 s
- Average CPU: 0.111% of one core during the run
- Peak RSS: 3796 KiB

## Checks

- TCP attributed TX within ±1% of payload sent: True
- TCP attributed RX within ±1% of payload received: True
- UDP attributed TX within ±1% of payload accepted by send(): True
- UDP attributed RX within ±1% of payload delivered to recvfrom(): True
- Expected TGID has exactly one process generation (no thread/start-time split): True
- Expected process identity verified against live `/proc`: n/a (short-lived emitting threads)
- Expected-process traffic on expected interface(s): True
- No unattributed bytes in a fully controlled transfer: True

## Interpretation and caveats

- eBPF counts TCP and UDP payload bytes at the send/recv function return; UDP/QUIC
  framing is not identified, so QUIC traffic is expected to appear as UDP payload.
  `lo` counters include IP/link bytes above socket payload. Ratios near 100% are expected but exact equality
  is not.
- Expected rows cover the parent workers and any short-lived children spawned during the soak; per-process byte totals are aggregated.
- Background traffic from other processes is reported separately; unresolved `-`
  rows for unrelated background traffic do not affect the expected-process gate.
- UDP datagrams may be dropped by the receiver; the UDP RX comparison uses bytes actually
  delivered to recvfrom(), and dropped bytes are not counted as attributed RX.
- Loopback-only production-load proxy: high concurrency and longer duration, not a real Internet workload, physical NIC, or VPN.
- This is a controlled sanity check, not a claim of complete attribution coverage.
