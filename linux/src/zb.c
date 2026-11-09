/* =============================================================================
 *  PZ Memory Tool - Linux / SteamOS version: ZombieBuddy install / uninstall
 *
 *  ZombieBuddy (by Zed, https://github.com/zed-0xff/ZombieBuddy, MIT licence)
 *  lets Project Zomboid load Java mods. This does what its Windows installer
 *  does, the Linux way (see ZombieBuddy's doc/Installation.md):
 *    - copies ZombieBuddy.jar from the Steam Workshop download
 *      (.../workshop/content/108600/3619862853/mods/ZombieBuddy/libs/) next to
 *      ProjectZomboid64.json
 *    - adds -javaagent:ZombieBuddy.jar to the game file's vmArgs and / or to
 *      Steam's launch options ("-javaagent:ZombieBuddy.jar --")
 *  ZombieBuddy then keeps itself up to date from the Workshop copy.
 * ============================================================================= */
#define _GNU_SOURCE
#include "pzmt.h"
#include "raylib.h"          /* DecompressData(), to read the version inside the .jar */

#include <ctype.h>
#include <ftw.h>
#include <limits.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
#include <unistd.h>

/* ---- Version from the jar's META-INF/MANIFEST.MF ("Implementation-Version") ---- */
static unsigned rd16(const unsigned char *p) { return p[0] | (p[1] << 8); }
static unsigned rd32(const unsigned char *p) { return p[0] | (p[1] << 8) | (p[2] << 16) | ((unsigned)p[3] << 24); }

bool jar_version(const char *jar, char *out, size_t n)
{
    out[0] = 0;
    size_t len = 0;
    unsigned char *z = (unsigned char *)read_file(jar, &len);
    if (!z || len < 22) { free(z); return false; }
    /* end of central directory record */
    long eocd = -1;
    for (long i = (long)len - 22; i >= 0 && i >= (long)len - 65557; i--)
        if (rd32(z + i) == 0x06054b50) { eocd = i; break; }
    bool ok = false;
    if (eocd >= 0) {
        unsigned count = rd16(z + eocd + 10), cd = rd32(z + eocd + 16);
        size_t p = cd;
        for (unsigned k = 0; k < count && p + 46 <= len && rd32(z + p) == 0x02014b50; k++) {
            unsigned method = rd16(z + p + 10), csize = rd32(z + p + 20), usize = rd32(z + p + 24);
            unsigned fnl = rd16(z + p + 28), exl = rd16(z + p + 30), cml = rd16(z + p + 32), loh = rd32(z + p + 42);
            if (fnl == 20 && p + 46 + fnl <= len && !memcmp(z + p + 46, "META-INF/MANIFEST.MF", 20) &&
                (size_t)loh + 30 <= len && rd32(z + loh) == 0x04034b50) {
                size_t data = loh + 30 + rd16(z + loh + 26) + rd16(z + loh + 28);
                if (data + csize > len) break;
                char *mf = NULL; int mlen = 0;
                if (method == 0) { mf = malloc(csize + 1); memcpy(mf, z + data, csize); mlen = (int)csize; }
                else if (method == 8) {
                    unsigned char *d = DecompressData(z + data, (int)csize, &mlen);
                    if (d) { mf = malloc((size_t)mlen + 1); memcpy(mf, d, (size_t)mlen); MemFree(d); }
                }
                (void)usize;
                if (mf) {
                    mf[mlen] = 0;
                    char *v = strstr(mf, "Implementation-Version:");
                    if (v) {
                        v += 23;
                        while (*v == ' ') v++;
                        size_t l = strcspn(v, "\r\n");
                        if (l >= n) l = n - 1;
                        memcpy(out, v, l); out[l] = 0;
                        ok = l > 0;
                    }
                    free(mf);
                }
                break;
            }
            p += 46 + fnl + exl + cml;
        }
    }
    free(z);
    return ok;
}

/* "3.10.2" > "3.9.0"; non-numeric parts compare as text */
bool version_newer(const char *a, const char *b)
{
    while (*a || *b) {
        while (*a && !isalnum((unsigned char)*a)) a++;
        while (*b && !isalnum((unsigned char)*b)) b++;
        if (isdigit((unsigned char)*a) && isdigit((unsigned char)*b)) {
            long x = strtol(a, (char **)&a, 10), y = strtol(b, (char **)&b, 10);
            if (x != y) return x > y;
        } else {
            if (!*a || !*b) return *a != 0 && isdigit((unsigned char)*a);
            if (*a != *b) return *a > *b;
            a++; b++;
        }
    }
    return false;
}

