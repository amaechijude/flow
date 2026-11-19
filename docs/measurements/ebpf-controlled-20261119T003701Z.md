# eBPF TCP+UDP attribution controlled validation

> **Status: accepted hard-cap limitation evidence (2026-11-19).** The probe
> reached the 10,200 target and ended with **10,231 map entries** (of 10,240).
> At that occupancy, attributed bytes fell short: expected 5,242,880 per
> protocol/direction, attributed 5,235,712 — a **7,168-byte shortfall**
> (7 children × 1,024 bytes) in each of TCP TX/RX and UDP TX/RX. Those bytes
> were neither attributed nor counted as unattributed; they were dropped when
> the map could not add new process generations.
>
> `check_single_generation: False` is consistent with this: some expected child
> PIDs had no rows at all because their map insertions failed. The ±1%
> `check_churn_bytes` still passed because 0.137% loss is inside tolerance;
> the exact shortfall is the meaningful finding here.
>
> This accepts that **BPF map saturation causes silent under-attribution**.
> It is not a clean pass. The next step is a design fix (expiry/eviction, larger
> map, or explicit unattributed-on-map-full) before relying on this provider
> under high process churn. No bounded-memory or production-load claim.

- Date (UTC): 2026-11-19T00:41:19+00:00
- Kernel: `7.0.0-43-generic`
- Loader: `/home/amaechi/code/flow/prototype/bpf/flow_tcp_bytes_loader` (interval 1.0 s)
- Payload per protocol: 1024 bytes (0.001 MiB), high-rate child churn toward BPF map capacity
- Transfers: batches of 64 short-lived TCP+UDP children, 1024 bytes per protocol each
- Expected process TGID(s): 91593, 91594, 91595, 91596, 91597, 91598, ... (5120 total)
- Loader wall time: 253.38 s

## Results by protocol

| Protocol | Payload sent | Payload received | Attributed TX | Attributed RX | TX / sent | RX / received |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| TCP (`tcp_sendmsg`/`tcp_recvmsg`) | 5242880 | 5242880 | 5235712 | 5235712 | 99.863% | 99.863% |
| UDP (`udp_sendmsg`/`udp_recvmsg`) | 5242880 | 5242880 | 5235712 | 5235712 | 99.863% | 99.863% |

- UDP delivered / UDP sent: 100.0% (drops are possible; eBPF RX is compared to delivered bytes).

| Aggregate | Bytes |
| --- | ---: |
| Total attributed TX, expected process | 10471424 |
| Total attributed RX, expected process | 10471424 |
| Other attributed TX (background traffic) | 44596 |
| Other attributed RX (background traffic) | 161075 |
| Total attributed TX | 10516020 |
| Total attributed RX | 10632499 |
| Unattributed TX | 0 |
| Unattributed RX | 0 |

## Per-process rows (TGID + event-time start_ns + UID + proto + interface)

| TGID | UID | Proto | Interface | Netns | TX | RX | comm | exe | start_ns | cgroup | identity verified | expected process |
| ---: | ---: | --- | --- | ---: | ---: | ---: | --- | --- | ---: | --- | --- | --- |
| 46770 | 1000 | tcp | wlp0s20f3 | 4026531833 | 24790 | 117970 | Chrome_ChildIOT | - | 14343641288622 | 25707 | no | no |
| 46946 | 1000 | tcp | wlp0s20f3 | 4026531833 | 6627 | 18701 | code | - | 14345129201072 | 25707 | no | no |
| 7292 | 1000 | tcp | wlp0s20f3 | 4026531833 | 4109 | 9953 | opencode | /home/amaechi/.opencode/bin/opencode | 821871857043 | 18956 | yes | no |
| 494 | 989 | udp | lo | 4026531833 | 4087 | 3100 | systemd-resolve | /usr/lib/systemd/systemd-resolved | 4474133849 | 2746 | yes | no |
| 494 | 989 | udp | wlp0s20f3 | 4026531833 | 1106 | 4826 | systemd-resolve | /usr/lib/systemd/systemd-resolved | 4474133849 | 2746 | yes | no |
| 22691 | 1000 | tcp | wlp0s20f3 | 4026531833 | 2021 | 2295 | HTTP Client | - | 7554278408042 | 22527 | no | no |
| 91638 | 0 | tcp | lo | 4026531833 | 1024 | 1024 | python3 | - | 30491670671562 | 23051 | no | yes |
| 91618 | 0 | tcp | lo | 4026531833 | 1024 | 1024 | python3 | - | 30491597149644 | 23051 | no | yes |
| 91644 | 0 | tcp | lo | 4026531833 | 1024 | 1024 | python3 | - | 30491704933456 | 23051 | no | yes |
| 91634 | 0 | udp | lo | 4026531833 | 1024 | 1024 | python3 | - | 30491651597764 | 23051 | no | yes |
| 91651 | 0 | tcp | lo | 4026531833 | 1024 | 1024 | python3 | - | 30491737458795 | 23051 | no | yes |
| 91603 | 0 | udp | lo | 4026531833 | 1024 | 1024 | python3 | - | 30491564964168 | 23051 | no | yes |
| 91656 | 0 | udp | lo | 4026531833 | 1024 | 1024 | python3 | - | 30491756274788 | 23051 | no | yes |
| 91596 | 0 | udp | lo | 4026531833 | 1024 | 1024 | python3 | - | 30491557593089 | 23051 | no | yes |
| 91616 | 0 | udp | lo | 4026531833 | 1024 | 1024 | python3 | - | 30491595264521 | 23051 | no | yes |
| 91593 | 0 | udp | lo | 4026531833 | 1024 | 1024 | python3 | - | 30491553160101 | 23051 | no | yes |
| 91636 | 0 | udp | lo | 4026531833 | 1024 | 1024 | python3 | - | 30491666266790 | 23051 | no | yes |
| 91606 | 0 | udp | lo | 4026531833 | 1024 | 1024 | python3 | - | 30491569644601 | 23051 | no | yes |
| 91597 | 0 | udp | lo | 4026531833 | 1024 | 1024 | python3 | - | 30491558692167 | 23051 | no | yes |
| 91598 | 0 | udp | lo | 4026531833 | 1024 | 1024 | python3 | - | 30491559066586 | 23051 | no | yes |

