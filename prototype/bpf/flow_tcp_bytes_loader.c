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
static int g_tsv = 0;

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

struct prev_entry {
    __u32 tgid;
    __u32 uid;
    __u64 start_ns;
    __u64 tx;
    __u64 rx;
    int used;
    int identity_cached;
    char comm[64];
    char exe[256];
    unsigned long long startticks;
    unsigned long long cgroup_id;
};

struct unattributed_val {
    __u64 tx_bytes;
    __u64 rx_bytes;
    __u64 tx_events;
    __u64 rx_events;
};

#define MAX_PREV 10240
static struct prev_entry prev[MAX_PREV];

static struct prev_entry *find_prev(__u32 tgid, __u32 uid, __u64 start_ns)
{
    for (int i = 0; i < MAX_PREV; i++) {
        if (prev[i].used && prev[i].tgid == tgid && prev[i].uid == uid && prev[i].start_ns == start_ns)
            return &prev[i];
    }
    for (int i = 0; i < MAX_PREV; i++) {
        if (!prev[i].used) {
            memset(&prev[i], 0, sizeof(prev[i]));
            prev[i].used = 1;
            prev[i].tgid = tgid;
            prev[i].uid = uid;
            prev[i].start_ns = start_ns;
            snprintf(prev[i].comm, sizeof(prev[i].comm), "?");
            snprintf(prev[i].exe, sizeof(prev[i].exe), "-");
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

static void tsv_sanitize(char *s)
{
    for (; *s; s++) {
        if (*s == '\t' || *s == '\n' || *s == '\r') *s = ' ';
    }
}

static int startticks_match(__u64 start_ns, unsigned long long st, long clk_tck)
{
    if (start_ns == 0 || st == 0 || clk_tck <= 0)
        return 0;
    unsigned long long ticks = (start_ns * (unsigned long long)clk_tck) / 1000000000ULL;
    unsigned long long diff = ticks > st ? ticks - st : st - ticks;
    return diff <= 2;
}

static long read_clk_tck(void);

static void print_stats(struct flow_tcp_bytes_bpf *skel, int snapshot_idx)
{
    struct traffic_key key = {}, next_key;
    struct traffic_val val;
    int fd = bpf_map__fd(skel->maps.traffic);
    int ufd = bpf_map__fd(skel->maps.unattributed);
    long clk_tck = read_clk_tck();

    if (!g_tsv) {
        printf("\n--- snapshot %d: TCP attribution (cumulative, delta, event-time identity) ---\n", snapshot_idx);
        printf("%-8s %-6s %-14s %-14s %-14s %-14s %-16s %s\n", "TGID", "UID", "TX_TOTAL", "RX_TOTAL", "TX_DELTA", "RX_DELTA", "COMM", "EXE/START/STATE");
    } else {
        printf("S\t%d\n", snapshot_idx);
    }

    while (bpf_map_get_next_key(fd, &key, &next_key) == 0) {
        if (bpf_map_lookup_elem(fd, &next_key, &val) == 0) {
            unsigned long long st = read_proc_startticks(next_key.tgid);
            char bpf_comm[17] = {0};
            memcpy(bpf_comm, val.comm, sizeof(val.comm));
            bpf_comm[sizeof(bpf_comm) - 1] = 0;
            struct prev_entry *pe = find_prev(next_key.tgid, next_key.uid, next_key.start_ns ? next_key.start_ns : st);
            unsigned long long dtx = val.tx_bytes, drx = val.rx_bytes;
            char live_comm[64] = {0}, live_exe[256] = {0};
            int have_live = 0;
            int identity_verified = 0;

            // Only enrich from /proc when the live PID still refers to the same
            // process generation observed at event time. This prevents PID reuse
            // from attaching a different executable to historical counters.
            if (next_key.start_ns != 0) {
                if (startticks_match(next_key.start_ns, st, clk_tck)) {
                    read_proc_comm(next_key.tgid, live_comm, sizeof(live_comm));
                    read_proc_exe(next_key.tgid, live_exe, sizeof(live_exe));
                    have_live = 1;
                    identity_verified = 1;
                }
            } else {
                read_proc_comm(next_key.tgid, live_comm, sizeof(live_comm));
                read_proc_exe(next_key.tgid, live_exe, sizeof(live_exe));
                have_live = live_comm[0] != '?' || (live_exe[0] != '-' && live_exe[0] != 0) || st != 0;
                identity_verified = have_live;
            }

            if (pe) {
                dtx = (val.tx_bytes >= pe->tx) ? (val.tx_bytes - pe->tx) : val.tx_bytes;
                drx = (val.rx_bytes >= pe->rx) ? (val.rx_bytes - pe->rx) : val.rx_bytes;
                pe->tx = val.tx_bytes;
                pe->rx = val.rx_bytes;
                if (identity_verified) {
                    snprintf(pe->comm, sizeof(pe->comm), "%s", live_comm);
                    snprintf(pe->exe, sizeof(pe->exe), "%s", live_exe);
                    pe->startticks = st;
                    pe->identity_cached = 1;
                }
                if (bpf_comm[0] && (pe->comm[0] == '?' || pe->comm[0] == 0))
                    snprintf(pe->comm, sizeof(pe->comm), "%s", bpf_comm);
                pe->cgroup_id = val.cgroup_id;
            }
            const char *comm = pe ? pe->comm : (bpf_comm[0] ? bpf_comm : live_comm);
            const char *exe = pe ? pe->exe : live_exe;
            unsigned long long show_st = pe ? pe->startticks : st;
            unsigned long long show_ns = pe && pe->start_ns ? pe->start_ns : next_key.start_ns;
            unsigned long long show_cgroup = pe ? pe->cgroup_id : val.cgroup_id;
            if (!g_tsv) {
                char extra[360];
                snprintf(extra, sizeof(extra), "%s (start_ns=%llu startticks=%llu cgroup=%llu%s)",
                         exe, show_ns, show_st, show_cgroup,
                         identity_verified ? "" : ", exited/unknown identity");
                printf("%-8u %-6u %-14llu %-14llu %-14llu %-14llu %-16s %s\n",
                       next_key.tgid, next_key.uid,
                       (unsigned long long)val.tx_bytes, (unsigned long long)val.rx_bytes,
                       dtx, drx, comm, extra);
            } else {
                char comm2[64], exe2[256];
                snprintf(comm2, sizeof(comm2), "%s", comm);
                snprintf(exe2, sizeof(exe2), "%s", exe);
                tsv_sanitize(comm2);
                tsv_sanitize(exe2);
                printf("E\t%u\t%u\t%llu\t%llu\t%llu\t%llu\t%s\t%s\t%llu\t%d\t%llu\t%llu\n",
                       next_key.tgid, next_key.uid,
                       (unsigned long long)val.tx_bytes, (unsigned long long)val.rx_bytes,
                       dtx, drx, comm2, exe2, show_st, identity_verified ? 1 : 0,
                       show_ns, show_cgroup);
            }
        }
        key = next_key;
    }

    __u32 uk = 0;
    struct unattributed_val uv = {0};
    static __u64 prev_utx = 0, prev_urx = 0;
    if (bpf_map_lookup_elem(ufd, &uk, &uv) == 0) {
        unsigned long long dutx = (uv.tx_bytes >= prev_utx) ? (uv.tx_bytes - prev_utx) : uv.tx_bytes;
        unsigned long long durx = (uv.rx_bytes >= prev_urx) ? (uv.rx_bytes - prev_urx) : uv.rx_bytes;
        prev_utx = uv.tx_bytes;
        prev_urx = uv.rx_bytes;
        if (!g_tsv) {
            char info[128];
            snprintf(info, sizeof(info), "events tx=%llu rx=%llu", (unsigned long long)uv.tx_events, (unsigned long long)uv.rx_events);
            printf("UNATTRIB %-6s %-14llu %-14llu %-14llu %-14llu %-16s %s\n",
                   "-", (unsigned long long)uv.tx_bytes, (unsigned long long)uv.rx_bytes,
                   dutx, durx, "(no-task)", info);
        } else {
            printf("U\t%llu\t%llu\t%llu\t%llu\t%llu\t%llu\n",
                   (unsigned long long)uv.tx_bytes, (unsigned long long)uv.rx_bytes,
                   dutx, durx,
                   (unsigned long long)uv.tx_events, (unsigned long long)uv.rx_events);
        }
    }
    fflush(stdout);
}

static long read_clk_tck(void)
{
    long v = sysconf(_SC_CLK_TCK);
    return v > 0 ? v : 100;
}

static long long read_boot_unix_ms(void)
{
    FILE *f = fopen("/proc/stat", "r");
    if (!f) return 0;
    char line[256];
    long long btime = 0;
    while (fgets(line, sizeof(line), f)) {
        if (strncmp(line, "btime ", 6) == 0) {
            btime = strtoll(line + 6, NULL, 10);
            break;
        }
    }
    fclose(f);
    return btime * 1000LL;
}

int main(int argc, char **argv)
{
    int interval = 2;
    int rounds = 10;
    if (argc > 1) interval = atoi(argv[1]);
    if (argc > 2) rounds = atoi(argv[2]);
    if (argc > 3 && strcmp(argv[3], "tsv") == 0) g_tsv = 1;
    for (int i = 1; i < argc; i++) if (strcmp(argv[i], "tsv") == 0) g_tsv = 1;
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

    if (!g_tsv) {
        printf("flow_tcp_bytes prototype attached (TX: tcp_sendmsg%s).\n",
               skel->links.tcp_recvmsg_exit ? ", RX: tcp_recvmsg" : "");
        printf("Measurement note: counts bytes returned by TCP send/recv kernel functions (TCP payload bytes at that hook), cumulative per (tgid,uid) while attached. UDP/other protocols and non-TCP paths are NOT counted. Unattributed/context edge cases require further validation.\n");
        printf("Sampling every %d s for %d rounds (Ctrl-C to stop early).\n", interval, rounds);
    } else {
        printf("M\t%ld\t%lld\n", read_clk_tck(), read_boot_unix_ms());
        fflush(stdout);
    }

    for (int i = 1; i <= rounds && !exiting; i++) {
        sleep(interval);
        print_stats(skel, i);
    }

    print_stats(skel, rounds + 1);
    flow_tcp_bytes_bpf__destroy(skel);
    printf("detached.\n");
    return 0;
}
