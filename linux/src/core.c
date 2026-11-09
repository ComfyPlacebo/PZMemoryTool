/* =============================================================================
 *  PZ Memory Tool - Linux / SteamOS version: core logic
 *  Finding the game, editing -Xmx / -Xms, Steam launch options, processes,
 *  memory detection and the allocation rules. No user interface in here.
 * ============================================================================= */
#define _GNU_SOURCE
#include "pzmt.h"

#include <ctype.h>
#include <dirent.h>
#include <time.h>
#include <errno.h>
#include <fcntl.h>
#include <limits.h>
#include <pwd.h>
#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
#include <sys/types.h>
#include <sys/wait.h>
#include <unistd.h>

/* =============================================================================
 *  Small helpers
 * ============================================================================= */
void buf_add(Buf *b, const char *s, size_t n)
{
    if (b->len + n + 1 > b->cap) {
        size_t c = b->cap ? b->cap : 256;
        while (c < b->len + n + 1) c *= 2;
        b->s = realloc(b->s, c);
        b->cap = c;
    }
    memcpy(b->s + b->len, s, n);
    b->len += n;
    b->s[b->len] = 0;
}
void buf_adds(Buf *b, const char *s) { buf_add(b, s, strlen(s)); }
void buf_addf(Buf *b, const char *fmt, ...)
{
    char tmp[4096];
    va_list ap;
    va_start(ap, fmt);
    int n = vsnprintf(tmp, sizeof tmp, fmt, ap);
    va_end(ap);
    if (n > 0) buf_add(b, tmp, (size_t)(n < (int)sizeof tmp ? n : (int)sizeof tmp - 1));
}
void buf_free(Buf *b) { free(b->s); b->s = NULL; b->len = b->cap = 0; }

char *read_file(const char *path, size_t *len_out)
{
    FILE *f = fopen(path, "rb");
    if (!f) return NULL;
    Buf b = {0};
    char chunk[65536];
    size_t n;
    while ((n = fread(chunk, 1, sizeof chunk, f)) > 0) buf_add(&b, chunk, n);
    fclose(f);
    if (!b.s) { b.s = calloc(1, 1); }
    if (len_out) *len_out = b.len;
    return b.s;
}

/* Writes via a temporary file + rename, so a crash never leaves half a file. */
bool write_file(const char *path, const char *data, size_t len)
{
    char tmp[PATH_MAX];
    snprintf(tmp, sizeof tmp, "%s.pzmt-tmp", path);
    FILE *f = fopen(tmp, "wb");
    if (!f) return false;
    bool ok = fwrite(data, 1, len, f) == len;
    ok = (fclose(f) == 0) && ok;
    if (ok) {
        struct stat st;
        if (stat(path, &st) == 0) chmod(tmp, st.st_mode & 07777);  /* keep permissions */
        ok = rename(tmp, path) == 0;
    }
    if (!ok) unlink(tmp);
    return ok;
}

bool copy_file(const char *from, const char *to)
{
    size_t n;
    char *d = read_file(from, &n);
    if (!d) return false;
    FILE *f = fopen(to, "wb");
    if (!f) { free(d); return false; }
    bool ok = fwrite(d, 1, n, f) == n;
    ok = (fclose(f) == 0) && ok;
    free(d);
    return ok;
}

bool file_exists(const char *p) { struct stat st; return stat(p, &st) == 0 && S_ISREG(st.st_mode); }
bool dir_exists(const char *p)  { struct stat st; return stat(p, &st) == 0 && S_ISDIR(st.st_mode); }

bool mkdir_p(const char *path)
{
    char tmp[PATH_MAX];
    snprintf(tmp, sizeof tmp, "%s", path);
    for (char *p = tmp + 1; *p; p++) {
        if (*p == '/') { *p = 0; mkdir(tmp, 0755); *p = '/'; }
    }
    return mkdir(tmp, 0755) == 0 || errno == EEXIST;
}

const char *home_dir(void)
{
    const char *h = getenv("HOME");
    if (h && *h) return h;
    struct passwd *pw = getpwuid(getuid());
    return pw ? pw->pw_dir : "/";
}

void path_join(char *out, size_t n, const char *a, const char *b)
{
    size_t la = strlen(a);
    if (la && a[la - 1] == '/') snprintf(out, n, "%s%s", a, b);
    else snprintf(out, n, "%s/%s", a, b);
}

bool command_exists(const char *name)
{
    const char *path = getenv("PATH");
    if (!path) path = "/usr/local/bin:/usr/bin:/bin";
    char *copy = strdup(path), *save = NULL;
    bool found = false;
    for (char *dir = strtok_r(copy, ":", &save); dir; dir = strtok_r(NULL, ":", &save)) {
        char full[PATH_MAX];
        path_join(full, sizeof full, dir, name);
        if (access(full, X_OK) == 0) { found = true; break; }
    }
    free(copy);
    return found;
}

static void child_quiet(void)
{
    int fd = open("/dev/null", O_RDWR);
    if (fd >= 0) { dup2(fd, 0); dup2(fd, 1); dup2(fd, 2); if (fd > 2) close(fd); }
}

void spawn_detached(const char *const argv[])
{
    pid_t pid = fork();
    if (pid == 0) {
        setsid();
        if (fork() == 0) {               /* double fork: the grandchild is not our child */
            child_quiet();
            execvp(argv[0], (char *const *)argv);
            _exit(127);
        }
        _exit(0);
    }
    if (pid > 0) waitpid(pid, NULL, 0);
}

int run_wait(const char *const argv[])
{
    pid_t pid = fork();
    if (pid == 0) { child_quiet(); execvp(argv[0], (char *const *)argv); _exit(127); }
    if (pid < 0) return -1;
    int st = 0;
    waitpid(pid, &st, 0);
    return WIFEXITED(st) ? WEXITSTATUS(st) : -1;
}

char *run_capture(const char *const argv[])
{
    int fds[2];
    if (pipe(fds) != 0) return NULL;
    pid_t pid = fork();
    if (pid == 0) {
        int fd = open("/dev/null", O_RDWR);
        if (fd >= 0) { dup2(fd, 0); dup2(fd, 2); }
        dup2(fds[1], 1);
        close(fds[0]); close(fds[1]);
        execvp(argv[0], (char *const *)argv);
        _exit(127);
    }
    close(fds[1]);
    if (pid < 0) { close(fds[0]); return NULL; }
    Buf b = {0};
    char tmp[4096];
    ssize_t n;
    while ((n = read(fds[0], tmp, sizeof tmp)) > 0) buf_add(&b, tmp, (size_t)n);
    close(fds[0]);
    int st = 0;
    waitpid(pid, &st, 0);
    if (!(WIFEXITED(st) && WEXITSTATUS(st) == 0)) { buf_free(&b); return NULL; }
    if (!b.s) b.s = calloc(1, 1);
    while (b.len && (b.s[b.len - 1] == '\n' || b.s[b.len - 1] == '\r')) b.s[--b.len] = 0;
    return b.s;
}

