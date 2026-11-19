# eBPF TCP+UDP attribution controlled validation

> **Status: accepted overflow-visibility evidence, but capacity still insufficient.**
> The post-fix run at 32,768 map entries reached 32,577 entries with 16,448
> children. The new `F` counter correctly reported **154,624 bytes / 151 events**
> of map-full overflow in each direction, so loss is no longer silent. Correcting
> the harness shortfall line (it had compared per-protocol expectation to the
> two-protocol total) gives an exact shortfall of **156,672 TX / 154,624 RX**;
> the RX shortfall matches overflow exactly, while TX has an additional 2,048
> bytes (2 events) not captured by the overflow map.
>
> This is **not a clean pass**: at ~16.4k short-lived children the provider still
> loses ~0.46% of bytes and `check_single_generation: False` because some child
> PIDs got no row. CPU averaged 32.585% of one core and RSS grew 3,824 → 15,864 KiB.
> Next step: raise capacity further (65,536) or add eviction; overflow accounting
> itself is validated.

- Date (UTC): 2026-11-19T01:15:18+00:00
- Kernel: `7.0.0-43-generic`
- Loader: `/home/amaechi/code/flow/prototype/bpf/flow_tcp_bytes_loader` (interval 1.0 s)
- Payload per protocol: 1024 bytes (0.001 MiB), high-rate child churn toward BPF map capacity
- Transfers: batches of 64 short-lived TCP+UDP children, 1024 bytes per protocol each
- Expected process TGID(s): 110386, 110387, 110388, 110389, 110390, 110391, ... (16448 total)
- Loader wall time: 854.09 s

## Results by protocol

| Protocol | Payload sent | Payload received | Attributed TX | Attributed RX | TX / sent | RX / received |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| TCP (`tcp_sendmsg`/`tcp_recvmsg`) | 16842752 | 16842752 | 16764928 | 16765952 | 99.538% | 99.544% |
| UDP (`udp_sendmsg`/`udp_recvmsg`) | 16842752 | 16842752 | 16763904 | 16764928 | 99.532% | 99.538% |

- UDP delivered / UDP sent: 100.0% (drops are possible; eBPF RX is compared to delivered bytes).

| Aggregate | Bytes |
| --- | ---: |
| Total attributed TX, expected process | 33528832 |
| Total attributed RX, expected process | 33530880 |
| Other attributed TX (background traffic) | 152717 |
| Other attributed RX (background traffic) | 529502 |
| Total attributed TX | 33681549 |
| Total attributed RX | 34060382 |
| Unattributed TX | 0 |
| Unattributed RX | 0 |

## Per-process rows (TGID + event-time start_ns + UID + proto + interface)

