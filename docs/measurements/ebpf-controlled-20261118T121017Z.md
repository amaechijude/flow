# eBPF TCP+UDP attribution controlled validation

> **Status: accepted 300-second soak evidence (2026-11-18).** All checks passed.
> Parent TGID 86954 accounted for exactly **364,904,448 TX/RX bytes per protocol**
> (348 iterations × 1 MiB), one verified process generation, all expected rows on
> `lo`, and 0 unresolved/unattributed bytes. 69 short-lived child transfers
> produced distinct background process generations; some show `netns=0`, `exe=-`,
> or `identity verified: no` because they exited before `/proc` enrichment.
>
> Loader CPU grew from 0.038 s to 1.895 s cumulatively (~0.616% of one core on
> average) while peak RSS stayed constant at **3,960 KiB** across all samples.
> BPF map entries grew **10 → 161** over ~305 s with no expiry. This is an
> observation of growth under low churn, not a bounded-memory claim, a capacity
> test, or production-load evidence. Higher churn, physical NICs, VPN/tunnel
> paths, and wildcard multi-interface sockets remain unmeasured.

- Date (UTC): 2026-11-18T12:15:22+00:00
- Kernel: `7.0.0-43-generic`
- Loader: `/home/amaechi/code/flow/prototype/bpf/flow_tcp_bytes_loader` (interval 1.0 s)
- Payload per protocol: 16777216 bytes (16.000 MiB), loopback soak, 300s, 1.000 MiB per protocol per iteration
- Transfers: TCP then UDP, repeated loopback transfers for 300s with short-lived child churn
- Expected process TGID(s): 86954
- Loader wall time: 304.93 s

## Results by protocol

| Protocol | Payload sent | Payload received | Attributed TX | Attributed RX | TX / sent | RX / received |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| TCP (`tcp_sendmsg`/`tcp_recvmsg`) | 364904448 | 364904448 | 364904448 | 364904448 | 100.0% | 100.0% |
| UDP (`udp_sendmsg`/`udp_recvmsg`) | 364904448 | 364904448 | 364904448 | 364904448 | 100.0% | 100.0% |

- UDP delivered / UDP sent: 100.0% (drops are possible; eBPF RX is compared to delivered bytes).

| Aggregate | Bytes |
| --- | ---: |
| Total attributed TX, expected process | 729808896 |
| Total attributed RX, expected process | 729808896 |
| Other attributed TX (background traffic) | 144850777 |
| Other attributed RX (background traffic) | 145081111 |
| Total attributed TX | 874659673 |
| Total attributed RX | 874890007 |
| Unattributed TX | 0 |
| Unattributed RX | 0 |
| `lo` TX delta (IP/link, both protocols) | 878383867 |
| `lo` RX delta (IP/link, both protocols) | 878383867 |

## Per-process rows (TGID + event-time start_ns + UID + proto + interface)