/* ---- Finding things ------------------------------------------------------------- */
static bool is_zb_arg(const char *s, size_t n)
{
    char tmp[512];
    if (n >= sizeof tmp) n = sizeof tmp - 1;
    memcpy(tmp, s, n); tmp[n] = 0;
    return (!strncmp(tmp, "-javaagent:", 11) && strstr(tmp, "ZombieBuddy")) || !strncmp(tmp, "-agentlib:zbNative", 18);
}

/* Finds a ZombieBuddy entry ("...") inside the game file's "vmArgs" list. */
static long json_find_zb(const char *t, size_t *end, char *arg, size_t an)
{
    const char *v = strstr(t, "\"vmArgs\"");
    if (!v) return -1;
    const char *p = strchr(v + 8, '[');
    if (!p) return -1;
    for (p++; *p && *p != ']'; p++) {
        if (*p != '"') continue;
        const char *s = p + 1, *e = s;
        while (*e && *e != '"') { if (*e == '\\') e++; e++; }
        if (!*e) return -1;
        if (is_zb_arg(s, (size_t)(e - s))) {
            if (arg) { size_t l = (size_t)(e - s) < an - 1 ? (size_t)(e - s) : an - 1; memcpy(arg, s, l); arg[l] = 0; }
            *end = (size_t)(e + 1 - t);
            return (long)(p - t);
        }
        p = e;
    }
    return -1;
}

void zb_scan(const char *json_path, const char *steam_cfg, ZbStatus *st)
{
    memset(st, 0, sizeof *st);
    if (json_path) {
        snprintf(st->game_dir, sizeof st->game_dir, "%s", json_path);
        char *slash = strrchr(st->game_dir, '/');
        if (slash) *slash = 0;
    }
    /* Workshop copies: the game's own library first, then all libraries */
    char libs[33][1024];
    int nl = 0;
    if (json_path) {
        const char *sa = strstr(json_path, "/steamapps/");
        if (sa) { size_t l = (size_t)(sa - json_path); memcpy(libs[0], json_path, l); libs[0][l] = 0; nl = 1; }
    }
    nl += steam_libraries(libs + nl, 32);
    const char *ids[2] = { ZB_MOD_ID, ZB_BETA_MOD_ID };
    for (int c = 0; c < 2; c++) {
        for (int i = 0; i < nl && !st->ws_found[c]; i++) {
            char jar[PATH_MAX];
            snprintf(jar, sizeof jar, "%s/steamapps/workshop/content/%s/%s/mods/ZombieBuddy/libs/%s", libs[i], STEAM_APP_ID, ids[c], ZB_JAR);
            if (file_exists(jar)) {
                st->ws_found[c] = true;
                snprintf(st->ws_jar[c], sizeof st->ws_jar[c], "%s", jar);
                jar_version(jar, st->ws_ver[c], sizeof st->ws_ver[c]);
            }
        }
    }
    if (st->game_dir[0]) {
        char jar[PATH_MAX];
        snprintf(jar, sizeof jar, "%s/%s", st->game_dir, ZB_JAR);
        st->jar_installed = file_exists(jar);
        if (st->jar_installed) jar_version(jar, st->inst_ver, sizeof st->inst_ver);
    }
    if (json_path) {
        char *t = read_file(json_path, NULL);
        size_t e;
        if (t) st->in_json = json_find_zb(t, &e, st->json_arg, sizeof st->json_arg) >= 0;
        free(t);
    }
    if (steam_cfg && steam_cfg[0]) {
        char *t = read_file(steam_cfg, NULL);
        char *lo = t ? steam_launch_options(t) : NULL;
        if (lo) {
            snprintf(st->steam_opts, sizeof st->steam_opts, "%s", lo);
            const char *p = lo;
            while (*p) {
                while (*p && isspace((unsigned char)*p)) p++;
                const char *s = p;
                while (*p && !isspace((unsigned char)*p)) p++;
                if (p > s && is_zb_arg(s, (size_t)(p - s))) st->in_steam = true;
            }
        }
        free(lo); free(t);
    }
    char ap[PATH_MAX];
    snprintf(ap, sizeof ap, "%s/.zombie_buddy", home_dir());
    st->approvals_exist = dir_exists(ap);
    /* log only when something changed (this runs every 2 seconds while the window is open) */
    char sum[4096];
    snprintf(sum, sizeof sum, "ZombieBuddy scan: Workshop stable %s %s | beta %s %s | game folder %s | jar %s %s | game file %s%s | Steam %s",
             st->ws_found[0] ? "found" : "not found", st->ws_found[0] ? (st->ws_ver[0][0] ? st->ws_ver[0] : "(no version)") : "",
             st->ws_found[1] ? "found" : "not found", st->ws_found[1] ? st->ws_ver[1] : "",
             st->game_dir, st->jar_installed ? "installed" : "not installed", st->inst_ver,
             st->in_json ? "has " : "no entry", st->json_arg, st->in_steam ? "has ZombieBuddy" : "no ZombieBuddy");
    static char last[4096];
    if (strcmp(sum, last) != 0) {
        snprintf(last, sizeof last, "%s", sum);
        pzlog("%s", sum);
        if (st->ws_found[0]) pzlog("  stable jar: %s", st->ws_jar[0]);
        if (st->ws_found[1]) pzlog("  beta jar: %s", st->ws_jar[1]);
    }
}