| TGID | UID | Proto | Interface | Netns | TX | RX | comm | exe | start_ns | cgroup | identity verified | expected process |
| ---: | ---: | --- | --- | ---: | ---: | ---: | --- | --- | ---: | --- | --- | --- |
| 46770 | 1000 | tcp | wlp0s20f3 | 4026531833 | 69559 | 365741 | Chrome_ChildIOT | - | 14343641288622 | 25707 | no | no |
| 107711 | 1000 | tcp | wlp0s20f3 | 4026531833 | 29539 | 41235 | Chrome_ChildIOT | - | 31180949983939 | 25707 | no | no |
| 109350 | 1000 | tcp | wlp0s20f3 | 4026531833 | 10783 | 44921 | .NET TP Worker | - | 31745333999248 | 18956 | no | no |
| 7292 | 1000 | tcp | wlp0s20f3 | 4026531833 | 10978 | 22570 | opencode | /home/amaechi/.opencode/bin/opencode | 821871857043 | 18956 | yes | no |
| 494 | 989 | udp | lo | 4026531833 | 17432 | 11042 | systemd-resolve | /usr/lib/systemd/systemd-resolved | 4474133849 | 2746 | yes | no |
| 46946 | 1000 | tcp | wlp0s20f3 | 4026531833 | 2018 | 16191 | code | - | 14345129201072 | 25707 | no | no |
| 494 | 989 | udp | wlp0s20f3 | 4026531833 | 2207 | 9682 | systemd-resolve | /usr/lib/systemd/systemd-resolved | 4474133849 | 2746 | yes | no |
| 109350 | 1000 | udp | lo | 4026531833 | 2604 | 5177 | .NET TP Worker | - | 31745333999248 | 18956 | no | no |
| 46770 | 1000 | udp | lo | 4026531833 | 1828 | 4994 | Chrome_ChildIOT | - | 14343641288622 | 25707 | no | no |
| 22691 | 1000 | tcp | wlp0s20f3 | 4026531833 | 2021 | 2295 | HTTP Client | - | 7554278408042 | 22527 | no | no |
| 1612 | 988 | udp | wlp0s20f3 | 4026531833 | 1212 | 1212 | chronyd | /usr/sbin/chronyd | 7331500526 | 6369 | yes | no |
| 107711 | 1000 | udp | lo | 4026531833 | 507 | 1667 | Chrome_ChildIOT | - | 31180949983939 | 25707 | no | no |
| 110389 | 0 | udp | lo | 4026531833 | 1024 | 1024 | python3 | - | 31858656043734 | 23051 | no | yes |
| 110389 | 0 | tcp | lo | 4026531833 | 1024 | 1024 | python3 | - | 31858656043734 | 23051 | no | yes |
| 110436 | 0 | tcp | lo | 4026531833 | 1024 | 1024 | Thread-1 (serve | - | 31858803017086 | 23051 | no | yes |
| 110401 | 0 | tcp | lo | 4026531833 | 1024 | 1024 | python3 | - | 31858676277616 | 23051 | no | yes |
| 110403 | 0 | tcp | lo | 4026531833 | 1024 | 1024 | python3 | - | 31858677552557 | 23051 | no | yes |
| 110425 | 0 | udp | lo | 4026531833 | 1024 | 1024 | python3 | - | 31858749607296 | 23051 | no | yes |
| 110410 | 0 | tcp | lo | 4026531833 | 1024 | 1024 | Thread-1 (serve | - | 31858697609224 | 23051 | no | yes |
| 110393 | 0 | tcp | lo | 4026531833 | 1024 | 1024 | python3 | - | 31858658206639 | 23051 | no | yes |

## Interface attribution

- Expected interface: all expected child rows on `lo`
- Expected-process resolved interface(s): lo
- Expected-process rows with unresolved interface: 0
- Expected-process unresolved-interface bytes: TX 0, RX 0
- TCP interface name(s): lo (TX: lo; RX: lo)
- UDP interface name(s): lo (TX: lo; RX: lo)

## Map churn observation

- Target map entries: 32700; reached: False
- Children spawned: 16448; successful: 16448; batches: 257
- Expected bytes per protocol/direction (successes × child payload): 16842752
- Exact total shortfall (2 × expected per protocol − attributed TX): 156672 (RX shortfall 154624; overflow F reported 154624 TX / 154624 RX)
- Attributed expected TX/RX: 33528832 / 33530880
- BPF map entries: first 130, last 32577, max 32577
- CPU samples (s): 0.062414613, 0.092032859, 0.133405413, 0.188580968, 0.230964202, 0.306729499, 0.403241202, 0.484635433, 0.574376419, 0.660494645, 0.769108862, 0.92699924, 1.06105127, 1.161912625, 1.240415029, 1.479739747, 1.639685226, 1.796715671, 2.063389617, 2.15357306, 2.501198284, 2.657703833, 2.97660111, 3.08130649, 3.420787522, 3.541673031, 3.868992819, 4.107217359, 4.327357483, 4.661610822, 4.996337369, 5.303510208, 5.502449619, 5.776106243, 6.060960338, 6.361900998, 6.668873839, 6.947631523, 7.179162467, 7.394558256, 7.622962673, 7.938069114, 8.260357732, 8.588740307, 8.951785154, 9.315078897, 9.648535975, 10.024383731, 10.409780976, 10.82049059, 11.186865773, 11.567326219, 12.004927406, 12.450404741, 12.9003696, 13.323090341, 13.757771967, 14.197467161, 14.69259793, 15.224249072, 15.721630044, 16.233317979, 16.722036492, 17.260479131, 17.806398222, 18.355273028, 18.951305904, 19.512474991, 20.129519235, 20.733407842, 21.385106673, 22.003869556, 22.650562475, 23.310075383, 23.939423281, 24.54610128, 25.180839038, 25.820608043, 26.492034849, 27.219190772, 27.906362693, 28.629810555, 29.329448057, 30.169123352, 31.021291693, 31.78060981, 32.56714028, 33.383406767, 34.118207571, 35.040246191, 35.860180798, 36.701421399, 37.532227846, 38.519063672, 39.343749601, 40.163845774, 41.01391324, 41.944067119, 42.898406224, 43.901078719, 44.872113335, 45.624784908, 46.59301279, 47.714994603, 48.767430072, 49.929081382, 50.455493026, 51.418174048, 52.528422335, 53.040036452, 54.142379422, 55.295178775, 56.143405598, 57.018619799, 58.109738003, 58.906438203, 60.128309589, 60.940490451, 62.264773214, 63.141449573, 63.815527348, 65.274686705, 66.072537684, 66.685012803, 68.123288548, 68.955652031, 69.674238567, 71.195331017, 72.218922001, 73.094044926, 73.861254243, 75.325366686, 76.460553276, 77.230082011, 78.453436348, 79.577752711, 80.860372476, 81.95242177, 83.077622101, 84.297901019, 85.412211564, 86.539108923, 87.588026281, 88.778223837, 90.073611162, 91.308599741, 92.541999645, 93.773422906, 95.047270754, 96.397668722, 97.62146143, 98.897634873, 100.232383431, 101.574865835, 103.004429562, 104.351460564, 105.763083009, 107.191624697, 108.61363083, 110.049553183, 111.490984787, 113.002872451, 114.420170221, 115.953546875, 117.306446191, 118.795312007, 120.254889234, 121.715856772, 123.241221865, 124.767695898, 126.397345181, 128.071567736, 129.557541437, 131.258261121, 132.834860178, 134.437189909, 136.094431204, 137.980875119, 139.684611071, 141.305774157, 143.095127948, 144.780937073, 146.480004514, 148.195501497, 149.801621193, 151.579725473, 153.533305919, 155.317191633, 157.726965856, 159.282806718, 161.040241118, 162.825981399, 164.68776844, 166.399103555, 168.252424437, 170.083543416, 171.820306131, 173.657900787, 175.30978379, 177.373381853, 179.182531739, 181.19932559, 183.152930303, 185.040834899, 186.988584953, 188.88601051, 190.84880213, 192.897486672, 194.93121824, 196.950010215, 199.010869162, 200.259340581, 202.117305888, 203.698883721, 205.297190585, 206.628101058, 208.626206699, 209.891557997, 211.413120349, 213.289035093, 214.635587069, 216.328769167, 218.338415553, 219.513679377, 221.065809861, 222.662247173, 224.411511307, 226.092790068, 227.774924725, 229.535269692, 231.523537251, 232.843094708, 234.536016154, 236.075027661, 238.16920944, 240.062796874, 241.706016054, 243.014311735, 245.087901377, 246.634041579, 248.737109164, 249.827437139, 252.06437143, 253.334803294, 255.401626897, 256.495667161, 258.652785572, 259.851178833, 262.080015479, 263.364756815, 265.489314684, 266.870374281, 269.105037661, 270.344796685, 272.620736385, 273.938856947, 276.123571959
- Peak RSS samples (KiB): 3824, 3824, 3824, 3824, 3824, 3824, 3824, 3824, 3824, 3824, 3864, 3912, 3960, 4012, 4048, 4108, 4128, 4208, 4256, 4276, 4352, 4376, 4452, 4492, 4548, 4588, 4644, 4696, 4708, 4788, 4844, 4892, 4928, 4948, 4996, 5048, 5092, 5160, 5196, 5240, 5296, 5356, 5404, 5452, 5500, 5548, 5608, 5652, 5700, 5752, 5808, 5852, 5892, 5944, 5992, 6052, 6100, 6152, 6204, 6240, 6300, 6352, 6400, 6452, 6500, 6552, 6592, 6644, 6696, 6748, 6792, 6844, 6896, 6944, 6996, 7048, 7096, 7144, 7192, 7240, 7288, 7340, 7388, 7436, 7488, 7540, 7584, 7636, 7684, 7724, 7784, 7828, 7880, 7916, 7976, 8028, 8076, 8124, 8172, 8224, 8260, 8320, 8368, 8416, 8464, 8516, 8524, 8612, 8664, 8668, 8760, 8812, 8852, 8884, 8956, 8984, 9060, 9092, 9156, 9196, 9212, 9300, 9336, 9356, 9448, 9488, 9508, 9592, 9636, 9672, 9700, 9788, 9840, 9876, 9932, 9980, 10028, 10076, 10132, 10176, 10228, 10272, 10320, 10364, 10424, 10468, 10520, 10564, 10616, 10660, 10712, 10760, 10808, 10856, 10904, 10948, 11000, 11052, 11100, 11144, 11196, 11240, 11296, 11340, 11388, 11440, 11492, 11540, 11592, 11636, 11680, 11736, 11780, 11832, 11876, 11924, 11972, 12028, 12072, 12124, 12176, 12216, 12268, 12324, 12368, 12412, 12468, 12512, 12568, 12612, 12656, 12708, 12760, 12808, 12856, 12904, 12952, 12996, 13044, 13100, 13148, 13192, 13244, 13292, 13344, 13388, 13440, 13488, 13536, 13588, 13636, 13668, 13728, 13772, 13832, 13864, 13924, 13968, 14012, 14072, 14116, 14160, 14212, 14252, 14312, 14356, 14412, 14460, 14504, 14564, 14612, 14644, 14700, 14740, 14808, 14852, 14888, 14932, 14992, 15036, 15100, 15128, 15200, 15224, 15296, 15324, 15396, 15420, 15484, 15520, 15588, 15616, 15684, 15720, 15784, 15812, 15860
- The map has no expiry; growth and any accounting shortfall near capacity are observations, not a bounded-memory claim.

## BPF map overflow

- Cumulative overflow TX: 154624 bytes (151 events)
- Cumulative overflow RX: 154624 bytes (151 events)
- These bytes could not get a process-generation row and are counted as unattributed overflow, not silently dropped.

## Loader overhead (first datapoint)

- CPU time: 278.305 s
- Average CPU: 32.585% of one core during the run
- Peak RSS: 15864 KiB

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
- Map target reached: False

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
