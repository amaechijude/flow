#define _GNU_SOURCE
#include <stdio.h>
#include <stdlib.h>
#include <unistd.h>
#include <signal.h>
#include <errno.h>
#include <string.h>
#include <bpf/libbpf.h>
#include <bpf/bpf.h>
#include "flow_tcp_bytes.skel.h"

static volatile sig_atomic_t exiting = 0;
static void on_sig(int sig) { (void)sig; exiting = 1; }

struct traffic_key {
    __u32 tgid;
    __u32 uid;
};

struct traffic_val {
    __u64 tx_bytes;
    __u64 rx_bytes;
};

static void print_stats(struct flow_tcp_bytes_bpf *skel)
{
    struct traffic_key key = {}, next_key;
    struct traffic_val val;
    int fd = bpf_map__fd(skel->maps.traffic);

    printf("\n--- TCP byte attribution snapshot (tgid/uid, cumulative since load) ---\n");
    printf("%-8s %-8s %-16s %-16s\n", "TGID", "UID", "TX_BYTES", "RX_BYTES");
    while (bpf_map_get_next_key(fd, &key, &next_key) == 0) {
        if (bpf_map_lookup_elem(fd, &next_key, &val) == 0) {
            printf("%-8u %-8u %-16llu %-16llu\n", next_key.tgid, next_key.uid,
                   (unsigned long long)val.tx_bytes, (unsigned long long)val.rx_bytes);
        }
        key = next_key;
    }
    fflush(stdout);
}

int main(int argc, char **argv)
{
    int interval = 2;
    int rounds = 10;
    if (argc > 1) interval = atoi(argv[1]);
    if (argc > 2) rounds = atoi(argv[2]);
    if (interval <= 0) interval = 2;
    if (rounds <= 0) rounds = 10;

    libbpf_set_strict_mode(LIBBPF_STRICT_ALL);

    struct flow_tcp_bytes_bpf *skel = flow_tcp_bytes_bpf__open();
    if (!skel) {
        fprintf(stderr, "failed to open BPF skeleton\n");
        return 1;
    }

    int err = flow_tcp_bytes_bpf__load(skel);
    if (err) {
        fprintf(stderr, "failed to load BPF object: %d (%s)\n", err, strerror(-err));
        fprintf(stderr, "Note: loading/attaching fentry programs usually requires elevated privileges (e.g. root/CAP_BPF/CAP_PERFMON/CAP_SYS_ADMIN depending on system).\n");
        flow_tcp_bytes_bpf__destroy(skel);
        return 1;
    }

    skel->links.tcp_sendmsg_exit = bpf_program__attach(skel->progs.tcp_sendmsg_exit);
    if (!skel->links.tcp_sendmsg_exit) {
        err = -errno;
        fprintf(stderr, "failed to attach tcp_sendmsg_exit: %d (%s)\n", err, strerror(-err));
        flow_tcp_bytes_bpf__destroy(skel);
        return 1;
    }
    skel->links.tcp_recvmsg_exit = bpf_program__attach(skel->progs.tcp_recvmsg_exit);
    if (!skel->links.tcp_recvmsg_exit) {
        err = -errno;
        fprintf(stderr, "warning: failed to attach tcp_recvmsg_exit: %d (%s) (continuing with TX-only accounting)\n", err, strerror(-err));
    }

    signal(SIGINT, on_sig);
    signal(SIGTERM, on_sig);

    printf("flow_tcp_bytes prototype attached (TX: tcp_sendmsg%s).\n",
           skel->links.tcp_recvmsg_exit ? ", RX: tcp_recvmsg" : "");
    printf("Measurement note: counts bytes returned by TCP send/recv kernel functions (TCP payload bytes at that hook), cumulative per (tgid,uid) while attached. UDP/other protocols and non-TCP paths are NOT counted. Unattributed/context edge cases require further validation.\n");
    printf("Sampling every %d s for %d rounds (Ctrl-C to stop early).\n", interval, rounds);

    for (int i = 0; i < rounds && !exiting; i++) {
        sleep(interval);
        print_stats(skel);
    }

    print_stats(skel);
    flow_tcp_bytes_bpf__destroy(skel);
    printf("detached.\n");
    return 0;
}