char *self_path(void)
{
    /* An AppImage-style launcher or symlink would still resolve to the real file. */
    char buf[PATH_MAX];
    ssize_t n = readlink("/proc/self/exe", buf, sizeof buf - 1);
    if (n <= 0) return strdup("PZMemoryTool");
    buf[n] = 0;
    return strdup(buf);
}

const char *describe_gb(long mb, char *out, size_t n)
{
    double gb = mb / 1024.0;
    if (gb == (long)gb) snprintf(out, n, "%ld GB", (long)gb);
    else snprintf(out, n, "%.1f GB", gb);
    return out;
}

/* =============================================================================
 *  Game file: reading / writing -Xmx (maximum) and -Xms (minimum)
 *  Same behaviour as XmxParser in the Windows version.
 * ============================================================================= */

/* Finds the first "-Xmx<digits><unit>" (flag 'x') or "-Xms..." (flag 's'). */
MemArg find_mem_arg(const char *text, char flag)
{
    MemArg r = {0};
    char pat[5] = { '-', 'X', 'm', flag, 0 };
    for (const char *p = text; (p = strstr(p, pat)) != NULL; p++) {
        const char *d = p + 4;
        if (!isdigit((unsigned char)*d)) continue;
        long n = 0;
        while (isdigit((unsigned char)*d)) { n = n * 10 + (*d - '0'); d++; }
        char unit = *d;
        long mb;
        switch (tolower((unsigned char)unit)) {
            case 'g': mb = n * 1024; d++; break;
            case 'm': mb = n; d++; break;
            case 'k': mb = n / 1024; d++; break;
            default:  mb = n / (1024 * 1024); break;   /* plain bytes */
        }
        r.found = true;
        r.pos = (size_t)(p - text);
        r.len = (size_t)(d - p);
        r.mb = mb;
        return r;
    }
    return r;
}

char *str_splice(const char *text, size_t at, size_t remove, const char *insert)
{
    size_t tl = strlen(text), il = strlen(insert);
    char *out = malloc(tl - remove + il + 1);
    memcpy(out, text, at);
    memcpy(out + at, insert, il);
    memcpy(out + at + il, text + at + remove, tl - at - remove + 1);
    return out;
}

/* Position of the quoted entry "-Xmx..." / "-Xms..." (the opening quote), or -1. */
static long quoted_entry(const char *text, char flag, size_t *end_out)
{
    char pat[6] = { '"', '-', 'X', 'm', flag, 0 };
    for (const char *p = text; (p = strstr(p, pat)) != NULL; p++) {
        const char *d = p + 5;
        if (!isdigit((unsigned char)*d)) continue;
        while (isdigit((unsigned char)*d)) d++;
        if (strchr("kKmMgG", *d) && *d) d++;
        if (*d != '"') continue;
        if (end_out) *end_out = (size_t)(d + 1 - text);
        return (long)(p - text);
    }
    return -1;
}

/* Removes the -Xms entry from the vmArgs list, keeping the list valid JSON. */
static char *remove_xms(const char *text)
{
    size_t e;
    long q = quoted_entry(text, 's', &e);
    if (q < 0) return strdup(text);
    return json_remove_entry(text, q, e);
}

/* Removes the quoted list entry at q (opening quote) .. e (after closing quote), keeping valid JSON. */
char *json_remove_entry(const char *text, long q, size_t e)
{

    size_t ls = (size_t)q;
    while (ls > 0 && text[ls - 1] != '\n') ls--;
    bool ws_before = true;
    for (size_t i = ls; i < (size_t)q; i++) if (text[i] != ' ' && text[i] != '\t') ws_before = false;

    size_t a = e;
    while (text[a] == ' ' || text[a] == '\t') a++;
    bool comma_after = text[a] == ',';
    if (comma_after) a++;
    while (text[a] == ' ' || text[a] == '\t') a++;
    size_t line_end = a;
    if (text[line_end] == '\r') line_end++;
    bool at_eol = text[line_end] == '\n';

    if (ws_before && at_eol) {
        /* the entry is alone on its line: remove the whole line */
        char *out = str_splice(text, ls, line_end + 1 - ls, "");
        if (!comma_after) {
            /* it was the last entry: drop the comma after the entry before it */
            long c = (long)ls - 1;
            while (c >= 0 && strchr(" \t\r\n", out[c])) c--;
            if (c >= 0 && out[c] == ',') { char *o2 = str_splice(out, (size_t)c, 1, ""); free(out); out = o2; }
        }
        return out;
    }
    /* the entry shares a line with other entries: remove it and one neighbouring comma */
    if (comma_after) return str_splice(text, (size_t)q, a - (size_t)q, "");
    long c = q - 1;
    while (c >= 0 && (text[c] == ' ' || text[c] == '\t')) c--;
    if (c >= 0 && text[c] == ',') return str_splice(text, (size_t)c, e - (size_t)c, "");
    return str_splice(text, (size_t)q, e - (size_t)q, "");
}

/* Sets -Xmx and -Xms. -Xms is always written directly BEFORE -Xmx: on its own line
 * if -Xmx is on its own line, otherwise on the same line. xms_gb 0 = no -Xms. */
char *set_memory(const char *text, int xmx_gb, int xms_gb)
{
    char xmx[32], xms[32];
    snprintf(xmx, sizeof xmx, "-Xmx%dm", xmx_gb * 1024);
    snprintf(xms, sizeof xms, "-Xms%dm", xms_gb * 1024);

    MemArg m = find_mem_arg(text, 'x');
    char *t1 = m.found ? str_splice(text, m.pos, m.len, xmx) : strdup(text);
    char *t2 = remove_xms(t1);
    free(t1);
    if (xms_gb <= 0) return t2;

    size_t e;
    long q = quoted_entry(t2, 'x', &e);
    if (q < 0) return t2;   /* -Xmx not in quotes: can't safely add -Xms (Save reports it) */

    size_t ls = (size_t)q;
    while (ls > 0 && t2[ls - 1] != '\n') ls--;
    char indent[256] = "";
    bool own_line = true;
    size_t il = 0;
    for (size_t i = ls; i < (size_t)q; i++) {
        if (t2[i] != ' ' && t2[i] != '\t') own_line = false;
        if (il < sizeof indent - 1) indent[il++] = t2[i];
    }
    indent[il] = 0;
    const char *nl = strstr(t2, "\r\n") ? "\r\n" : "\n";

    Buf ins = {0};
    buf_addf(&ins, "\"%s\"", xms);
    if (own_line) { buf_adds(&ins, ","); buf_adds(&ins, nl); buf_adds(&ins, indent); }
    else buf_adds(&ins, ", ");
    char *out = str_splice(t2, (size_t)q, 0, ins.s);
    buf_free(&ins);
    free(t2);
    return out;
}

/* "Correct file" check: right name, Project Zomboid main class, vmArgs and an -Xmx argument. */
bool is_valid_pz_json(const char *path)
{
    const char *base = strrchr(path, '/');
    base = base ? base + 1 : path;
    if (strcasecmp(base, JSON_NAME) != 0) return false;
    struct stat st;
    if (stat(path, &st) != 0 || !S_ISREG(st.st_mode) || st.st_size > 1024 * 1024) return false;
    char *t = read_file(path, NULL);
    if (!t) return false;
    bool ok = strstr(t, "zombie/gameStates/MainScreenState") && strstr(t, "\"vmArgs\"") &&
              find_mem_arg(t, 'x').found;
    free(t);
    return ok;
}

