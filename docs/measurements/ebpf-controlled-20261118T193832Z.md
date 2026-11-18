# eBPF TCP+UDP attribution controlled validation

> **Status: accepted high-churn observation (2026-11-18), with target not reached.**
> The probe spawned 3,840 short-lived TCP+UDP children in 180 s and grew BPF
> map entries from 130 to 7,653 (of 10,240 capacity). Byte totals stayed exact
> at 15,728,640 per protocol/direction, `check_churn_bytes: True`, 0
> unattributed, and expected rows all on `lo`. It did **not** reach the 9,000
> entry target within the timeout, so this is evidence of stable accounting at
> high entry counts, not a capacity-limit or bounded-memory claim. Loader CPU
> averaged 7.815% of one core and peak RSS rose from 3,940 KiB to 6,380 KiB as
> entries accumulated — memory grew with retained process generations.
>
> The harness wrote this report and then exited with an `UnboundLocalError`
> because `check_netns_collision` was not initialized in map-churn mode. That
> harness bug was fixed afterwards; report values above are the measured data.

- Date (UTC): 2026-11-18T19:41:41+00:00
- Kernel: `7.0.0-43-generic`
- Loader: `/home/amaechi/code/flow/prototype/bpf/flow_tcp_bytes_loader` (interval 1.0 s)
- Payload per child: 4096 bytes per protocol, high-rate child churn toward BPF map capacity
- Transfers: batches of 64 short-lived TCP+UDP children, 4096 bytes per protocol each
- Expected process TGID(s): 33625, 33626, 33627, 33628, 33629, 33630, ... (3840 total)
- Loader wall time: 186.50 s

## Results by protocol

| Protocol | Payload sent | Payload received | Attributed TX | Attributed RX | TX / sent | RX / received |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| TCP (`tcp_sendmsg`/`tcp_recvmsg`) | 15728640 | 15728640 | 15728640 | 15728640 | 100.0% | 100.0% |
| UDP (`udp_sendmsg`/`udp_recvmsg`) | 15728640 | 15728640 | 15728640 | 15728640 | 100.0% | 100.0% |

- UDP delivered / UDP sent: 100.0% (drops are possible; eBPF RX is compared to delivered bytes).

| Aggregate | Bytes |
| --- | ---: |
| Total attributed TX, expected process | 31457280 |
| Total attributed RX, expected process | 31457280 |
| Other attributed TX (background traffic) | 5591 |
| Other attributed RX (background traffic) | 9141 |
| Total attributed TX | 31462871 |
| Total attributed RX | 31466421 |
| Unattributed TX | 0 |
| Unattributed RX | 0 |

## Per-process rows (TGID + event-time start_ns + UID + proto + interface)

| TGID | UID | Proto | Interface | Netns | TX | RX | comm | exe | start_ns | cgroup | identity verified | expected process |
| ---: | ---: | --- | --- | ---: | ---: | ---: | --- | --- | ---: | --- | --- | --- |
| 7292 | 1000 | tcp | wlp0s20f3 | 4026531833 | 3649 | 6019 | opencode | /home/amaechi/.opencode/bin/opencode | 821871857043 | 18956 | yes | no |
| 33643 | 0 | tcp | lo | 4026531833 | 4096 | 4096 | python3 | - | 12583014281883 | 23051 | no | yes |
| 33656 | 0 | tcp | lo | 4026531833 | 4096 | 4096 | python3 | - | 12583054521030 | 23051 | no | yes |
| 33686 | 0 | tcp | lo | 4026531833 | 4096 | 4096 | python3 | - | 12583182594593 | 23051 | no | yes |
| 33683 | 0 | udp | lo | 4026531833 | 4096 | 4096 | python3 | - | 12583163574895 | 23051 | no | yes |
| 33673 | 0 | tcp | lo | 4026531833 | 4096 | 4096 | python3 | - | 12583116273411 | 23051 | no | yes |
| 33664 | 0 | udp | lo | 4026531833 | 4096 | 4096 | python3 | - | 12583091617020 | 23051 | no | yes |
| 33657 | 0 | udp | lo | 4026531833 | 4096 | 4096 | python3 | - | 12583060661183 | 23051 | no | yes |
| 33667 | 0 | tcp | lo | 4026531833 | 4096 | 4096 | python3 | - | 12583096566434 | 23051 | no | yes |
| 33681 | 0 | udp | lo | 4026531833 | 4096 | 4096 | python3 | - | 12583161276580 | 23051 | no | yes |
| 33653 | 0 | tcp | lo | 4026531833 | 4096 | 4096 | python3 | - | 12583047285278 | 23051 | no | yes |
| 33640 | 0 | udp | lo | 4026531833 | 4096 | 4096 | python3 | - | 12583007295077 | 23051 | no | yes |
| 33688 | 0 | tcp | lo | 4026531833 | 4096 | 4096 | python3 | - | 12583191882442 | 23051 | no | yes |
| 33625 | 0 | tcp | lo | 4026531833 | 4096 | 4096 | python3 | - | 12582984240636 | 23051 | no | yes |
| 33649 | 0 | udp | lo | 4026531833 | 4096 | 4096 | python3 | - | 12583033561266 | 23051 | no | yes |
| 33641 | 0 | tcp | lo | 4026531833 | 4096 | 4096 | python3 | - | 12583007878819 | 23051 | no | yes |
| 33639 | 0 | udp | lo | 4026531833 | 4096 | 4096 | python3 | - | 12583001831642 | 23051 | no | yes |
| 33672 | 0 | tcp | lo | 4026531833 | 4096 | 4096 | python3 | - | 12583111626460 | 23051 | no | yes |
| 33662 | 0 | udp | lo | 4026531833 | 4096 | 4096 | python3 | - | 12583083622413 | 23051 | no | yes |
| 33677 | 0 | udp | lo | 4026531833 | 4096 | 4096 | python3 | - | 12583141603180 | 23051 | no | yes |

