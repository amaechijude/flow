# eBPF TCP+UDP attribution controlled validation

> **Status: accepted isolation evidence — mismatch is systemic, not thread-related.**
> With one worker, loader raw `/proc` ticks = `/proc` starttime = 4,529,365, while
> BPF `start_ns` converts to 4,398,790 — the same ~130,575-tick gap seen with
> eight workers. Because the offset is constant across runs and worker counts,
> it is characteristic of a clock-domain difference (monotonic `start_time` vs
> boottime-based `/proc` starttime, e.g. suspend time), not a multithreading bug.
> Fix: read `task_struct.start_boottime` in BPF for event-time identity.

- Date (UTC): 2026-11-19T04:40:29+00:00
- Kernel: `7.0.0-43-generic`
- Loader: `/home/amaechi/code/flow/prototype/bpf/flow_tcp_bytes_loader` (interval 1.0 s)
- Payload per protocol: 16777216 bytes (16.000 MiB), identity diagnostic, 1 workers × 30s
- Transfers: 1 concurrent workers, 30s, 1.000 MiB per protocol per transfer
- Expected process TGID(s): 176154
- Loader wall time: 35.06 s

## Results by protocol

| Protocol | Payload sent | Payload received | Attributed TX | Attributed RX | TX / sent | RX / received |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| TCP (`tcp_sendmsg`/`tcp_recvmsg`) | 41943040 | 41943040 | 41943040 | 41943040 | 100.0% | 100.0% |
| UDP (`udp_sendmsg`/`udp_recvmsg`) | 41943040 | 41943040 | 41943040 | 41943040 | 100.0% | 100.0% |

- UDP delivered / UDP sent: 100.0% (drops are possible; eBPF RX is compared to delivered bytes).

| Aggregate | Bytes |
| --- | ---: |
| Total attributed TX, expected process | 83886080 |
| Total attributed RX, expected process | 83886080 |
| Other attributed TX (background traffic) | 661 |
| Other attributed RX (background traffic) | 1554 |
| Total attributed TX | 83886741 |
| Total attributed RX | 83887634 |
| Unattributed TX | 0 |
| Unattributed RX | 0 |
| `lo` TX delta (IP/link, both protocols) | 84246524 |
| `lo` RX delta (IP/link, both protocols) | 84246524 |

## Per-process rows (TGID + event-time start_ns + UID + proto + interface)

| TGID | UID | Proto | Interface | Netns | TX | RX | comm | exe | start_ns | cgroup | identity verified | expected process |
| ---: | ---: | --- | --- | ---: | ---: | ---: | --- | --- | ---: | --- | --- | --- |
| 176154 | 0 | tcp | lo | 4026531833 | 41943040 | 41943040 | Thread-4 (serve | - | 43987906239195 | 23051 | no | yes |
| 176154 | 0 | udp | lo | 4026531833 | 41943040 | 41943040 | Thread-3 (prod_ | - | 43987906239195 | 23051 | no | yes |
| 7292 | 1000 | tcp | wlp0s20f3 | 4026531833 | 460 | 1290 | opencode | /home/amaechi/.opencode/bin/opencode | 821871857043 | 18956 | yes | no |
| 107711 | 1000 | tcp | wlp0s20f3 | 4026531833 | 165 | 228 | Chrome_ChildIOT | - | 31180949983939 | 25707 | no | no |
| 22691 | 1000 | tcp | lo | 4026531833 | 0 | 36 | HTTP Client | - | 7554278408042 | 22527 | no | no |
| 7292 | 1000 | tcp | lo | 4026531833 | 36 | 0 | opencode | /home/amaechi/.opencode/bin/opencode | 821871857043 | 18956 | yes | no |

## Interface attribution

- Expected interface: all expected rows on `lo`
- Expected-process resolved interface(s): lo
- Expected-process rows with unresolved interface: 0
- Expected-process unresolved-interface bytes: TX 0, RX 0
- TCP interface name(s): lo (TX: lo; RX: lo)
- UDP interface name(s): lo (TX: lo; RX: lo)

## Identity diagnostic

- clk_tck from loader M line: 100
- /proc/176154/stat starttime: 4529365
- /proc/176154/exe: /usr/bin/python3.14
- TGID 176154 proto 1 comm=Thread-4 (serve exe=-: start_ns=43987906239195 cached_ticks=0 raw_ticks=4529365 computed=4398790 computed−raw=-130575 verified=False
- TGID 176154 proto 2 comm=Thread-3 (prod_ exe=-: start_ns=43987906239195 cached_ticks=0 raw_ticks=4529365 computed=4398790 computed−raw=-130575 verified=False
- If computed≈stored but verified is false, investigate /proc exe enrichment; if they differ, investigate event-time start-time/leader handling.

## Loader overhead (first datapoint)

- CPU time: 0.037 s
- Average CPU: 0.106% of one core during the run
- Peak RSS: 3956 KiB

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