/* =============================================================================
 *  Steam's KeyValues (.vdf) text files
 * ============================================================================= */
typedef struct { const char *key; size_t key_len; const char *val; size_t val_len; int depth; } VdfPair;

/* Walks a .vdf file. Calls cb for every "key" "value" pair with the section path. */
typedef bool (*VdfCb)(const char **path, int depth, const char *key, size_t kl,
                      const char *val, size_t vl, size_t val_pos, void *ud);
static void vdf_walk(const char *t, VdfCb cb, void *ud)
{
    const char *path[32];
    char keybuf[32][128];
    int depth = 0;
    const char *pending = NULL; size_t pending_len = 0;
    size_t i = 0, n = strlen(t);
    while (i < n) {
        char c = t[i];
        if (isspace((unsigned char)c)) { i++; continue; }
        if (c == '/' && i + 1 < n && t[i + 1] == '/') { while (i < n && t[i] != '\n') i++; continue; }
        if (c == '{') {
            if (depth < 32) {
                size_t kl = pending ? (pending_len < 127 ? pending_len : 127) : 0;
                if (kl) memcpy(keybuf[depth], pending, kl);
                keybuf[depth][kl] = 0;
                path[depth] = keybuf[depth];
            }
            depth++; pending = NULL; i++; continue;
        }
        if (c == '}') { if (depth > 0) depth--; pending = NULL; i++; continue; }
        if (c == '"') {
            size_t start = ++i;
            while (i < n && t[i] != '"') { if (t[i] == '\\') i++; i++; }
            size_t len = (i < n ? i : n) - start;
            i++;
            if (!pending) { pending = t + start; pending_len = len; continue; }
            if (cb && !cb(path, depth < 32 ? depth : 32, pending, pending_len, t + start, len, start, ud)) return;
            pending = NULL;
            continue;
        }
        i++;
    }
}

static char *unescape_vdf(const char *s, size_t n)
{
    char *o = malloc(n + 1);
    size_t j = 0;
    for (size_t i = 0; i < n; i++) {
        if (s[i] == '\\' && i + 1 < n) { i++; }
        o[j++] = s[i];
    }
    o[j] = 0;
    return o;
}

/* ---- Project Zomboid's LaunchOptions in localconfig.vdf ---------------------- */
typedef struct { size_t pos, len; bool found; } LoLoc;
static bool lo_cb(const char **path, int depth, const char *key, size_t kl,
                  const char *val, size_t vl, size_t val_pos, void *ud)
{
    (void)val;
    LoLoc *loc = ud;
    if (kl == 13 && strncasecmp(key, "LaunchOptions", 13) == 0 && depth >= 5) {
        const char *want[5] = { "Software", "Valve", "Steam", "apps", STEAM_APP_ID };
        int off = depth - 5;
        for (int k = 0; k < 5; k++) if (strcasecmp(path[off + k], want[k]) != 0) return true;
        loc->pos = val_pos; loc->len = vl; loc->found = true;
        return false;
    }
    return true;
}
static LoLoc locate_launch_options(const char *text)
{
    LoLoc loc = {0};
    vdf_walk(text, lo_cb, &loc);
    return loc;
}

char *steam_launch_options(const char *config_text)
{
    LoLoc l = locate_launch_options(config_text);
    if (!l.found) return NULL;
    char *o = malloc(l.len + 1);
    memcpy(o, config_text + l.pos, l.len);
    o[l.len] = 0;
    return o;
}

static bool is_mem_token(const char *s, size_t n)
{
    if (n < 5 || s[0] != '-' || s[1] != 'X' || s[2] != 'm' || (s[3] != 'x' && s[3] != 's')) return false;
    size_t i = 4;
    if (!isdigit((unsigned char)s[i])) return false;
    while (i < n && isdigit((unsigned char)s[i])) i++;
    if (i < n && strchr("kKmMgG", s[i])) i++;
    return i == n;
}

char *memory_args(const char *lo)
{
    Buf b = {0};
    const char *p = lo;
    while (*p) {
        while (*p && isspace((unsigned char)*p)) p++;
        const char *s = p;
        while (*p && !isspace((unsigned char)*p)) p++;
        if (p > s && is_mem_token(s, (size_t)(p - s))) {
            if (b.len) buf_adds(&b, " ");
            buf_add(&b, s, (size_t)(p - s));
        }
    }
    if (!b.s) b.s = calloc(1, 1);
    return b.s;
}

char *without_memory_args(const char *lo)
{
    Buf b = {0};
    const char *p = lo;
    while (*p) {
        while (*p && isspace((unsigned char)*p)) p++;
        const char *s = p;
        while (*p && !isspace((unsigned char)*p)) p++;
        if (p > s && !is_mem_token(s, (size_t)(p - s))) {
            if (b.len) buf_adds(&b, " ");
            buf_add(&b, s, (size_t)(p - s));
        }
    }
    if (!b.s) b.s = calloc(1, 1);
    if (strcmp(b.s, "--") == 0) b.s[0] = 0;   /* only the "end of Java options" marker was left */
    return b.s;
}

/* ---- Steam folders, account, config ------------------------------------------ */
int steam_roots(char roots[][1024], int max)
{
    const char *h = home_dir();
    const char *cands[] = {
        "/.local/share/Steam", "/.steam/steam", "/.steam/root", "/.steam/debian-installation",
        "/.var/app/com.valvesoftware.Steam/.local/share/Steam",   /* Flatpak Steam */
        "/snap/steam/common/.local/share/Steam",
    };
    int n = 0;
    for (size_t i = 0; i < sizeof cands / sizeof *cands && n < max; i++) {
        char p[PATH_MAX], real[PATH_MAX], sa[PATH_MAX];
        snprintf(p, sizeof p, "%s%s", h, cands[i]);
        if (!realpath(p, real)) continue;
        path_join(sa, sizeof sa, real, "steamapps");
        if (!dir_exists(sa)) continue;
        bool dup = false;
        for (int k = 0; k < n; k++) if (strcmp(roots[k], real) == 0) dup = true;
        if (!dup) snprintf(roots[n++], 1024, "%s", real);
    }
    return n;
}

typedef struct { char libs[32][1024]; int n; } LibList;
static bool lib_cb(const char **path, int depth, const char *key, size_t kl,
                   const char *val, size_t vl, size_t val_pos, void *ud)
{
    (void)path; (void)depth; (void)val_pos;
    LibList *L = ud;
    bool digits = kl > 0;
    for (size_t i = 0; i < kl; i++) if (!isdigit((unsigned char)key[i])) digits = false;
    if ((kl == 4 && strncmp(key, "path", 4) == 0) || digits) {
        char *v = unescape_vdf(val, vl);
        if (v[0] == '/' && dir_exists(v) && L->n < 32) {
            bool dup = false;
            for (int k = 0; k < L->n; k++) if (strcmp(L->libs[k], v) == 0) dup = true;
            if (!dup) snprintf(L->libs[L->n++], 1024, "%s", v);
        }
        free(v);
    }
    return true;
}

