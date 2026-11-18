# eBPF TCP+UDP attribution controlled validation

> **Status: accepted hard map-capacity approach (2026-11-18).** The probe
> spawned 5,056 short-lived TCP+UDP children and grew BPF map entries from 128
> to **10,107** (of 10,240). The 10,000 target was reached, yet byte totals
> stayed exact at 20,709,376 per protocol/direction, `check_churn_bytes: True`,
> 0 unattributed, and all expected rows on `lo`. CPU averaged 10.142% of one
> core and peak RSS rose 3,788 → 7,224 KiB as entries accumulated.
>
> This is evidence of stable accounting close to the map cap, **not** a test of
> behavior at the hard 10,240 limit or a bounded-memory claim. No entry loss or
> unattributed shortfall was observed up to 10,107 entries.

- Date (UTC): 2026-11-18T22:56:03+00:00
- Kernel: `7.0.0-43-generic`
- Loader: `/home/amaechi/code/flow/prototype/bpf/flow_tcp_bytes_loader` (interval 1.0 s)
- Payload per protocol: 4096 bytes (0.004 MiB), high-rate child churn toward BPF map capacity
- Transfers: batches of 64 short-lived TCP+UDP children, 4096 bytes per protocol each
- Expected process TGID(s): 56589, 56590, 56591, 56592, 56593, 56594, ... (5056 total)
- Loader wall time: 245.38 s

## Results by protocol

| Protocol | Payload sent | Payload received | Attributed TX | Attributed RX | TX / sent | RX / received |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| TCP (`tcp_sendmsg`/`tcp_recvmsg`) | 20709376 | 20709376 | 20709376 | 20709376 | 100.0% | 100.0% |
| UDP (`udp_sendmsg`/`udp_recvmsg`) | 20709376 | 20709376 | 20709376 | 20709376 | 100.0% | 100.0% |

- UDP delivered / UDP sent: 100.0% (drops are possible; eBPF RX is compared to delivered bytes).

| Aggregate | Bytes |
| --- | ---: |
| Total attributed TX, expected process | 41418752 |
| Total attributed RX, expected process | 41418752 |
| Other attributed TX (background traffic) | 41292 |
| Other attributed RX (background traffic) | 105527 |
| Total attributed TX | 41460044 |
| Total attributed RX | 41524279 |
| Unattributed TX | 0 |
| Unattributed RX | 0 |

## Per-process rows (TGID + event-time start_ns + UID + proto + interface)

| TGID | UID | Proto | Interface | Netns | TX | RX | comm | exe | start_ns | cgroup | identity verified | expected process |
| ---: | ---: | --- | --- | ---: | ---: | ---: | --- | --- | ---: | --- | --- | --- |
| 46770 | 1000 | tcp | wlp0s20f3 | 4026531833 | 10452 | 86474 | Chrome_ChildIOT | - | 14343641288622 | 25707 | no | no |
| 65379 | 1000 | tcp | wlp0s20f3 | 4026531833 | 26480 | 12292 | wakatime-cli-li | - | 24319161659721 | 25707 | no | no |
| 56604 | 0 | udp | lo | 4026531833 | 4096 | 4096 | python3 | - | 24183694269491 | 23051 | no | yes |
| 56604 | 0 | tcp | lo | 4026531833 | 4096 | 4096 | python3 | - | 24183694269491 | 23051 | no | yes |
| 56637 | 0 | tcp | lo | 4026531833 | 4096 | 4096 | python3 | - | 24183800303592 | 23051 | no | yes |
| 56634 | 0 | tcp | lo | 4026531833 | 4096 | 4096 | python3 | - | 24183779276926 | 23051 | no | yes |
| 56619 | 0 | tcp | lo | 4026531833 | 4096 | 4096 | python3 | - | 24183726630369 | 23051 | no | yes |
| 56622 | 0 | tcp | lo | 4026531833 | 4096 | 4096 | python3 | - | 24183737282890 | 23051 | no | yes |
| 56607 | 0 | tcp | lo | 4026531833 | 4096 | 4096 | python3 | - | 24183695976918 | 23051 | no | yes |
| 56599 | 0 | udp | lo | 4026531833 | 4096 | 4096 | python3 | - | 24183681899466 | 23051 | no | yes |
| 56632 | 0 | udp | lo | 4026531833 | 4096 | 4096 | python3 | - | 24183766299028 | 23051 | no | yes |
| 56594 | 0 | udp | lo | 4026531833 | 4096 | 4096 | python3 | - | 24183675582666 | 23051 | no | yes |
| 56607 | 0 | udp | lo | 4026531833 | 4096 | 4096 | python3 | - | 24183695976918 | 23051 | no | yes |
| 56625 | 0 | tcp | lo | 4026531833 | 4096 | 4096 | python3 | - | 24183745515148 | 23051 | no | yes |
| 56620 | 0 | udp | lo | 4026531833 | 4096 | 4096 | python3 | - | 24183730282897 | 23051 | no | yes |
| 56649 | 0 | udp | lo | 4026531833 | 4096 | 4096 | python3 | - | 24183863575641 | 23051 | no | yes |
| 56605 | 0 | tcp | lo | 4026531833 | 4096 | 4096 | python3 | - | 24183694792566 | 23051 | no | yes |
| 56603 | 0 | tcp | lo | 4026531833 | 4096 | 4096 | python3 | - | 24183688203868 | 23051 | no | yes |
| 56615 | 0 | tcp | lo | 4026531833 | 4096 | 4096 | python3 | - | 24183715524509 | 23051 | no | yes |
| 56649 | 0 | tcp | lo | 4026531833 | 4096 | 4096 | python3 | - | 24183863575641 | 23051 | no | yes |

