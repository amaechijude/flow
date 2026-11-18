#include "vmlinux.h"
#include <bpf/bpf_helpers.h>
#include <bpf/bpf_tracing.h>
#include <bpf/bpf_core_read.h>

char LICENSE[] SEC("license") = "Dual BSD/GPL";

/*
 * Process identity is captured at event time. Including the task start time in
 * the map key prevents two different processes that reused the same TGID from
 * being merged into one counter row (and therefore one stable Flow identity).
 * comm/cgroup/start-time are captured on first observation, so short-lived
 * processes retain attribution even if they exit before userspace enrichment.
 */
struct traffic_key {
    __u64 start_ns;
    __u32 tgid;
    __u32 uid;
};

struct traffic_val {
    __u64 tx_bytes;
    __u64 rx_bytes;
    __u64 cgroup_id;
    char comm[16];
};

struct {
    __uint(type, BPF_MAP_TYPE_HASH);
    __uint(max_entries, 10240);
    __type(key, struct traffic_key);
    __type(value, struct traffic_val);
} traffic SEC(".maps");

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

static __always_inline __u64 current_start_ns(void)
{
    struct task_struct *task = (struct task_struct *)bpf_get_current_task_btf();
    if (!task)
        return 0;
    return BPF_CORE_READ(task, start_time);
}

static __always_inline struct traffic_val *get_or_create(struct traffic_key *key)
{
    struct traffic_val *v = bpf_map_lookup_elem(&traffic, key);
    if (v)
        return v;

    struct traffic_val init = {};
    init.cgroup_id = bpf_get_current_cgroup_id();
    bpf_get_current_comm(init.comm, sizeof(init.comm));

    bpf_map_update_elem(&traffic, key, &init, BPF_NOEXIST);
    return bpf_map_lookup_elem(&traffic, key);
}

static __always_inline void add_bytes(__u64 bytes, int is_tx)
{
    if (bytes == 0 || (__s64)bytes < 0)
        return;

    __u64 pid_tgid = bpf_get_current_pid_tgid();
    __u32 tgid = pid_tgid >> 32;
    __u32 uid = bpf_get_current_uid_gid();
    if (tgid == 0) {
        if (is_tx)
            add_unattributed_tx(bytes);
        else
            add_unattributed_rx(bytes);
        return;
    }

    struct traffic_key k = {
        .start_ns = current_start_ns(),
        .tgid = tgid,
        .uid = uid,
    };
    struct traffic_val *v = get_or_create(&k);
    if (!v)
        return;

    if (is_tx)
        __sync_fetch_and_add(&v->tx_bytes, bytes);
    else
        __sync_fetch_and_add(&v->rx_bytes, bytes);
}

SEC("fexit/tcp_sendmsg")
int BPF_PROG(tcp_sendmsg_exit, struct sock *sk, struct msghdr *msg, size_t size, int ret)
{
    if (ret > 0)
        add_bytes((__u64)ret, 1);
    return 0;
}

SEC("kretprobe/tcp_recvmsg")
int BPF_KRETPROBE(tcp_recvmsg_exit, int ret)
{
    if (ret > 0)
        add_bytes((__u64)ret, 0);
    return 0;
}
