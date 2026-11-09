/* =============================================================================
 *  PZ Memory Tool - log file
 *
 *  Writes everything the tool detects and does to
 *      ~/.local/state/PZMemoryTool/log.txt
 *  The previous run is kept as log-previous.txt; each start deletes the older
 *  one, so only two runs are ever kept, and one run never writes more than
 *  1 MB. Nothing is sent anywhere.
 *  Copy it to the clipboard with F12, or the View / Select button on a controller.
 * ============================================================================= */
#define _GNU_SOURCE
#include "pzmt.h"

#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/time.h>
#include <time.h>

#define LOG_MAX_BYTES (1024L * 1024L)
static FILE *logf;
static char  log_file[1024];
static long  written;
static bool  capped;

const char *log_path(void) { return log_file; }

void log_init(void)
{
    const char *x = getenv("XDG_STATE_HOME");
    char dir[1024];
    if (x && *x) snprintf(dir, sizeof dir, "%s/PZMemoryTool", x);
    else snprintf(dir, sizeof dir, "%s/.local/state/PZMemoryTool", home_dir());
    mkdir_p(dir);
    snprintf(log_file, sizeof log_file, "%s/log.txt", dir);
    char prev[1100];
    snprintf(prev, sizeof prev, "%s/log-previous.txt", dir);
    rename(log_file, prev);
    logf = fopen(log_file, "w");
}

void pzlog(const char *fmt, ...)
{
    struct timeval tv;
    gettimeofday(&tv, NULL);
    struct tm tm;
    localtime_r(&tv.tv_sec, &tm);
    char ts[32];
    strftime(ts, sizeof ts, "%H:%M:%S", &tm);
    char msg[8192];
    va_list ap;
    va_start(ap, fmt);
    vsnprintf(msg, sizeof msg, fmt, ap);
    va_end(ap);
    /* one log line per text line, so multi-line dialog texts stay readable */
    char *save = NULL, *copy = strdup(msg);
    bool first = true;
    for (char *ln = strtok_r(copy, "\n", &save); ln || first; ln = strtok_r(NULL, "\n", &save)) {
        const char *t = ln ? ln : "";
        if (logf && !capped) {
            if (written + (long)strlen(t) + 24 > LOG_MAX_BYTES) {
                fprintf(logf, "%s (the log reached 1 MB: nothing more is written during this run)\n", ts);
                capped = true;
            } else written += fprintf(logf, "%s.%03ld %s%s\n", ts, (long)(tv.tv_usec / 1000), first ? "" : "    | ", t);
        }
        fprintf(stderr, "[pzmt] %s%s\n", first ? "" : "    | ", t);
        first = false;
        if (!ln) break;
    }
    free(copy);
    if (logf) fflush(logf);
}

/* Logs a text file's contents (small files only), e.g. the game's vmArgs */
void pzlog_file(const char *label, const char *path, size_t max)
{
    size_t n = 0;
    char *t = read_file(path, &n);
    if (!t) { pzlog("%s: %s (could not read)", label, path); return; }
    if (n > max) { t[max] = 0; pzlog("%s: %s (%zu bytes, first %zu):\n%s", label, path, n, max, t); }
    else pzlog("%s: %s (%zu bytes):\n%s", label, path, n, t);
    free(t);
}