| TGID | UID | Proto | Interface | Netns | TX | RX | comm | exe | start_ns | cgroup | identity verified | expected process |
| ---: | ---: | --- | --- | ---: | ---: | ---: | --- | --- | ---: | --- | --- | --- |
| 86954 | 0 | udp | lo | 4026531833 | 364904448 | 364904448 | python3 | /usr/bin/python3.14 | 12622661761133 | 18750 | yes | yes |
| 86954 | 0 | tcp | lo | 4026531833 | 364904448 | 364904448 | python3 | /usr/bin/python3.14 | 12622661761133 | 18750 | yes | yes |
| 86972 | 0 | udp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 12628527689171 | 18750 | yes | no |
| 86972 | 0 | tcp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 12628527689171 | 18750 | yes | no |
| 86985 | 0 | tcp | lo | 0 | 1048576 | 1048576 | python3 | - | 12632847750399 | 18750 | yes | no |
| 86985 | 0 | udp | lo | 0 | 1048576 | 1048576 | python3 | - | 12632847750399 | 18750 | yes | no |
| 86999 | 0 | udp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 12637171683690 | 18750 | yes | no |
| 86999 | 0 | tcp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 12637171683690 | 18750 | yes | no |
| 87031 | 0 | tcp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 12641492950309 | 18750 | yes | no |
| 87031 | 0 | udp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 12641492950309 | 18750 | yes | no |
| 87044 | 0 | udp | lo | 0 | 1048576 | 1048576 | python3 | - | 12645811744555 | 18750 | yes | no |
| 87044 | 0 | tcp | lo | 0 | 1048576 | 1048576 | python3 | - | 12645811744555 | 18750 | yes | no |
| 87057 | 0 | tcp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 12650125553981 | 18750 | yes | no |
| 87057 | 0 | udp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 12650125553981 | 18750 | yes | no |
| 87073 | 0 | udp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 12654439104716 | 18750 | yes | no |
| 87073 | 0 | tcp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 12654439104716 | 18750 | yes | no |
| 87089 | 0 | udp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 12658759066525 | 18750 | yes | no |
| 87089 | 0 | tcp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 12658759066525 | 18750 | yes | no |
| 87103 | 0 | udp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 12663078111319 | 18750 | yes | no |
| 87103 | 0 | tcp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 12663078111319 | 18750 | yes | no |
| 87123 | 0 | tcp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 12667399485698 | 18750 | yes | no |
| 87123 | 0 | udp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 12667399485698 | 18750 | yes | no |
| 87144 | 0 | udp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 12671719345499 | 18750 | yes | no |
| 87144 | 0 | tcp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 12671719345499 | 18750 | yes | no |
| 87158 | 0 | udp | lo | 0 | 1048576 | 1048576 | python3 | - | 12676041450681 | 18750 | yes | no |
| 87158 | 0 | tcp | lo | 0 | 1048576 | 1048576 | python3 | - | 12676041450681 | 18750 | yes | no |
| 87175 | 0 | udp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 12680357695711 | 18750 | yes | no |
| 87175 | 0 | tcp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 12680357695711 | 18750 | yes | no |
| 87188 | 0 | tcp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 12684673900588 | 18750 | yes | no |
| 87188 | 0 | udp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 12684673900588 | 18750 | yes | no |
| 87208 | 0 | udp | lo | 0 | 1048576 | 1048576 | python3 | - | 12689005151639 | 18750 | yes | no |
| 87208 | 0 | tcp | lo | 0 | 1048576 | 1048576 | python3 | - | 12689005151639 | 18750 | yes | no |
| 87221 | 0 | tcp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 12693324974528 | 18750 | yes | no |
| 87221 | 0 | udp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 12693324974528 | 18750 | yes | no |
| 87248 | 0 | udp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 12697643885840 | 18750 | yes | no |
| 87248 | 0 | tcp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 12697643885840 | 18750 | yes | no |
| 87261 | 0 | tcp | lo | 0 | 1048576 | 1048576 | python3 | - | 12701964550880 | 18750 | yes | no |
| 87261 | 0 | udp | lo | 0 | 1048576 | 1048576 | python3 | - | 12701964550880 | 18750 | yes | no |
| 87546 | 0 | tcp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 12706276768223 | 18750 | yes | no |
| 87546 | 0 | udp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 12706276768223 | 18750 | yes | no |
| 87561 | 0 | udp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 12710597839697 | 18750 | yes | no |
| 87561 | 0 | tcp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 12710597839697 | 18750 | yes | no |
| 87590 | 0 | tcp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 12714919538420 | 18750 | yes | no |
| 87590 | 0 | udp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 12714919538420 | 18750 | yes | no |
| 87606 | 0 | udp | lo | 0 | 1048576 | 1048576 | python3 | - | 12719233722261 | 18750 | yes | no |
| 87606 | 0 | tcp | lo | 0 | 1048576 | 1048576 | python3 | - | 12719233722261 | 18750 | yes | no |
| 87620 | 0 | udp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 12723555973503 | 18750 | yes | no |
| 87620 | 0 | tcp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 12723555973503 | 18750 | yes | no |
| 87669 | 0 | udp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 12727869431538 | 18750 | yes | no |
| 87669 | 0 | tcp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 12727869431538 | 18750 | yes | no |
| 87734 | 0 | tcp | lo | 0 | 1048576 | 1048576 | python3 | - | 12732181922633 | 18750 | yes | no |
| 87734 | 0 | udp | lo | 0 | 1048576 | 1048576 | python3 | - | 12732181922633 | 18750 | yes | no |
| 87747 | 0 | udp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 12736509562801 | 18750 | yes | no |
| 87747 | 0 | tcp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 12736509562801 | 18750 | yes | no |
| 87763 | 0 | tcp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 12740823123757 | 18750 | yes | no |
| 87763 | 0 | udp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 12740823123757 | 18750 | yes | no |
| 87783 | 0 | udp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 12745140527538 | 18750 | yes | no |
| 87783 | 0 | tcp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 12745140527538 | 18750 | yes | no |
| 87798 | 0 | tcp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 12749460920473 | 18750 | yes | no |
| 87798 | 0 | udp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 12749460920473 | 18750 | yes | no |
| 87811 | 0 | tcp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 12753775462316 | 18750 | yes | no |
| 87811 | 0 | udp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 12753775462316 | 18750 | yes | no |
| 87838 | 0 | udp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 12758093622565 | 18750 | yes | no |
| 87838 | 0 | tcp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 12758093622565 | 18750 | yes | no |
| 87854 | 0 | tcp | lo | 0 | 1048576 | 1048576 | python3 | - | 12762414521440 | 18750 | yes | no |
| 87854 | 0 | udp | lo | 0 | 1048576 | 1048576 | python3 | - | 12762414521440 | 18750 | yes | no |
| 87867 | 0 | tcp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 12766735370538 | 18750 | yes | no |
| 87867 | 0 | udp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 12766735370538 | 18750 | yes | no |
| 87889 | 0 | udp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 12771051982629 | 18750 | yes | no |
| 87889 | 0 | tcp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 12771051982629 | 18750 | yes | no |
| 87902 | 0 | udp | lo | 0 | 1048576 | 1048576 | python3 | - | 12775368408278 | 18750 | yes | no |
| 87902 | 0 | tcp | lo | 0 | 1048576 | 1048576 | python3 | - | 12775368408278 | 18750 | yes | no |
| 87922 | 0 | tcp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 12779687770785 | 18750 | yes | no |
| 87922 | 0 | udp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 12779687770785 | 18750 | yes | no |
| 87941 | 0 | tcp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 12784005110554 | 18750 | yes | no |
| 87941 | 0 | udp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 12784005110554 | 18750 | yes | no |
| 87968 | 0 | udp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 12788321154949 | 18750 | yes | no |
| 87968 | 0 | tcp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 12788321154949 | 18750 | yes | no |
| 87982 | 0 | tcp | lo | 0 | 1048576 | 1048576 | python3 | - | 12792643201914 | 18750 | yes | no |
| 87982 | 0 | udp | lo | 0 | 1048576 | 1048576 | python3 | - | 12792643201914 | 18750 | yes | no |
| 87995 | 0 | tcp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 12796962866292 | 18750 | yes | no |
| 87995 | 0 | udp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 12796962866292 | 18750 | yes | no |
| 88010 | 0 | tcp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 12801276902596 | 18750 | yes | no |
| 88010 | 0 | udp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 12801276902596 | 18750 | yes | no |
| 88023 | 0 | udp | lo | 0 | 1048576 | 1048576 | python3 | - | 12805600711641 | 18750 | yes | no |
| 88023 | 0 | tcp | lo | 0 | 1048576 | 1048576 | python3 | - | 12805600711641 | 18750 | yes | no |
| 88040 | 0 | udp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 12809918988773 | 18750 | yes | no |
| 88040 | 0 | tcp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 12809918988773 | 18750 | yes | no |
| 88060 | 0 | tcp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 12814238379453 | 18750 | yes | no |
| 88060 | 0 | udp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 12814238379453 | 18750 | yes | no |
| 88081 | 0 | tcp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 12818561210104 | 18750 | yes | no |
| 88081 | 0 | udp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 12818561210104 | 18750 | yes | no |
| 88098 | 0 | udp | lo | 0 | 1048576 | 1048576 | python3 | - | 12822879688435 | 18750 | yes | no |
| 88098 | 0 | tcp | lo | 0 | 1048576 | 1048576 | python3 | - | 12822879688435 | 18750 | yes | no |
| 88111 | 0 | tcp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 12827194134883 | 18750 | yes | no |
| 88111 | 0 | udp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 12827194134883 | 18750 | yes | no |
| 88125 | 0 | udp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 12831513487512 | 18750 | yes | no |
| 88125 | 0 | tcp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 12831513487512 | 18750 | yes | no |
| 88140 | 0 | udp | lo | 0 | 1048576 | 1048576 | python3 | - | 12835832346358 | 18750 | yes | no |
| 88140 | 0 | tcp | lo | 0 | 1048576 | 1048576 | python3 | - | 12835832346358 | 18750 | yes | no |
| 88154 | 0 | udp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 12840153226086 | 18750 | yes | no |
| 88154 | 0 | tcp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 12840153226086 | 18750 | yes | no |
| 88167 | 0 | tcp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 12844474401121 | 18750 | yes | no |
| 88167 | 0 | udp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 12844474401121 | 18750 | yes | no |
| 88194 | 0 | udp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 12848796513525 | 18750 | yes | no |
| 88194 | 0 | tcp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 12848796513525 | 18750 | yes | no |
| 88208 | 0 | tcp | lo | 0 | 1048576 | 1048576 | python3 | - | 12853116540981 | 18750 | yes | no |
| 88208 | 0 | udp | lo | 0 | 1048576 | 1048576 | python3 | - | 12853116540981 | 18750 | yes | no |
| 88223 | 0 | udp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 12857435149475 | 18750 | yes | no |
| 88223 | 0 | tcp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 12857435149475 | 18750 | yes | no |
| 88239 | 0 | udp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 12861759298726 | 18750 | yes | no |
| 88239 | 0 | tcp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 12861759298726 | 18750 | yes | no |
| 88255 | 0 | udp | lo | 0 | 1048576 | 1048576 | python3 | - | 12866074563699 | 18750 | yes | no |
| 88255 | 0 | tcp | lo | 0 | 1048576 | 1048576 | python3 | - | 12866074563699 | 18750 | yes | no |
| 88271 | 0 | tcp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 12870389073857 | 18750 | yes | no |
| 88271 | 0 | udp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 12870389073857 | 18750 | yes | no |
| 88284 | 0 | udp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 12874709826741 | 18750 | yes | no |
| 88284 | 0 | tcp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 12874709826741 | 18750 | yes | no |
| 88312 | 0 | udp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 12879030274539 | 18750 | yes | no |
| 88312 | 0 | tcp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 12879030274539 | 18750 | yes | no |
| 88330 | 0 | tcp | lo | 0 | 1048576 | 1048576 | python3 | - | 12883352537098 | 18750 | yes | no |
| 88330 | 0 | udp | lo | 0 | 1048576 | 1048576 | python3 | - | 12883352537098 | 18750 | yes | no |
| 88343 | 0 | tcp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 12887673722006 | 18750 | yes | no |
| 88343 | 0 | udp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 12887673722006 | 18750 | yes | no |
| 88364 | 0 | udp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 12891993232067 | 18750 | yes | no |
| 88364 | 0 | tcp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 12891993232067 | 18750 | yes | no |
| 88378 | 0 | tcp | lo | 0 | 1048576 | 1048576 | python3 | - | 12896310387440 | 18750 | yes | no |
| 88378 | 0 | udp | lo | 0 | 1048576 | 1048576 | python3 | - | 12896310387440 | 18750 | yes | no |
| 88393 | 0 | tcp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 12900628831529 | 18750 | yes | no |
| 88393 | 0 | udp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 12900628831529 | 18750 | yes | no |
| 88421 | 0 | tcp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 12904946799053 | 18750 | yes | no |
| 88421 | 0 | udp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 12904946799053 | 18750 | yes | no |
| 88449 | 0 | udp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 12909266539189 | 18750 | yes | no |
| 88449 | 0 | tcp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 12909266539189 | 18750 | yes | no |
| 88463 | 0 | tcp | lo | 0 | 1048576 | 1048576 | python3 | - | 12913573436644 | 18750 | yes | no |
| 88463 | 0 | udp | lo | 0 | 1048576 | 1048576 | python3 | - | 12913573436644 | 18750 | yes | no |
| 88476 | 0 | tcp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 12917897086691 | 18750 | yes | no |
| 88476 | 0 | udp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 12917897086691 | 18750 | yes | no |
| 88491 | 0 | tcp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 12922220917963 | 18750 | yes | no |
| 88491 | 0 | udp | lo | 4026531833 | 1048576 | 1048576 | python3 | /usr/bin/python3.14 | 12922220917963 | 18750 | yes | no |
| 66102 | 1000 | tcp | wlp0s20f3 | 4026531833 | 115154 | 322109 | msedge | /opt/microsoft/msedge/msedge | 7287130883236 | 22107 | yes | no |
| 66102 | 1000 | udp | wlp0s20f3 | 4026531833 | 14788 | 26351 | msedge | /opt/microsoft/msedge/msedge | 7287130883236 | 22107 | yes | no |
| 6030 | 1000 | tcp | wlp0s20f3 | 4026531833 | 4569 | 8593 | opencode | /home/amaechi/.opencode/bin/opencode | 114950780258 | 18270 | yes | no |
| 488 | 989 | udp | wlp0s20f3 | 4026531833 | 1801 | 9770 | systemd-resolve | /usr/lib/systemd/systemd-resolved | 3909591732 | 2746 | yes | no |
| 488 | 989 | udp | lo | 4026531833 | 4352 | 2530 | systemd-resolve | /usr/lib/systemd/systemd-resolved | 3909591732 | 2746 | yes | no |
| 6019 | 1000 | tcp | wlp0s20f3 | 4026531833 | 2021 | 2295 | opencode | /home/amaechi/.opencode/bin/opencode | 114587790130 | 18270 | yes | no |
| 66102 | 1000 | udp | lo | 4026531833 | 962 | 2879 | msedge | /opt/microsoft/msedge/msedge | 7287130883236 | 22107 | yes | no |
| 1596 | 988 | udp | wlp0s20f3 | 4026531833 | 2436 | 0 | chronyd | /usr/sbin/chronyd | 6638833261 | 6369 | yes | no |
| 87933 | 1000 | tcp | wlp0s20f3 | 0 | 78 | 862 | curl | - | 12783135036339 | 25113 | no | no |
| 88348 | 1000 | tcp | wlp0s20f3 | 0 | 83 | 326 | curl | - | 12887992723024 | 25113 | no | no |
| 88496 | 1000 | tcp | wlp0s20f3 | 0 | 83 | 326 | curl | - | 12922734389218 | 25113 | no | no |
| 6030 | 1000 | tcp | lo | 4026531833 | 360 | 0 | opencode | /home/amaechi/.opencode/bin/opencode | 114950780258 | 18270 | yes | no |
| 6019 | 1000 | tcp | lo | 4026531833 | 0 | 360 | opencode | /home/amaechi/.opencode/bin/opencode | 114587790130 | 18270 | yes | no |
| 87774 | 1000 | tcp | wlp0s20f3 | 4026531833 | 79 | 247 | curl | /usr/bin/curl | 12743843767335 | 25113 | yes | no |
| 87874 | 1000 | tcp | wlp0s20f3 | 4026531833 | 79 | 247 | curl | /usr/bin/curl | 12767719421325 | 25113 | yes | no |
| 6030 | 1000 | udp | lo | 4026531833 | 94 | 182 | opencode | /home/amaechi/.opencode/bin/opencode | 114950780258 | 18270 | yes | no |
| 6019 | 1000 | udp | lo | 4026531833 | 80 | 168 | opencode | /home/amaechi/.opencode/bin/opencode | 114587790130 | 18270 | yes | no |
| 1608 | 988 | udp | lo | 4026531833 | 90 | 134 | chronyd | /usr/sbin/chronyd | 6652639820 | 6369 | yes | no |
| 66041 | 1000 | udp | lo | 4026531833 | 39 | 103 | msedge | /opt/microsoft/msedge/msedge | 7286893625430 | 22151 | yes | no |
| 1369 | 103 | udp | - | 4026531833 | 141 | 0 | avahi-daemon | /usr/sbin/avahi-daemon | 6300592348 | 6245 | yes | no |
| 1369 | 103 | udp | wlp0s20f3 | 4026531833 | 0 | 141 | avahi-daemon | /usr/sbin/avahi-daemon | 6300592348 | 6245 | yes | no |

