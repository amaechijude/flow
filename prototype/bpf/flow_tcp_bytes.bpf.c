#include "vmlinux.h"
#include <bpf/bpf_helpers.h>
#include <bpf/bpf_tracing.h>
#include <bpf/bpf_core_read.h>

char LICENSE[] SEC("license") = "Dual BSD/GPL";

#define PROTO_TCP 1
#define PROTO_UDP 2

/*
 * Process identity is captured at event time. Including the thread-group
 * leader's start time in the map key prevents two different processes that
 * reused the same TGID from being merged into one counter row (and therefore
 * one stable Flow identity). comm/cgroup/start-time are captured on first
 * observation, so short-lived processes retain attribution even if they exit
 * before userspace enrichment.
 *
 * ifindex is captured from the socket when it can be resolved (bound device,
 * cached RX route, or cached output route). It is part of the key so traffic
 * over different interfaces is reported separately. ifindex 0 means unknown.
 */
struct traffic_key {
    __u64 start_ns;
    __u32 tgid;
    __u32 uid;
    __u32 ifindex;
    __u32 proto; /* PROTO_TCP or PROTO_UDP */
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

/* Threads execute one recvmsg at a time, so pid_tgid is sufficient to carry
 * the socket pointer from the entry probe to the return probe. */
struct {
    __uint(type, BPF_MAP_TYPE_HASH);
    __uint(max_entries, 10240);
    __type(key, __u64);
    __type(value, __u64);
} recv_args SEC(".maps");

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

static __always_inline __u64 current_process_start_ns(void)
{
    struct task_struct *task = (struct task_struct *)bpf_get_current_task_btf();
    if (!task)
        return 0;

    /* start_time is per-thread; attribution is per process (TGID). */
    struct task_struct *leader = BPF_CORE_READ(task, group_leader);
    if (!leader)
        leader = task;
    return BPF_CORE_READ(leader, start_time);
}

static __always_inline __u32 sock_ifindex(struct sock *sk)
{
    if (!sk)
        return 0;

    int bound = BPF_CORE_READ(sk, __sk_common.skc_bound_dev_if);
    if (bound > 0)
        return (__u32)bound;

    int cached = BPF_CORE_READ(sk, sk_rx_dst_ifindex);
    if (cached > 0)
        return (__u32)cached;

    struct dst_entry *dst = BPF_CORE_READ(sk, sk_dst_cache);
    if (!dst)
        return 0;

    struct net_device *dev = BPF_CORE_READ(dst, dev);
    if (!dev)
        return 0;

    int index = BPF_CORE_READ(dev, ifindex);
    return index > 0 ? (__u32)index : 0;
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

static __always_inline void add_bytes(__u64 bytes, int is_tx, __u32 proto, struct sock *sk)
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
        .start_ns = current_process_start_ns(),
        .tgid = tgid,
        .uid = uid,
        .ifindex = sock_ifindex(sk),
        .proto = proto,
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
        add_bytes((__u64)ret, 1, PROTO_TCP, sk);
    return 0;
}

SEC("kprobe/tcp_recvmsg")
int BPF_KPROBE(tcp_recvmsg_entry, struct sock *sk)
{
    __u64 key = bpf_get_current_pid_tgid();
    __u64 val = (__u64)sk;
    bpf_map_update_elem(&recv_args, &key, &val, BPF_ANY);
    return 0;
}

SEC("kretprobe/tcp_recvmsg")
int BPF_KRETPROBE(tcp_recvmsg_exit, int ret)
{
    __u64 key = bpf_get_current_pid_tgid();
    __u64 *skp = bpf_map_lookup_elem(&recv_args, &key);
    struct sock *sk = skp ? (struct sock *)*skp : NULL;
    bpf_map_delete_elem(&recv_args, &key);

    if (ret > 0)
        add_bytes((__u64)ret, 0, PROTO_TCP, sk);
    return 0;
}

SEC("fexit/udp_sendmsg")
int BPF_PROG(udp_sendmsg_exit, struct sock *sk, struct msghdr *msg, size_t len, int ret)
{
    if (ret > 0)
        add_bytes((__u64)ret, 1, PROTO_UDP, sk);
    return 0;
}

SEC("kprobe/udp_recvmsg")
int BPF_KPROBE(udp_recvmsg_entry, struct sock *sk)
{
    __u64 key = bpf_get_current_pid_tgid();
    __u64 val = (__u64)sk;
    bpf_map_update_elem(&recv_args, &key, &val, BPF_ANY);
    return 0;
}

SEC("kretprobe/udp_recvmsg")
int BPF_KRETPROBE(udp_recvmsg_exit, int ret)
{
    __u64 key = bpf_get_current_pid_tgid();
    __u64 *skp = bpf_map_lookup_elem(&recv_args, &key);
    struct sock *sk = skp ? (struct sock *)*skp : NULL;
    bpf_map_delete_elem(&recv_args, &key);

    if (ret > 0)
        add_bytes((__u64)ret, 0, PROTO_UDP, sk);
    return 0;
}
