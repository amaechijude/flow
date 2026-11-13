#define _GNU_SOURCE
#include <stdio.h>
#include <stdlib.h>
#include <unistd.h>
#include <signal.h>
#include <errno.h>
#include <string.h>
#include <dirent.h>
#include <sys/stat.h>
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

struct prev_entry {
    __u32 tgid;
    __u32 uid;
    __u64 tx;
    __u64 rx;
    int used;
};

#define MAX_PREV 4096
static struct prev_entry prev[MAX_PREV];

static struct prev_entry *find_prev(__u32 tgid, __u32 uid)
{
    for (int i = 0; i < MAX_PREV; i++) {
        if (prev[i].used && prev[i].tgid == tgid && prev[i].uid == uid)
            return &prev[i];
    }
    for (int i = 0; i < MAX_PREV; i++) {
        if (!prev[i].used) {
            prev[i].used = 1;
            prev[i].tgid = tgid;
            prev[i].uid = uid;
            prev[i].tx = 0;
            prev[i].rx = 0;
            return &prev[i];
        }
    }
    return NULL;
}

static void read_proc_comm(__u32 tgid, char *buf, size_t buflen)
{
    char path[64];
    snprintf(path, sizeof(path), "/proc/%u/comm", tgid);
    FILE *f = fopen(path, "r");
    if (!f) { snprintf(buf, buflen, "?"); return; }
    if (!fgets(buf, buflen, f)) snprintf(buf, buflen, "?");
    fclose(f);
    size_t n = strlen(buf);
    while (n > 0 && (buf[n-1] == '\n' || buf[n-1] == '\r')) buf[--n] = 0;
}

static void read_proc_exe(__u32 tgid, char *buf, size_t buflen)
{
    char path[64], target[512];
    snprintf(path, sizeof(path), "/proc/%u/exe", tgid);
    ssize_t n = readlink(path, target, sizeof(target)-1);
    if (n < 0) { snprintf(buf, buflen, "-"); return; }
    target[n] = 0;
    snprintf(buf, buflen, "%.*s", (int)(buflen - 1), target);
}

static unsigned long long read_proc_startticks(__u32 tgid)
{
    char path[64], line[1024];
    snprintf(path, sizeof(path), "/proc/%u/stat", tgid);
    FILE *f = fopen(path, "r");
    if (!f) return 0;
    if (!fgets(line, sizeof(line), f)) { fclose(f); return 0; }
    fclose(f);
    char *rparen = strrchr(line, ')');
    if (!rparen) return 0;
    char *p = rparen + 1;
    if (*p == ' ') p++;
    // fields after comm: [0]=state(field3) ... [19]=starttime(field22)
    char *save = NULL;
    int idx = 0;
    for (char *tok = strtok_r(p, " ", &save); tok; tok = strtok_r(NULL, " ", &save), idx++) {
        if (idx == 19) {
            return strtoull(tok, NULL, 10);
        }
    }
    return 0;
}

static void print_stats(struct flow_tcp_bytes_bpf *skel, int snapshot_idx)
{
    struct traffic_key key = {}, next_key;
    struct traffic_val val;
    int fd = bpf_map__fd(skel->maps.traffic);

    printf("\n--- snapshot %d: TCP attribution (cumulative, delta, process info best-effort) ---\n", snapshot_idx);
    printf("%-8s %-6s %-14s %-14s %-14s %-14s %-16s %s\n", "TGID", "UID", "TX_TOTAL", "RX_TOTAL", "TX_DELTA", "RX_DELTA", "COMM", "EXE/STARTTICKS");
    while (bpf_map_get_next_key(fd, &key, &next_key) == 0) {
        if (bpf_map_lookup_elem(fd, &next_key, &val) == 0) {
            struct prev_entry *pe = find_prev(next_key.tgid, next_key.uid);
            unsigned long long dtx = val.tx_bytes, drx = val.rx_bytes;
            if (pe) {
                dtx = (val.tx_bytes >= pe->tx) ? (val.tx_bytes - pe->tx) : val.tx_bytes;
                drx = (val.rx_bytes >= pe->rx) ? (val.rx_bytes - pe->rx) : val.rx_bytes;
                pe->tx = val.tx_bytes;
                pe->rx = val.rx_bytes;
            }
            char comm[64], exe[256];
            read_proc_comm(next_key.tgid, comm, sizeof(comm));
            read_proc_exe(next_key.tgid, exe, sizeof(exe));
            unsigned long long st = read_proc_startticks(next_key.tgid);
            char extra[320];
            snprintf(extra, sizeof(extra), "%s (startticks=%llu)", exe, st);
            printf("%-8u %-6u %-14llu %-14llu %-14llu %-14llu %-16s %s\n",
                   next_key.tgid, next_key.uid,
                   (unsigned long long)val.tx_bytes, (unsigned long long)val.rx_bytes,
                   dtx, drx, comm, extra);
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

    for (int i = 1; i <= rounds && !exiting; i++) {
        sleep(interval);
        print_stats(skel, i);
    }

    print_stats(skel, rounds + 1);
    flow_tcp_bytes_bpf__destroy(skel);
    printf("detached.\n");
    return 0;
}