typedef struct { char value[512]; const char *want; } ValFind;
static bool val_cb(const char **path, int depth, const char *key, size_t kl,
                   const char *val, size_t vl, size_t val_pos, void *ud)
{
    (void)path; (void)depth; (void)val_pos;
    ValFind *f = ud;
    if (strlen(f->want) == kl && strncasecmp(key, f->want, kl) == 0) {
        char *v = unescape_vdf(val, vl);
        snprintf(f->value, sizeof f->value, "%s", v);
        free(v);
        return false;
    }
    return true;
}

/* Every Steam library folder (default Steam folders + libraryfolders.vdf). */
int steam_libraries(char libs[][1024], int max)
{
    char roots[MAX_STEAM_ROOTS][1024];
    int nr = steam_roots(roots, MAX_STEAM_ROOTS);
    LibList L = {0};
    for (int r = 0; r < nr; r++) {
        bool dup = false;
        for (int k = 0; k < L.n; k++) if (!strcmp(L.libs[k], roots[r])) dup = true;
        if (!dup && L.n < 32) snprintf(L.libs[L.n++], 1024, "%s", roots[r]);
        char vdf[PATH_MAX];
        snprintf(vdf, sizeof vdf, "%s/steamapps/libraryfolders.vdf", roots[r]);
        char *t = read_file(vdf, NULL);
        if (t) { vdf_walk(t, lib_cb, &L); free(t); }
    }
    int n = 0;
    for (int i = 0; i < L.n && n < max; i++) snprintf(libs[n++], 1024, "%s", L.libs[i]);
    return n;
}

/* Checks <dir>/ProjectZomboid64.json and <dir>/projectzomboid/ProjectZomboid64.json */
static char *json_in_game_dir(const char *dir)
{
    const char *subs[] = { "", "/projectzomboid" };
    for (int k = 0; k < 2; k++) {
        char cand[PATH_MAX];
        snprintf(cand, sizeof cand, "%s%s/%s", dir, subs[k], JSON_NAME);
        bool ok = is_valid_pz_json(cand);
        pzlog("  checking %s: %s", cand, ok ? "FOUND" : file_exists(cand) ? "exists but not valid" : "not there");
        if (ok) return strdup(cand);
    }
    return NULL;
}

/* Default Steam folders, every Steam library, and Project Zomboid's app manifest. */
char *find_game_json(void)
{
    char libs[32][1024];
    int n = steam_libraries(libs, 32);
    pzlog("Looking for the game in %d Steam librar%s", n, n == 1 ? "y" : "ies");
    for (int i = 0; i < n; i++) {
        char manifest[PATH_MAX], dir[PATH_MAX];
        pzlog(" library: %s", libs[i]);
        snprintf(manifest, sizeof manifest, "%s/steamapps/appmanifest_%s.acf", libs[i], STEAM_APP_ID);
        char *t = read_file(manifest, NULL);
        if (t) {
            ValFind f = { .want = "installdir" };
            vdf_walk(t, val_cb, &f);
            free(t);
            pzlog("  app manifest found, installdir = \"%s\"", f.value);
            if (f.value[0]) {
                snprintf(dir, sizeof dir, "%s/steamapps/common/%s", libs[i], f.value);
                char *r = json_in_game_dir(dir);
                if (r) return r;
            }
        }
        snprintf(dir, sizeof dir, "%s/steamapps/common/ProjectZomboid", libs[i]);
        char *r = json_in_game_dir(dir);
        if (r) return r;
    }
    pzlog("Game file not found in any Steam library");
    return NULL;
}

/* Signed-in account: Steam's ActiveUser, otherwise the most recent sign-in. */
/* loginusers.vdf: every account, with its MostRecent flag and last sign-in time */
typedef struct { char id[16][32]; int most_recent[16]; long long ts[16]; int n; } LoginList;
static bool login_cb(const char **path, int depth, const char *key, size_t kl,
                     const char *val, size_t vl, size_t val_pos, void *ud)
{
    (void)val_pos;
    LoginList *L = ud;
    if (depth < 2) return true;
    const char *uid = path[depth - 1];
    if (strlen(uid) != 17) return true;
    int k;
    for (k = 0; k < L->n; k++) if (!strcmp(L->id[k], uid)) break;
    if (k == L->n) { if (L->n >= 16) return true; snprintf(L->id[L->n++], 32, "%s", uid); }
    char v[32] = "";
    snprintf(v, sizeof v, "%.*s", (int)(vl < 31 ? vl : 31), val);
    if (kl == 10 && !strncasecmp(key, "MostRecent", 10)) L->most_recent[k] = atoi(v);
    if (kl == 9 && !strncasecmp(key, "Timestamp", 9)) L->ts[k] = atoll(v);
    return true;
}

/* registry.vdf can hold more than one "ActiveUser" (e.g. an empty one and the real one) */
typedef struct { long best; char all[256]; } ActiveFind;
static bool active_cb(const char **path, int depth, const char *key, size_t kl,
                      const char *val, size_t vl, size_t val_pos, void *ud)
{
    (void)path; (void)depth; (void)val_pos;
    ActiveFind *f = ud;
    if (kl == 10 && !strncasecmp(key, "ActiveUser", 10)) {
        char v[32];
        snprintf(v, sizeof v, "%.*s", (int)(vl < 31 ? vl : 31), val);
        size_t l = strlen(f->all);
        snprintf(f->all + l, sizeof f->all - l, "%s\"%s\"", l ? ", " : "", v);
        if (!f->best && atol(v) > 0) f->best = atol(v);
    }
    return true;
}

