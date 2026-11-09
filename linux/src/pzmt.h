/* =============================================================================
 *  PZ Memory Tool - Linux / SteamOS version
 *  Shared declarations for the source files in this folder.
 *
 *  Open source. Same behaviour as the Windows version (PZMemoryTool.cs):
 *  sets Project Zomboid's memory allocation (-Xmx / -Xms) in
 *  ProjectZomboid64.json, clears memory settings from Steam's launch options,
 *  and copies console.txt to the clipboard.
 * ============================================================================= */
#ifndef PZMT_H
#define PZMT_H

#include <stdbool.h>
#include <stddef.h>

#ifndef PZMT_VERSION
#define PZMT_VERSION "1.2.0-preview"
#endif
#ifndef PZMT_BUILD
#define PZMT_BUILD "0"          /* GitHub Actions sets this to the run number */
#endif

/* ---- Settings (change these to adjust the tool's behaviour) --------------- */
#define JSON_NAME            "ProjectZomboid64.json"
#define GAME_PROCESS_NAME    "ProjectZomboid64"
#define STEAM_APP_ID         "108600"
#define KOFI_URL             "https://ko-fi.com/comfyplacebo"

#define MIN_ALLOC_GB         5   /* never offer a maximum below this              */
#define FIXED_TIER_RESERVE   2   /* 8 / 12 GB tiers: fixed choices must fit in RAM - 2 */
#define LINUX_RESERVE_GB     6   /* 16 GB and up: highest maximum = installed - 6 GB */
#define DEFAULT_XMS_GB       4   /* minimum (-Xms) selected by default             */
#define MIN_XMS_GB           4   /* lowest -Xms offered; highest is maximum - 1 GB */
#define STEAM_MACHINE_MIN_GB 8   /* Steam Machine: lowest maximum offered          */
#define GAME_CHECK_WAIT_S    5
#define STEAM_CLOSE_WAIT_S   30

/* ---- Small helpers (core.c) ----------------------------------------------- */
typedef struct { char *s; size_t len, cap; } Buf;
void   buf_add(Buf *b, const char *s, size_t n);
void   buf_adds(Buf *b, const char *s);
void   buf_addf(Buf *b, const char *fmt, ...);
void   buf_free(Buf *b);

char  *read_file(const char *path, size_t *len_out);           /* malloc'd, NUL-terminated */
bool   write_file(const char *path, const char *data, size_t len);
bool   copy_file(const char *from, const char *to);
bool   file_exists(const char *path);
bool   dir_exists(const char *path);
bool   mkdir_p(const char *path);
const char *home_dir(void);
void   path_join(char *out, size_t n, const char *a, const char *b);
bool   command_exists(const char *name);
void   spawn_detached(const char *const argv[]);              /* runs and forgets */
int    run_wait(const char *const argv[]);                      /* runs, waits, returns exit code */
char  *run_capture(const char *const argv[]);                   /* runs, returns stdout (malloc'd) */
char  *self_path(void);                                         /* absolute path of this program */

/* ---- Game file (-Xmx / -Xms) ---------------------------------------------- */
typedef struct { bool found; size_t pos, len; long mb; } MemArg;
MemArg find_mem_arg(const char *text, char flag);               /* flag 'x' = -Xmx, 's' = -Xms */
char  *set_memory(const char *text, int xmx_gb, int xms_gb);    /* malloc'd; xms_gb 0 = no -Xms */
bool   is_valid_pz_json(const char *path);
char  *find_game_json(void);                                    /* default Steam folders + libraries */
const char *describe_gb(long mb, char *out, size_t n);          /* "3 GB" / "3.5 GB" */

/* ---- Steam ------------------------------------------------------------------ */
#define MAX_STEAM_ROOTS 8
int    steam_roots(char roots[][1024], int max);
long   steam_account_id(void);
bool   steam_config_file(char *out, size_t n);                 /* signed-in account's localconfig.vdf */
char  *steam_launch_options(const char *config_text);          /* PZ's launch options, malloc'd or NULL */
char  *memory_args(const char *launch_options);                 /* "-Xmx6g -Xms2g" ("" if none) */
char  *without_memory_args(const char *launch_options);
bool   steam_remove_memory_args(const char *config_file, char *backup_out, size_t n);
char  *steam_backed_up_value(const char *config_file);
bool   steam_restore_from_backup(const char *config_file);
void   steam_request_shutdown(void);
bool   steam_add_to_steam_supported(void);
bool   steam_add_to_steam(void);

/* ---- Processes / session ------------------------------------------------------ */
bool   is_game_running(void);
bool   is_steam_running(void);
bool   in_game_mode(void);
bool   can_switch_session(void);
bool   switch_to_desktop_mode(void);
bool   switch_to_game_mode(void);