void zb_agent_arg(int policy, int verbosity, char *out, size_t n)
{
    char extra[96] = "";
    if (policy == 1) snprintf(extra, sizeof extra, "policy=deny-new");
    if (verbosity > 0) {
        size_t l = strlen(extra);
        snprintf(extra + l, sizeof extra - l, "%sverbosity=%d", l ? "," : "", verbosity);
    }
    snprintf(out, n, "-javaagent:%s%s%s", ZB_JAR, extra[0] ? "=" : "", extra);
}

/* ---- Installing ------------------------------------------------------------------- */
bool zb_copy_jar(const ZbStatus *st, int channel, char *msg, size_t n)
{
    if (!st->ws_found[channel]) { snprintf(msg, n, "ZombieBuddy is not downloaded from the Workshop yet."); return false; }
    char dst[PATH_MAX];
    snprintf(dst, sizeof dst, "%s/%s", st->game_dir, ZB_JAR);
    size_t len;
    char *d = read_file(st->ws_jar[channel], &len);
    bool ok = d && write_file(dst, d, len);
    free(d);
    /* a waiting update from an older install would overwrite this one: remove it */
    char nw[PATH_MAX];
    snprintf(nw, sizeof nw, "%s.new", dst);
    if (ok && file_exists(nw)) unlink(nw);
    snprintf(msg, n, ok ? "Copied ZombieBuddy.jar%s%s to the game folder." : "Could not copy ZombieBuddy.jar to %s.",
             ok && st->ws_ver[channel][0] ? " v" : (ok ? "" : st->game_dir), ok ? st->ws_ver[channel] : "");
    pzlog("Copy %s -> %s: %s", st->ws_jar[channel], dst, ok ? "OK" : "FAILED");
    return ok;
}

/* Adds (or replaces, or removes) the ZombieBuddy entry at the start of vmArgs. */
int zb_json_set(const char *json_path, const char *agent_arg)
{
    char *t = read_file(json_path, NULL);
    if (!t) return -1;
    size_t e;
    char old[256] = "";
    long q = json_find_zb(t, &e, old, sizeof old);
    if (agent_arg && q >= 0 && !strcmp(old, agent_arg)) { free(t); pzlog("Game file already has %s", agent_arg); return 0; }
    if (!agent_arg && q < 0) { free(t); return 0; }
    char *cur = t;
    if (q >= 0) { cur = json_remove_entry(t, q, e); free(t); }
    if (agent_arg) {
        const char *v = strstr(cur, "\"vmArgs\"");
        const char *b = v ? strchr(v + 8, '[') : NULL;
        if (!b) { free(cur); pzlog("Game file has no vmArgs list"); return -1; }
        const char *f = b + 1;
        while (*f == ' ' || *f == '\t' || *f == '\r' || *f == '\n') f++;
        Buf ins = {0};
        if (*f == ']') buf_addf(&ins, "\"%s\"", agent_arg);
        else {
            /* same layout as the first entry: own line (with its indent) or same line */
            bool own_line = false;
            const char *ls = f;
            while (ls > b + 1 && ls[-1] != '\n') ls--;
            if (ls > b + 1 || memchr(b + 1, '\n', (size_t)(f - b - 1))) own_line = true;
            if (own_line) {
                const char *nl = strstr(cur, "\r\n") ? "\r\n" : "\n";
                buf_addf(&ins, "\"%s\",%s", agent_arg, nl);
                buf_add(&ins, ls, (size_t)(f - ls));
            } else buf_addf(&ins, "\"%s\", ", agent_arg);
        }
        char *out = str_splice(cur, (size_t)(f - cur), 0, ins.s);
        buf_free(&ins);
        free(cur);
        cur = out;
    }
    bool ok = write_file(json_path, cur, strlen(cur));
    pzlog("Game file: %s ZombieBuddy entry (%s): %s", agent_arg ? "set" : "removed", agent_arg ? agent_arg : old, ok ? "OK" : "FAILED");
    free(cur);
    return ok ? 1 : -1;
}