/* Signed-in account: Steam's ActiveUser, then loginusers.vdf, then the newest userdata folder. */
long steam_account_id(void)
{
    char reg[PATH_MAX];
    snprintf(reg, sizeof reg, "%s/.steam/registry.vdf", home_dir());
    char *t = read_file(reg, NULL);
    if (t) {
        ActiveFind f = {0};
        vdf_walk(t, active_cb, &f);
        free(t);
        pzlog("Steam registry.vdf ActiveUser values: %s", f.all[0] ? f.all : "(none)");
        if (f.best > 0) return f.best;
    } else pzlog("Steam registry.vdf: not found at %s", reg);

    char roots[MAX_STEAM_ROOTS][1024];
    int nr = steam_roots(roots, MAX_STEAM_ROOTS);
    for (int r = 0; r < nr; r++) {
        char p[PATH_MAX];
        snprintf(p, sizeof p, "%s/config/loginusers.vdf", roots[r]);
        char *lt = read_file(p, NULL);
        if (!lt) { pzlog("loginusers.vdf: not found at %s", p); continue; }
        LoginList L = {0};
        vdf_walk(lt, login_cb, &L);
        free(lt);
        int pick = -1;
        for (int k = 0; k < L.n; k++) {
            pzlog("  loginusers.vdf account %s: MostRecent %d, Timestamp %lld", L.id[k], L.most_recent[k], L.ts[k]);
            if (L.most_recent[k] == 1 && pick < 0) pick = k;
        }
        if (pick < 0)   /* nobody marked MostRecent: the latest sign-in */
            for (int k = 0; k < L.n; k++) if (pick < 0 || L.ts[k] > L.ts[pick]) pick = k;
        if (pick >= 0) {
            pzlog("Steam account from loginusers.vdf: %s", L.id[pick]);
            return (long)(atoll(L.id[pick]) - 76561197960265728LL);
        }
    }

    /* last resort: the account folder whose settings changed most recently */
    long best = 0; time_t best_t = 0;
    for (int r = 0; r < nr; r++) {
        char ud[PATH_MAX];
        snprintf(ud, sizeof ud, "%s/userdata", roots[r]);
        DIR *d = opendir(ud);
        if (!d) continue;
        struct dirent *e;
        while ((e = readdir(d))) {
            long id = atol(e->d_name);
            if (id <= 0) continue;
            char lc[PATH_MAX];
            snprintf(lc, sizeof lc, "%s/%s/config/localconfig.vdf", ud, e->d_name);
            struct stat st;
            if (stat(lc, &st) != 0) continue;
            pzlog("  userdata account %ld: localconfig.vdf changed %ld", id, (long)st.st_mtime);
            if (st.st_mtime > best_t) { best_t = st.st_mtime; best = id; }
        }
        closedir(d);
    }
    if (best) pzlog("Steam account from the newest userdata folder: %ld", best);
    return best;
}

bool steam_config_file(char *out, size_t n)
{
    long id = steam_account_id();
    if (id <= 0) { pzlog("No signed-in Steam account found"); return false; }
    char roots[MAX_STEAM_ROOTS][1024];
    int nr = steam_roots(roots, MAX_STEAM_ROOTS);
    for (int r = 0; r < nr; r++) {
        snprintf(out, n, "%s/userdata/%ld/config/localconfig.vdf", roots[r], id);
        pzlog("  Steam settings %s: %s", out, file_exists(out) ? "found" : "not there");
        if (file_exists(out)) return true;
    }
    return false;
}

static void backup_path(const char *cfg, char *out, size_t n) { snprintf(out, n, "%s.pzmemorytool.bak", cfg); }

char *steam_backed_up_value(const char *cfg)
{
    char b[PATH_MAX];
    backup_path(cfg, b, sizeof b);
    char *t = read_file(b, NULL);
    if (!t) return NULL;
    char *v = steam_launch_options(t);
    free(t);
    return v;
}

/* Removes -Xmx / -Xms from the account's launch options. Backs up localconfig.vdf first. */
bool steam_remove_memory_args(const char *cfg, char *backup_out, size_t n)
{
    char *text = read_file(cfg, NULL);                  /* re-read: Steam may have rewritten it */
    if (!text) return false;
    LoLoc l = locate_launch_options(text);
    if (!l.found) { free(text); return false; }
    char *cur = malloc(l.len + 1);
    memcpy(cur, text + l.pos, l.len); cur[l.len] = 0;
    char *mem = memory_args(cur);
    bool ok = false;
    if (mem[0]) {
        char b[PATH_MAX];
        backup_path(cfg, b, sizeof b);
        char *old = steam_backed_up_value(cfg);
        char *oldmem = old ? memory_args(old) : NULL;
        if (!file_exists(b) || !oldmem || !oldmem[0])   /* keep the copy that has the user's originals */
            copy_file(cfg, b);
        free(old); free(oldmem);
        char *clean = without_memory_args(cur);
        char *updated = str_splice(text, l.pos, l.len, clean);
        ok = write_file(cfg, updated, strlen(updated));
        free(clean); free(updated);
        if (backup_out) snprintf(backup_out, n, "%s", b);
    }
    free(mem); free(cur); free(text);
    return ok;
}

/* Puts back only Project Zomboid's launch options from the backup. */
bool steam_restore_from_backup(const char *cfg)
{
    char *old = steam_backed_up_value(cfg);
    if (!old) return false;
    char *text = read_file(cfg, NULL);
    bool ok = false;
    if (text) {
        LoLoc l = locate_launch_options(text);
        if (l.found && !(l.len == strlen(old) && strncmp(text + l.pos, old, l.len) == 0)) {
            char *updated = str_splice(text, l.pos, l.len, old);
            ok = write_file(cfg, updated, strlen(updated));
            free(updated);
        }
        free(text);
    }
    free(old);
    return ok;
}

/* Position just after the "{" of Software/Valve/Steam/apps/108600 in localconfig.vdf, or -1. */
static long find_app_section(const char *t)
{
    char path[32][128];
    int depth = 0;
    const char *pending = NULL; size_t pl = 0;
    size_t i = 0, n = strlen(t);
    while (i < n) {
        char c = t[i];
        if (isspace((unsigned char)c)) { i++; continue; }
        if (c == '/' && t[i + 1] == '/') { while (i < n && t[i] != '\n') i++; continue; }
        if (c == '{') {
            if (depth < 32) { size_t k = pending ? (pl < 127 ? pl : 127) : 0; memcpy(path[depth], pending ? pending : "", k); path[depth][k] = 0; }
            depth++;
            if (depth >= 5 && depth <= 32 && !strcasecmp(path[depth - 1], STEAM_APP_ID) && !strcasecmp(path[depth - 2], "apps") &&
                !strcasecmp(path[depth - 3], "Steam") && !strcasecmp(path[depth - 4], "Valve") && !strcasecmp(path[depth - 5], "Software"))
                return (long)i + 1;
            pending = NULL; i++; continue;
        }
        if (c == '}') { if (depth > 0) depth--; pending = NULL; i++; continue; }
        if (c == '"') {
            size_t st = ++i;
            while (i < n && t[i] != '"') { if (t[i] == '\\') i++; i++; }
            if (!pending) { pending = t + st; pl = i - st; } else pending = NULL;
            i++;
            continue;
        }
        i++;
    }
    return -1;
}

/* Changes Project Zomboid's launch options for the signed-in account. edit() gets the current
 * value and returns the new one (malloc'd). Backs up localconfig.vdf to <cfg><backup_suffix> once.
 * Returns 1 changed, 0 nothing to change, -1 error. */