## Interface attribution

- Expected interface: all expected child rows on `lo`
- Expected-process resolved interface(s): lo
- Expected-process rows with unresolved interface: 0
- Expected-process unresolved-interface bytes: TX 0, RX 0
- TCP interface name(s): lo (TX: lo; RX: lo)
- UDP interface name(s): lo (TX: lo; RX: lo)

## Map churn observation

- Target map entries: 9000; reached: False
- Children spawned: 3840; successful: 3840; batches: 60
- Expected bytes per protocol/direction (successes × child payload): 15728640
- Attributed expected TX/RX: 31457280 / 31457280
- BPF map entries: first 130, last 7653, max 7653
- CPU samples (s): 0.052678978, 0.074440655, 0.107607902, 0.147717499, 0.213275614, 0.297798089, 0.354558658, 0.386219176, 0.448856471, 0.570111802, 0.705466747, 0.832864868, 0.90195811, 1.089970092, 1.26752254, 1.339322828, 1.560259308, 1.699659709, 1.940380255, 2.104157076, 2.327836611, 2.494770335, 2.781964729, 2.904778911, 3.191352031, 3.462213615, 3.672446422, 3.944893744, 4.191786671, 4.42199272, 4.622916703, 4.810285839, 5.059508178, 5.260068969, 5.478252658, 5.662297692, 5.919071683, 6.234427871, 6.530186697, 6.835568673, 7.158072346, 7.479316519, 7.799952048, 8.119448702, 8.479793022, 8.806065629, 9.17855964, 9.511573962, 9.862894335, 10.228159322, 10.606631973, 10.997806972, 11.367333634, 11.76690955, 12.156133087, 12.540718763, 12.955561447, 13.344515775, 13.722727927, 14.118154563
- Peak RSS samples (KiB): 3940, 3940, 3940, 3940, 3940, 3940, 3940, 3940, 3940, 3940, 3980, 4028, 4076, 4124, 4176, 4200, 4272, 4284, 4372, 4388, 4468, 4484, 4568, 4580, 4664, 4716, 4744, 4780, 4832, 4896, 4932, 4972, 5032, 5068, 5120, 5164, 5220, 5264, 5316, 5372, 5420, 5464, 5524, 5568, 5604, 5672, 5716, 5768, 5824, 5872, 5920, 5972, 6016, 6068, 6120, 6172, 6212, 6268, 6312, 6368
- The map has no expiry; growth and any accounting shortfall near capacity are observations, not a bounded-memory claim.

## Loader overhead (first datapoint)

- CPU time: 14.574 s
- Average CPU: 7.815% of one core during the run
- Peak RSS: 6380 KiB

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
- Map target reached: False

## Interpretation and caveats

- The 9,000-entry target was not reached within 180 s; the run ended at 7,653
  entries after 3,840 children. This still exercised high map occupancy without
  byte loss or unattributed traffic.
- Peak RSS grew with retained process generations (3,940 → 6,380 KiB) and
  loader CPU averaged 7.815% of one core. The BPF map has no expiry, so this is
  an observation of growth, not a bounded-memory or production-overhead claim.
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