## Interface attribution

- Expected interface: all expected-process rows on `lo`
- Expected-process resolved interface(s): lo
- Expected-process rows with unresolved interface: 0
- Expected-process unresolved-interface bytes: TX 0, RX 0
- TCP interface name(s): lo (TX: lo; RX: lo)
- UDP interface name(s): lo (TX: lo; RX: lo)

## Soak / map growth observation

- Duration requested: 300 s; wall time: 304.93 s
- Parent iterations: 348
- Short-lived child transfers: 69
- BPF map entries: first 10, last 161, max 161
- CPU samples (s): 0.038183414, 0.044010968, 0.052383723, 0.065454327, 0.074781684, 0.086465681, 0.096977145, 0.111262321, 0.126359166, 0.143604087, 0.157303243, 0.17666691, 0.194632791, 0.210803192, 0.231654484, 0.253281703, 0.280562184, 0.299015011, 0.320566341, 0.34720671, 0.377150801, 0.411941237, 0.439172727, 0.463452584, 0.488809519, 0.517111309, 0.549706573, 0.580402883, 0.617061985, 0.644024885, 0.671483428, 0.701014199, 0.733891615, 0.768287659, 0.809072925, 0.847290539, 0.885010377, 0.915172999, 0.951652416, 0.987822009, 1.023087607, 1.065203769, 1.103865987, 1.137085551, 1.170749184, 1.209846048, 1.251560808, 1.27654927, 1.341921112, 1.390766269, 1.43782804, 1.49226147, 1.54231073, 1.596547948, 1.667543179, 1.731155257, 1.803979952, 1.894828355, 1.894828355
- Peak RSS samples (KiB): 3960, 3960, 3960, 3960, 3960, 3960, 3960, 3960, 3960, 3960, 3960, 3960, 3960, 3960, 3960, 3960, 3960, 3960, 3960, 3960, 3960, 3960, 3960, 3960, 3960, 3960, 3960, 3960, 3960, 3960, 3960, 3960, 3960, 3960, 3960, 3960, 3960, 3960, 3960, 3960, 3960, 3960, 3960, 3960, 3960, 3960, 3960, 3960, 3960, 3960, 3960, 3960, 3960, 3960, 3960, 3960, 3960, 3960, 3960
- The BPF map has no expiry; growth here is an observation, not a claim of bounded steady-state memory.

## Loader overhead (first datapoint)

- CPU time: 1.879 s
- Average CPU: 0.616% of one core during the run
- Peak RSS: 3960 KiB

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
  `lo` counters count IP/link bytes including headers. Ratios near 100% are expected but exact equality
  is not.
- Exactness checks cover only the parent TGID (86954); 69 short-lived child transfer(s) are reported as background rows and are not asserted.
- Background traffic from other processes is reported separately; unresolved `-`
  rows for unrelated background traffic do not affect the expected-process gate.
- UDP datagrams may be dropped by the receiver; the UDP RX comparison uses bytes actually
  delivered to recvfrom(), and dropped bytes are not counted as attributed RX.
- Loopback traffic appears once in each interface direction. Child churn rows may have unverified identity after exit; map growth is reported as observation only, since the BPF map has no expiry.
- This is a controlled sanity check, not a claim of complete attribution coverage.