int steam_edit_launch_options(const char *cfg, char *(*edit)(const char *cur, void *ud), void *ud,
                              const char *backup_suffix, char *backup_out, size_t bn)
{
    char *text = read_file(cfg, NULL);
    if (!text) { pzlog("steam_edit_launch_options: cannot read %s", cfg); return -1; }
    LoLoc l = locate_launch_options(text);
    char *cur;
    if (l.found) { cur = malloc(l.len + 1); memcpy(cur, text + l.pos, l.len); cur[l.len] = 0; }
    else cur = strdup("");
    char *nv = edit(cur, ud);
    pzlog("Steam launch options: \"%s\" -> \"%s\"%s", cur, nv, l.found ? "" : " (no LaunchOptions entry yet)");
    int rc = 0;
    if (strcmp(cur, nv) != 0) {
        char *updated = NULL;
        if (l.found) updated = str_splice(text, l.pos, l.len, nv);
        else {
            long at = find_app_section(text);
            if (at >= 0) {
                /* indent like the line holding the section's "{" plus one tab */
                long ls = at - 1;
                while (ls > 0 && text[ls - 1] != '\n') ls--;
                char indent[64] = ""; int k = 0;
                for (long j = ls; j < at - 1 && k < 62 && (text[j] == '\t' || text[j] == ' '); j++) indent[k++] = text[j];
                indent[k++] = '\t'; indent[k] = 0;
                Buf ins = {0};
                buf_addf(&ins, "\n%s\"LaunchOptions\"\t\t\"%s\"", indent, nv);
                updated = str_splice(text, (size_t)at, 0, ins.s);
                buf_free(&ins);
            } else pzlog("Project Zomboid (108600) section not found in %s", cfg);
        }
        if (updated) {
            char b[PATH_MAX];
            snprintf(b, sizeof b, "%s%s", cfg, backup_suffix);
            if (!file_exists(b)) { bool ok = copy_file(cfg, b); pzlog("Backup %s: %s", b, ok ? "made" : "FAILED"); }
            if (backup_out) snprintf(backup_out, bn, "%s", b);
            rc = write_file(cfg, updated, strlen(updated)) ? 1 : -1;
            pzlog("Writing %s: %s", cfg, rc == 1 ? "OK" : "FAILED");
            free(updated);
        } else rc = -1;
    }
    free(cur); free(nv); free(text);
    return rc;
}

/* Starts Steam again (after the tool closed it). */
void steam_start(void)
{
    char flat[PATH_MAX];
    snprintf(flat, sizeof flat, "%s/.var/app/com.valvesoftware.Steam", home_dir());
    if (command_exists("steam")) { const char *a[] = { "steam", NULL }; spawn_detached(a); pzlog("Started Steam (steam)"); return; }
    if (dir_exists(flat) && command_exists("flatpak")) {
        const char *a[] = { "flatpak", "run", "com.valvesoftware.Steam", NULL }; spawn_detached(a); pzlog("Started Steam (flatpak)"); return;
    }
    const char *a[] = { "xdg-open", "steam://open/main", NULL };
    spawn_detached(a);
    pzlog("Started Steam (xdg-open steam://open/main)");
}

/* Opens a steam:// link in the Steam client (falls back to the web browser). */
void steam_open_url(const char *url)
{
    if (command_exists("steam")) { const char *a[] = { "steam", url, NULL }; spawn_detached(a); }
    else { const char *a[] = { "xdg-open", url, NULL }; spawn_detached(a); }
    pzlog("Opening %s", url);
}

void steam_request_shutdown(void)
{
    pzlog("Asking Steam to close");
    if (command_exists("steam")) {
        const char *a[] = { "steam", "-shutdown", NULL };
        spawn_detached(a);
        return;
    }
    char flat[PATH_MAX];
    snprintf(flat, sizeof flat, "%s/.var/app/com.valvesoftware.Steam", home_dir());
    if (dir_exists(flat) && command_exists("flatpak")) {
        const char *a[] = { "flatpak", "run", "com.valvesoftware.Steam", "-shutdown", NULL };
        spawn_detached(a);
        return;
    }
    const char *a[] = { "xdg-open", "steam://exit", NULL };
    spawn_detached(a);
}

/* SteamOS ships a helper that adds a program to Steam (used by "Add to Steam" in the file manager). */
bool steam_add_to_steam_supported(void) { return command_exists("steamos-add-to-steam"); }
bool steam_add_to_steam(void)
{
    char *me = self_path();
    const char *a[] = { "steamos-add-to-steam", me, NULL };
    int rc = run_wait(a);
    pzlog("steamos-add-to-steam %s -> exit code %d", me, rc);
    free(me);
    return rc == 0;
}

/* =============================================================================
 *  Processes and Game Mode
 * ============================================================================= */
static bool any_process(bool (*match)(const char *comm, const char *argv0, const char *cmdline))
{
    DIR *d = opendir("/proc");
    if (!d) return false;
    struct dirent *e;
    bool found = false;
    pid_t me = getpid();
    while (!found && (e = readdir(d))) {
        if (!isdigit((unsigned char)e->d_name[0])) continue;
        if (atoi(e->d_name) == me) continue;
        char p[64], comm[64] = "", cmd[4096] = "";
        snprintf(p, sizeof p, "/proc/%s/comm", e->d_name);
        FILE *f = fopen(p, "r");
        if (f) { if (fgets(comm, sizeof comm, f)) comm[strcspn(comm, "\n")] = 0; fclose(f); }
        snprintf(p, sizeof p, "/proc/%s/cmdline", e->d_name);
        f = fopen(p, "r");
        size_t n = 0;
        if (f) { n = fread(cmd, 1, sizeof cmd - 1, f); fclose(f); }
        cmd[n] = 0;
        const char *argv0 = strrchr(cmd, '/');
        argv0 = argv0 ? argv0 + 1 : cmd;
        for (size_t i = 0; i < n; i++) if (cmd[i] == 0) cmd[i] = ' ';   /* whole command line */
        if (match(comm, argv0, cmd)) found = true;
    }
    closedir(d);
    return found;
}

static bool match_game(const char *comm, const char *argv0, const char *cmd)
{
    /* Linux shortens process names to 15 characters ("ProjectZomboid6") */
    if (strncmp(comm, "ProjectZomboid", 14) == 0) return true;
    if (strcmp(argv0, GAME_PROCESS_NAME) == 0 || strncmp(argv0, "ProjectZomboid64", 16) == 0) return true;
    if (strstr(cmd, "zombie.gameStates.MainScreenState") || strstr(cmd, "zombie/gameStates/MainScreenState")) return true;
    return false;
}
static bool match_steam(const char *comm, const char *argv0, const char *cmd)
{
    (void)argv0; (void)cmd;
    return strcmp(comm, "steam") == 0;
}
bool is_game_running(void)  { return any_process(match_game); }
bool is_steam_running(void) { return any_process(match_steam); }

/* Game Mode on SteamOS / Bazzite and similar runs inside the "gamescope" session. */
bool in_game_mode(void)
{
    const char *xdg = getenv("XDG_CURRENT_DESKTOP");
    if (xdg && strcasestr(xdg, "gamescope")) return true;
    if (getenv("GAMESCOPE_WAYLAND_DISPLAY")) return true;
    if (getenv("SteamGamepadUI")) return true;
    const char *forced = getenv("PZMT_GAME_MODE");
    return forced && forced[0] == '1';
}

bool can_switch_session(void) { return command_exists("steamos-session-select"); }
bool switch_to_desktop_mode(void)
{
    pzlog("Switching to Desktop Mode (steamos-session-select plasma)");
    const char *a[] = { "steamos-session-select", "plasma", NULL };
    spawn_detached(a);
    return true;
}
bool switch_to_game_mode(void)
{
    pzlog("Switching to Game Mode (steamos-session-select gamescope)");
    if (command_exists("steamos-session-select")) {
        const char *a[] = { "steamos-session-select", "gamescope", NULL };
        spawn_detached(a);
        return true;
    }
    return false;
}