## Interface attribution

- Expected interface: all expected child rows on `lo`
- Expected-process resolved interface(s): lo
- Expected-process rows with unresolved interface: 0
- Expected-process unresolved-interface bytes: TX 0, RX 0
- TCP interface name(s): lo (TX: lo; RX: lo)
- UDP interface name(s): lo (TX: lo; RX: lo)

## Map churn observation

- Target map entries: 10000; reached: True
- Children spawned: 5056; successful: 5056; batches: 79
- Expected bytes per protocol/direction (successes × child payload): 20709376
- Attributed expected TX/RX: 41418752 / 41418752
- BPF map entries: first 128, last 10107, max 10107
- CPU samples (s): 0.05273415, 0.075229606, 0.107732753, 0.155000628, 0.188539809, 0.231300154, 0.276843524, 0.319871614, 0.37982165, 0.472016899, 0.606450651, 0.735906943, 0.829727841, 1.010519368, 1.163634218, 1.315310608, 1.533872265, 1.669795528, 1.929322757, 2.083776112, 2.340156183, 2.47259484, 2.749829272, 2.951884159, 3.167879731, 3.44705898, 3.578304543, 3.831971952, 4.113908192, 4.39641088, 4.564573155, 4.825936856, 5.093836954, 5.316596, 5.516920096, 5.72049403, 5.923140843, 6.152175993, 6.375649132, 6.6268351, 6.946587707, 7.268406955, 7.595722356, 7.925306408, 8.268934256, 8.568096708, 8.836145839, 9.108239528, 9.446614938, 9.805050532, 10.201899319, 10.578834584, 10.951963177, 11.302609862, 11.705031026, 12.10926194, 12.488770986, 12.870261886, 13.265823504, 13.65006619, 14.06340089, 14.511004608, 14.962624774, 15.427620259, 15.953771988, 16.415446537, 16.911778143, 17.456903366, 18.025689627, 18.677980209, 19.269550685, 19.888863008, 20.517779183, 21.135757635, 21.772228803, 22.359430322, 22.982080177, 23.631077864, 24.290835452
- Peak RSS samples (KiB): 3788, 3788, 3788, 3788, 3788, 3788, 3788, 3788, 3788, 3844, 3892, 3940, 3964, 4040, 4080, 4136, 4188, 4212, 4284, 4308, 4384, 4400, 4480, 4528, 4544, 4628, 4652, 4692, 4772, 4824, 4860, 4884, 4924, 4980, 5028, 5076, 5116, 5172, 5216, 5272, 5332, 5384, 5428, 5480, 5536, 5584, 5636, 5688, 5732, 5784, 5836, 5884, 5936, 5984, 6032, 6084, 6128, 6180, 6228, 6276, 6328, 6372, 6424, 6476, 6516, 6576, 6624, 6676, 6728, 6764, 6828, 6876, 6924, 6972, 7016, 7072, 7120, 7172, 7220
- The map has no expiry; growth and any accounting shortfall near capacity are observations, not a bounded-memory claim.

## Loader overhead (first datapoint)

- CPU time: 24.886 s
- Average CPU: 10.142% of one core during the run
- Peak RSS: 7224 KiB

## Checks

- TCP attributed TX within ±1% of payload sent: True
- TCP attributed RX within ±1% of payload received: True
- UDP attributed TX within ±1% of payload accepted by send(): True
- UDP attributed RX within ±1% of payload delivered to recvfrom(): True
- Expected TGID has exactly one process generation (no thread/start-time split): True
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
