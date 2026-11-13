#include "vmlinux.h"
#include <bpf/bpf_helpers.h>
#include <bpf/bpf_tracing.h>
#include <bpf/bpf_core_read.h>

char LICENSE[] SEC("license") = "Dual BSD/GPL";

struct traffic_key {
    __u32 tgid;
    __u32 uid;
};

struct traffic_val {
    __u64 tx_bytes;
    __u64 rx_bytes;
};

struct {
    __uint(type, BPF_MAP_TYPE_HASH);
    __uint(max_entries, 10240);
    __type(key, struct traffic_key);
    __type(value, struct traffic_val);
} traffic SEC(".maps");

struct {
    __uint(type, BPF_MAP_TYPE_HASH);
    __uint(max_entries, 10240);
    __type(key, __u32);
    __type(value, struct traffic_val);
} traffic_by_pid SEC(".maps");

struct unattributed_val {
    __u64 tx_bytes;
    __u64 rx_bytes;
    __u64 tx_events;
    __u64 rx_events;
};

struct {
    __uint(type, BPF_MAP_TYPE_ARRAY);
    __uint(max_entries, 1);
    __type(key, __u32);
    __type(value, struct unattributed_val);
} unattributed SEC(".maps");

static __always_inline void add_unattributed_tx(__u64 bytes)
{
    __u32 k = 0;
    struct unattributed_val *u = bpf_map_lookup_elem(&unattributed, &k);
    if (u) {
        __sync_fetch_and_add(&u->tx_bytes, bytes);
        __sync_fetch_and_add(&u->tx_events, 1);
    }
}

static __always_inline void add_unattributed_rx(__u64 bytes)
{
    __u32 k = 0;
    struct unattributed_val *u = bpf_map_lookup_elem(&unattributed, &k);
    if (u) {
        __sync_fetch_and_add(&u->rx_bytes, bytes);
        __sync_fetch_and_add(&u->rx_events, 1);
    }
}

static __always_inline void add_tx(__u64 bytes)
{
    if (bytes == 0 || (__s64)bytes < 0)
        return;
    __u64 pid_tgid = bpf_get_current_pid_tgid();
    __u32 tgid = pid_tgid >> 32;
    __u32 pid = (__u32)pid_tgid;
    __u32 uid = bpf_get_current_uid_gid();
    if (tgid == 0) {
        add_unattributed_tx(bytes);
        return;
    }

    struct traffic_key k = {.tgid = tgid, .uid = uid};
    struct traffic_val zero = {};
    struct traffic_val *v = bpf_map_lookup_elem(&traffic, &k);
    if (!v) {
        bpf_map_update_elem(&traffic, &k, &zero, BPF_NOEXIST);
        v = bpf_map_lookup_elem(&traffic, &k);
    }
    if (v)
        __sync_fetch_and_add(&v->tx_bytes, bytes);

    struct traffic_val *vp = bpf_map_lookup_elem(&traffic_by_pid, &pid);
    if (!vp) {
        bpf_map_update_elem(&traffic_by_pid, &pid, &zero, BPF_NOEXIST);
        vp = bpf_map_lookup_elem(&traffic_by_pid, &pid);
    }
    if (vp)
        __sync_fetch_and_add(&vp->tx_bytes, bytes);
}

static __always_inline void add_rx(__u64 bytes)
{
    if (bytes == 0 || (__s64)bytes < 0)
        return;
    __u64 pid_tgid = bpf_get_current_pid_tgid();
    __u32 tgid = pid_tgid >> 32;
    __u32 pid = (__u32)pid_tgid;
    __u32 uid = bpf_get_current_uid_gid();
    if (tgid == 0) {
        add_unattributed_rx(bytes);
        return;
    }

    struct traffic_key k = {.tgid = tgid, .uid = uid};
    struct traffic_val zero = {};
    struct traffic_val *v = bpf_map_lookup_elem(&traffic, &k);
    if (!v) {
        bpf_map_update_elem(&traffic, &k, &zero, BPF_NOEXIST);
        v = bpf_map_lookup_elem(&traffic, &k);
    }
    if (v)
        __sync_fetch_and_add(&v->rx_bytes, bytes);

    struct traffic_val *vp = bpf_map_lookup_elem(&traffic_by_pid, &pid);
    if (!vp) {
        bpf_map_update_elem(&traffic_by_pid, &pid, &zero, BPF_NOEXIST);
        vp = bpf_map_lookup_elem(&traffic_by_pid, &pid);
    }
    if (vp)
        __sync_fetch_and_add(&vp->rx_bytes, bytes);
}

SEC("fexit/tcp_sendmsg")
int BPF_PROG(tcp_sendmsg_exit, struct sock *sk, struct msghdr *msg, size_t size, int ret)
{
    if (ret > 0)
        add_tx((__u64)ret);
    return 0;
}

SEC("kretprobe/tcp_recvmsg")
int BPF_KRETPROBE(tcp_recvmsg_exit, int ret)
{
    if (ret > 0)
        add_rx((__u64)ret);
    return 0;
}