/* =============================================================================
 *  Memory detection and the allocation rules
 * ============================================================================= */
static int round_up_installed(double gb)
{
    static const int sizes[] = { 2, 4, 6, 8, 12, 16, 24, 32, 48, 64, 96, 128, 192, 256, 384, 512 };
    for (size_t i = 0; i < sizeof sizes / sizeof *sizes; i++)
        if (gb <= sizes[i] + 0.001) return sizes[i];
    return (int)(gb + 0.999);
}

static void read_line(const char *path, char *out, size_t n)
{
    out[0] = 0;
    FILE *f = fopen(path, "r");
    if (!f) return;
    if (fgets(out, (int)n, f)) out[strcspn(out, "\n")] = 0;
    fclose(f);
}

/* Steam Deck graphics reserve ("UMA Frame Buffer Size") in GB, read from the graphics driver. */
static int read_uma_gb(void)
{
    DIR *d = opendir("/sys/class/drm");
    if (!d) return 0;
    struct dirent *e;
    long long best = 0;
    while ((e = readdir(d))) {
        if (strncmp(e->d_name, "card", 4) != 0 || strchr(e->d_name, '-')) continue;
        char p[512], v[64];
        snprintf(p, sizeof p, "/sys/class/drm/%s/device/mem_info_vram_total", e->d_name);
        read_line(p, v, sizeof v);
        long long b = atoll(v);
        if (b > best) best = b;
    }
    closedir(d);
    if (best <= 0) return 0;
    return (int)((best + (512LL << 20)) >> 30);    /* round to whole GB */
}

MemInfo read_memory_info(void)
{
    MemInfo mi = {0};
    const char *fake = getenv("PZMT_FAKE_MEMTOTAL_KB");      /* testing only */
    long long kb = fake ? atoll(fake) : 0;
    if (!kb) {
        FILE *f = fopen("/proc/meminfo", "r");
        char line[256];
        while (f && fgets(line, sizeof line, f))
            if (sscanf(line, "MemTotal: %lld kB", &kb) == 1) break;
        if (f) fclose(f);
    }
    mi.usable_gb = kb / 1048576.0;

    char vendor[128], product[128];
    read_line("/sys/class/dmi/id/sys_vendor", vendor, sizeof vendor);
    read_line("/sys/class/dmi/id/product_name", product, sizeof product);
    const char *fv = getenv("PZMT_FAKE_VENDOR"), *fp = getenv("PZMT_FAKE_PRODUCT");
    if (fv) snprintf(vendor, sizeof vendor, "%s", fv);
    if (fp) snprintf(product, sizeof product, "%s", fp);

    mi.kind = HW_PC;
    if (strcasestr(vendor, "Valve")) {
        /* "Jupiter" = Steam Deck LCD, "Galileo" = Steam Deck OLED; any other Valve PC = Steam Machine */
        if (strcasestr(product, "Jupiter") || strcasestr(product, "Galileo")) mi.kind = HW_STEAM_DECK;
        else mi.kind = HW_STEAM_MACHINE;
    }

    double counted = mi.usable_gb;
    if (mi.kind == HW_STEAM_DECK) {
        const char *fu = getenv("PZMT_FAKE_UMA_GB");
        mi.uma_gb = fu ? atoi(fu) : read_uma_gb();
        if (mi.uma_gb < 1) mi.uma_gb = 1;
        counted += mi.uma_gb;                /* the Deck's graphics reserve is part of its 16 GB */
    }
    mi.installed_gb = round_up_installed(counted);
    pzlog("Memory: MemTotal %.2f GB, DMI vendor \"%s\" product \"%s\" -> %s, UMA %d GB, installed (rounded) %d GB",
          mi.usable_gb, vendor, product, mi.kind == HW_STEAM_DECK ? "Steam Deck" : mi.kind == HW_STEAM_MACHINE ? "Steam Machine" : "PC",
          mi.uma_gb, mi.installed_gb);
    return mi;
}

static void add_range(Plan *p, int from, int to)
{
    for (int g = from; g <= to && p->count < 64; g++) p->options[p->count++] = g;
}

Plan make_plan(const MemInfo *mi)
{
    Plan p = {0};
    p.xms = XMS_NORMAL;
    int inst = mi->installed_gb;

    if (mi->kind == HW_STEAM_DECK) {
        /* Steam Deck: graphics share the same 16 GB, so the rules follow the UMA buffer */
        p.xms = XMS_HIDDEN;
        p.tier = 100 + mi->uma_gb;
        if (mi->uma_gb <= 1)      { add_range(&p, 8, 10); p.recommended = 8; }
        else if (mi->uma_gb == 2) { add_range(&p, 8, 9);  p.recommended = 8; }
        else if (mi->uma_gb == 3) { add_range(&p, 7, 8);  p.recommended = 8; }
        else                      { add_range(&p, 6, 8);  p.recommended = 7; }
        snprintf(p.recommend_text, sizeof p.recommend_text, "%d GB", p.recommended);
        return p;
    }

    if (mi->kind == HW_STEAM_MACHINE && inst >= 16) {
        p.tier = 200;
        p.recommended = 8;
        if (inst <= 16) add_range(&p, STEAM_MACHINE_MIN_GB, 10);
        else add_range(&p, STEAM_MACHINE_MIN_GB, inst - LINUX_RESERVE_GB);
        snprintf(p.recommend_text, sizeof p.recommend_text, "8 GB");
        return p;
    }

    int fixed_max = inst - FIXED_TIER_RESERVE;
    int cap = inst - LINUX_RESERVE_GB;
    if (inst >= 32) {
        p.tier = 32; p.recommended = 8;
        snprintf(p.recommend_text, sizeof p.recommend_text, "8 to 12 GB");
        add_range(&p, MIN_ALLOC_GB, cap);
    } else if (inst >= 16) {
        p.tier = 16; p.recommended = 8;
        snprintf(p.recommend_text, sizeof p.recommend_text, "8 GB");
        add_range(&p, MIN_ALLOC_GB, cap);
    } else if (inst >= 12) {
        p.tier = 12; p.recommended = 6;
        snprintf(p.recommend_text, sizeof p.recommend_text, "6 GB or 8 GB only");
        if (6 <= fixed_max) p.options[p.count++] = 6;
        if (8 <= fixed_max) p.options[p.count++] = 8;
    } else if (inst >= 8) {
        p.tier = 8; p.recommended = 5; p.xms = XMS_LOCKED_OFF;
        snprintf(p.recommend_text, sizeof p.recommend_text, "5 GB or 6 GB only");
        if (5 <= fixed_max) p.options[p.count++] = 5;
        if (6 <= fixed_max) p.options[p.count++] = 6;
    } else {
        p.tier = 0; p.xms = XMS_HIDDEN;
        snprintf(p.recommend_text, sizeof p.recommend_text, "none (under 8 GB)");
    }
    p.locked = (p.tier == 0 || p.count == 0);
    return p;
}