## Interface attribution

- Expected interface: all expected child rows on `lo`
- Expected-process resolved interface(s): lo
- Expected-process rows with unresolved interface: 0
- Expected-process unresolved-interface bytes: TX 0, RX 0
- TCP interface name(s): lo (TX: lo; RX: lo)
- UDP interface name(s): lo (TX: lo; RX: lo)

## Map churn observation

- Target map entries: 10200; reached: True
- Children spawned: 5120; successful: 5120; batches: 80
- Expected bytes per protocol/direction (successes × child payload): 5242880
- Attributed expected TX/RX: 10471424 / 10471424
- BPF map entries: first 128, last 10231, max 10231
- Exact shortfall: expected 5242880 vs attributed 5235712 per protocol/direction = 7168 bytes (7 children) silently unaccounted
- CPU samples (s): 0.051378763, 0.073988116, 0.108348093, 0.15479927, 0.211111654, 0.293422841, 0.350249144, 0.434169216, 0.522664255, 0.581528959, 0.705139295, 0.854666884, 0.997305941, 1.084581968, 1.267898784, 1.458569068, 1.57718884, 1.825913304, 1.963972887, 2.048554372, 2.346417625, 2.502773848, 2.8089128, 2.973321548, 3.233162749, 3.501640237, 3.683143609, 3.945070901, 4.272036826, 4.410454634, 4.656641691, 4.909413196, 5.245551452, 5.576091242, 5.799375712, 6.077120913, 6.363555976, 6.658692888, 6.965417332, 7.291241552, 7.630704567, 7.929025024, 8.250018602, 8.609490451, 8.967102288, 9.302798112, 9.659625053, 10.012469384, 10.385479003, 10.737490363, 11.089708709, 11.474683283, 11.852667846, 12.260127795, 12.693223438, 13.073736033, 13.511116373, 13.962052572, 14.390299903, 14.835057693, 15.282683891, 15.753805248, 16.274314406, 16.820859851, 17.336281641, 17.877823094, 18.445688364, 19.000976588, 19.610315122, 20.155408902, 20.742737245, 21.307852377, 21.988176831, 22.654906698, 23.325997097, 23.969167071, 24.622470862, 25.281042295, 25.914482923, 26.583608734
- Peak RSS samples (KiB): 3952, 3952, 3952, 3952, 3952, 3952, 3952, 3952, 3952, 3952, 3992, 4040, 4092, 4120, 4188, 4236, 4272, 4336, 4384, 4436, 4484, 4512, 4584, 4616, 4656, 4728, 4752, 4808, 4876, 4920, 4932, 4976, 5072, 5120, 5144, 5180, 5228, 5280, 5340, 5376, 5432, 5488, 5528, 5580, 5636, 5684, 5732, 5784, 5832, 5880, 5932, 5980, 6032, 6080, 6124, 6184, 6224, 6276, 6332, 6380, 6432, 6476, 6524, 6580, 6628, 6676, 6728, 6780, 6824, 6876, 6928, 6972, 7024, 7072, 7124, 7176, 7224, 7272, 7320, 7368
- The map has no expiry; growth and any accounting shortfall near capacity are observations, not a bounded-memory claim.

## Loader overhead (first datapoint)

- CPU time: 27.181 s
- Average CPU: 10.727% of one core during the run
- Peak RSS: 7368 KiB

## Checks

- TCP attributed TX within ±1% of payload sent: True
- TCP attributed RX within ±1% of payload received: True
- UDP attributed TX within ±1% of payload accepted by send(): True
- UDP attributed RX within ±1% of payload delivered to recvfrom(): True
- Expected TGID has exactly one process generation (no thread/start-time split): False
- Expected process identity verified against live `/proc`: n/a (short-lived emitting threads)
- Expected-process traffic on expected interface(s): True
- No unattributed bytes in a fully controlled transfer: True
- Child byte totals matched expected payload: True
- Map target reached: True

## Interpretation and caveats

- eBPF counts TCP and UDP payload bytes at the send/recv function return; UDP/QUIC
  framing is not identified, so QUIC traffic is expected to appear as UDP payload.
  child traffic uses per-netns loopback; counters are not sampled. Ratios near 100% are expected but exact equality
  is not.
- Each short-lived child is an expected process generation. Identity verification is n/a because children exit before snapshot enrichment; kernel-captured identity is retained.
- Background traffic from other processes is reported separately; unresolved `-`
  rows for unrelated background traffic do not affect the expected-process gate.
- UDP datagrams may be dropped by the receiver; the UDP RX comparison uses bytes actually
  delivered to recvfrom(), and dropped bytes are not counted as attributed RX.
- Loopback-only churn in the root netns. This probes map capacity and accounting under churn, not physical interfaces, VPNs, or multi-interface behavior.
- This is a controlled sanity check, not a claim of complete attribution coverage.