/* ---- Memory / hardware ------------------------------------------------------ */
typedef enum { HW_PC, HW_STEAM_DECK, HW_STEAM_MACHINE } HwKind;
typedef struct {
    HwKind kind;
    double usable_gb;     /* what Linux reports (MemTotal)                 */
    int    installed_gb;  /* rounded up to a standard size                 */
    int    uma_gb;        /* Steam Deck graphics reserve (UMA buffer), 0 if n/a */
} MemInfo;
MemInfo read_memory_info(void);

/* The rules: which maxima are allowed, what is recommended, which notes show */
typedef enum { XMS_NORMAL, XMS_LOCKED_OFF, XMS_HIDDEN } XmsMode;
typedef struct {
    int     options[64];
    int     count;
    int     recommended;
    char    recommend_text[64];
    XmsMode xms;
    bool    locked;          /* under 8 GB: nothing can be saved */
    int     tier;            /* 32, 16, 12, 8, 0; or 100+uma for Steam Deck, 200 for Steam Machine */
} Plan;
Plan   make_plan(const MemInfo *mi);
void   plan_messages(const Plan *p, const MemInfo *mi, int selected_gb,
                     char *warning, size_t wn, char *note, size_t nn);

/* ---- Settings file ~/.config/PZMemoryTool/settings.ini -------------------- */
char  *settings_get(const char *key);                           /* malloc'd or NULL */
void   settings_set(const char *key, const char *value);
void   config_dir(char *out, size_t n);

/* ---- Pending Game Mode fix ------------------------------------------------- */
bool   pending_fix_write(const char *action);                     /* "remove" or "restore" */
char  *pending_fix_read(void);                                   /* malloc'd or NULL */
bool   pending_fix_exists(void);
void   pending_fix_clear(void);

/* ---- More helpers (core.c) ----------------------------------------------- */
char  *str_splice(const char *text, size_t at, size_t remove, const char *insert);
char  *json_remove_entry(const char *text, long q, size_t e);
int    steam_libraries(char libs[][1024], int max);
int    steam_edit_launch_options(const char *cfg, char *(*edit)(const char *cur, void *ud), void *ud,
                                 const char *backup_suffix, char *backup_out, size_t bn);
void   steam_start(void);
void   steam_open_url(const char *url);

/* ---- Debug log (log.c) -------------------------------------------------------- */
void   log_init(void);
void   pzlog(const char *fmt, ...) __attribute__((format(printf, 1, 2)));
void   pzlog_file(const char *label, const char *path, size_t max);
const char *log_path(void);

/* ---- ZombieBuddy (zb.c) ------------------------------------------------------- */
#define ZB_MOD_ID       "3619862853"
#define ZB_BETA_MOD_ID  "3812624292"
#define ZB_JAR          "ZombieBuddy.jar"
typedef struct {
    bool ws_found[2];                 /* [0] stable, [1] beta Workshop copy downloaded */
    char ws_jar[2][1024];
    char ws_ver[2][32];
    char game_dir[1024];              /* folder of ProjectZomboid64.json */
    bool jar_installed;
    char inst_ver[32];
    bool in_json;                     /* ZombieBuddy entry in the game file's vmArgs */
    char json_arg[256];
    bool in_steam;                    /* ZombieBuddy in Steam's launch options */
    char steam_opts[1024];
    bool approvals_exist;             /* ~/.zombie_buddy */
} ZbStatus;
void   zb_scan(const char *json_path, const char *steam_cfg, ZbStatus *st);
void   zb_agent_arg(int policy, int verbosity, char *out, size_t n);   /* policy 0 prompt, 1 deny-new; verbosity 0..2 */
bool   zb_copy_jar(const ZbStatus *st, int channel, char *msg, size_t n);
int    zb_json_set(const char *json_path, const char *agent_arg);    /* agent_arg NULL = remove. 1 changed, 0 same, -1 error */
int    zb_steam_set(const char *steam_cfg, const char *agent_arg);   /* same, for Steam's launch options */
bool   zb_remove_files(const ZbStatus *st, char *msg, size_t n);
bool   zb_delete_approvals(void);
bool   jar_version(const char *jar, char *out, size_t n);
bool   version_newer(const char *a, const char *b);                  /* a > b */

/* ---- Clipboard (clipboard.c) ------------------------------------------------ */
bool   clip_init(void);
bool   clip_set_file(const char *path);
bool   clip_set_text(const char *text, size_t len);
void   clip_pump(void);
void   clip_shutdown(void);

/* ---- Banner (banner.c) ------------------------------------------------------ */
void   banner_init(void);
void   banner_step(void);
void   banner_draw(float x, float y, float width, float height);
int    banner_design_height(void);

#endif