/* Warning (red) and note (accent, italic) text for the current choice. */
void plan_messages(const Plan *p, const MemInfo *mi, int sel, char *w, size_t wn, char *n, size_t nn)
{
    w[0] = n[0] = 0;
    if (mi->kind == HW_STEAM_DECK) {
        if (mi->uma_gb >= 4)
            snprintf(w, wn, "Your UMA buffer (the memory your Steam Deck sets aside for graphics, "
                            "\"UMA Frame Buffer Size\" in the BIOS) is %d GB. Project Zomboid doesn't need "
                            "that much. Setting it back to 1 GB gives the game %d GB more.",
                     mi->uma_gb, mi->uma_gb - 1);
        else if (mi->uma_gb == 3)
            snprintf(w, wn, "Your UMA buffer (the memory your Steam Deck sets aside for graphics, "
                            "\"UMA Frame Buffer Size\" in the BIOS) is 3 GB. Project Zomboid doesn't need "
                            "that much; at 1 GB you'd have more memory for the game. If you keep it higher "
                            "for other games, 8 GB still works.");
        if (mi->uma_gb >= 4 && sel >= 8)
            snprintf(n, nn, "At 8 GB only about 0.5 GB is left for everything else, so the game may "
                            "stutter or freeze. Choose 7 GB, or lower the UMA buffer to 1-2 GB.");
        else if (mi->uma_gb <= 2 && sel >= 9)
            snprintf(n, nn, "Graphics share this memory. In Desktop Mode or docked, 8-9 GB is safer. "
                            "If you notice stutter, go back to 8.");
        return;
    }
    if (p->tier == 200) return;   /* Steam Machine: no extra notes */
    switch (p->tier) {
        case 32:
            if (sel > 10) snprintf(w, wn, "Advanced users only: allocations above 10 GB are intended for advanced users.%s",
                                   sel > 12 ? " This is also above the recommended range of 8 to 12 GB." : "");
            break;
        case 16:
            if (sel > 8) snprintf(w, wn, "Setting more than 8 GB on a 16 GB system is not recommended.");
            break;
        case 12:
            snprintf(w, wn, "WARNING: You should consider upgrading to 16 GB or more of total installed memory.");
            snprintf(n, nn, "Try 6 GB first. If that doesn't work, try 8 GB.");
            break;
        case 8:
            snprintf(w, wn, "WARNING: 8 GB of RAM on heavily modified servers is not recommended. "
                            "It is recommended to upgrade to at least 16 GB of total installed memory.");
            snprintf(n, nn, "Try 5 GB first. If that doesn't work, try 6 GB.");
            break;
        default:
            snprintf(w, wn, "WARNING: This PC has less than 8 GB of total installed memory. "
                            "It is not advised to attempt to play on heavily modified servers. "
                            "The memory allocation cannot be changed with this tool.");
            break;
    }
}

/* =============================================================================
 *  Settings file and the pending Game Mode fix
 * ============================================================================= */
void config_dir(char *out, size_t n)
{
    const char *x = getenv("XDG_CONFIG_HOME");
    if (x && *x) snprintf(out, n, "%s/PZMemoryTool", x);
    else snprintf(out, n, "%s/.config/PZMemoryTool", home_dir());
}

char *settings_get(const char *key)
{
    char dir[PATH_MAX], p[PATH_MAX];
    config_dir(dir, sizeof dir);
    path_join(p, sizeof p, dir, "settings.ini");
    char *t = read_file(p, NULL);
    if (!t) return NULL;
    char *res = NULL, *save = NULL;
    for (char *line = strtok_r(t, "\n", &save); line; line = strtok_r(NULL, "\n", &save)) {
        char *eq = strchr(line, '=');
        if (!eq) continue;
        *eq = 0;
        if (strcmp(line, key) == 0) { res = strdup(eq + 1); break; }
    }
    free(t);
    return res;
}

void settings_set(const char *key, const char *value)
{
    char dir[PATH_MAX], p[PATH_MAX];
    config_dir(dir, sizeof dir);
    mkdir_p(dir);
    path_join(p, sizeof p, dir, "settings.ini");
    char *t = read_file(p, NULL);
    Buf out = {0};
    if (t) {
        char *save = NULL;
        for (char *line = strtok_r(t, "\n", &save); line; line = strtok_r(NULL, "\n", &save)) {
            size_t kl = strlen(key);
            if (strncmp(line, key, kl) == 0 && line[kl] == '=') continue;
            buf_adds(&out, line); buf_adds(&out, "\n");
        }
        free(t);
    }
    buf_addf(&out, "%s=%s\n", key, value);
    write_file(p, out.s, out.len);
    buf_free(&out);
}

static void autostart_path(char *out, size_t n)
{
    const char *x = getenv("XDG_CONFIG_HOME");
    if (x && *x) snprintf(out, n, "%s/autostart/pzmemorytool-finish-steam-fix.desktop", x);
    else snprintf(out, n, "%s/.config/autostart/pzmemorytool-finish-steam-fix.desktop", home_dir());
}
static void pending_path(char *out, size_t n)
{
    char dir[PATH_MAX];
    config_dir(dir, sizeof dir);
    snprintf(out, n, "%s/pending-steam-fix", dir);
}

/* Game Mode "Fix it for me": open this tool once, automatically, in Desktop Mode. */
bool pending_fix_write(const char *action)
{
    char dir[PATH_MAX], p[PATH_MAX], a[PATH_MAX];
    config_dir(dir, sizeof dir);
    mkdir_p(dir);
    pending_path(p, sizeof p);
    if (!write_file(p, action, strlen(action))) return false;
    autostart_path(a, sizeof a);
    char adir[PATH_MAX];
    snprintf(adir, sizeof adir, "%s", a);
    char *slash = strrchr(adir, '/');
    if (slash) { *slash = 0; mkdir_p(adir); }
    char *me = self_path();
    Buf d = {0};
    buf_addf(&d, "[Desktop Entry]\nType=Application\nName=PZ Memory Tool (finish Steam fix)\n"
                 "Comment=Runs once to finish clearing Project Zomboid's Steam launch options\n"
                 "Exec=\"%s\" --finish-steam-fix\nX-KDE-autostart-after=panel\nTerminal=false\n", me);
    bool ok = write_file(a, d.s, d.len);
    buf_free(&d);
    free(me);
    return ok;
}
char *pending_fix_read(void)
{
    char p[PATH_MAX];
    pending_path(p, sizeof p);
    char *t = read_file(p, NULL);
    if (t) { size_t l = strlen(t); while (l && (t[l - 1] == '\n' || t[l - 1] == '\r')) t[--l] = 0; }
    return t;
}
bool pending_fix_exists(void) { char p[PATH_MAX]; pending_path(p, sizeof p); return file_exists(p); }
void pending_fix_clear(void)
{
    char p[PATH_MAX];
    pending_path(p, sizeof p);
    unlink(p);
    autostart_path(p, sizeof p);
    unlink(p);
}