/* Steam launch options, same rules as ZombieBuddy's installer */
static char *steam_edit_zb(const char *cur, void *ud)
{
    const char *arg = ud;
    if (arg && !arg[0]) arg = NULL;
    /* remove any ZombieBuddy agent and the "--" right after it */
    Buf b = {0};
    const char *p = cur;
    bool skip_dashes = false;
    while (*p) {
        while (*p && isspace((unsigned char)*p)) p++;
        const char *s = p;
        while (*p && !isspace((unsigned char)*p)) p++;
        size_t l = (size_t)(p - s);
        if (!l) break;
        if (is_zb_arg(s, l)) { skip_dashes = true; continue; }
        if (skip_dashes && l == 2 && !strncmp(s, "--", 2)) {
            /* drop the "--" that came with ZombieBuddy only when nothing follows it */
            const char *r = p;
            while (*r && isspace((unsigned char)*r)) r++;
            skip_dashes = false;
            if (!*r) continue;
        }
        skip_dashes = false;
        if (b.len) buf_adds(&b, " ");
        buf_add(&b, s, l);
    }
    char *rest = b.s ? b.s : calloc(1, 1);
    if (!strcmp(rest, "--")) rest[0] = 0;
    if (!arg) return rest;
    Buf o = {0};
    char *cmd = strstr(rest, "%command%");
    bool has_dashes = false;
    for (const char *q = rest; (q = strstr(q, "--")); q += 2)
        if ((q == rest || q[-1] == ' ') && (q[2] == 0 || q[2] == ' ')) has_dashes = true;
    if (!rest[0]) buf_addf(&o, "%s --", arg);
    else if (cmd) {
        size_t at = (size_t)(cmd - rest) + 9;
        buf_add(&o, rest, at);
        buf_addf(&o, " %s --", arg);
        if (rest[at]) { buf_adds(&o, " "); const char *r = rest + at; while (*r == ' ') r++; buf_adds(&o, r); }
    } else if (has_dashes) buf_addf(&o, "%s %s", arg, rest);   /* already has a "--": stay in front of it */
    else buf_addf(&o, "%s -- %s", arg, rest);
    free(rest);
    return o.s;
}

int zb_steam_set(const char *steam_cfg, const char *agent_arg)
{
    if (!steam_cfg || !steam_cfg[0]) return -1;
    return steam_edit_launch_options(steam_cfg, steam_edit_zb, (void *)agent_arg, ".pzmemorytool-zb.bak", NULL, 0);
}

bool zb_remove_files(const ZbStatus *st, char *msg, size_t n)
{
    const char *names[] = { ZB_JAR, ZB_JAR ".new", ZB_JAR ".bak", "zbNative.dll" };
    int removed = 0; bool ok = true;
    for (int i = 0; i < 4; i++) {
        char p[PATH_MAX];
        snprintf(p, sizeof p, "%s/%s", st->game_dir, names[i]);
        if (!file_exists(p)) continue;
        if (unlink(p) == 0) { removed++; pzlog("Removed %s", p); }
        else { ok = false; pzlog("Could not remove %s", p); }
    }
    snprintf(msg, n, ok ? "Removed %d ZombieBuddy file%s from the game folder." : "Some ZombieBuddy files could not be removed (%d removed).",
             removed, removed == 1 ? "" : "s");
    return ok;
}

static int rm_cb(const char *p, const struct stat *sb, int flag, struct FTW *f)
{
    (void)sb; (void)flag; (void)f;
    return remove(p);
}
bool zb_delete_approvals(void)
{
    char p[PATH_MAX];
    snprintf(p, sizeof p, "%s/.zombie_buddy", home_dir());
    if (!dir_exists(p)) return true;
    bool ok = nftw(p, rm_cb, 16, FTW_DEPTH | FTW_PHYS) == 0;
    pzlog("Delete %s: %s", p, ok ? "OK" : "FAILED");
    return ok;
}
