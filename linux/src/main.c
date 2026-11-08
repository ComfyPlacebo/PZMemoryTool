/* =============================================================================
 *  PZ Memory Tool - Linux / SteamOS version: window, controls and dialogs
 *
 *  Same window, layout and behaviour as the Windows version, drawn with raylib.
 *  Looks native: colours from your KDE / SteamOS theme (kdeglobals), the system
 *  font (Noto Sans), and works with mouse, touch, keyboard and controller.
 *
 *  Controls:  D-pad / left stick / arrow keys  move between controls
 *             A / Enter / Space                 press
 *             B / Escape                        back (close a list or dialog)
 *             Left / Right on a dropdown        change the value directly
 * ============================================================================= */
#define _GNU_SOURCE
#include "pzmt.h"
#include "raylib.h"
#include "GLFW/glfw3.h"   /* only to catch very quick taps and clicks (see below) */

#include <limits.h>
#include <math.h>
#include <pthread.h>
#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
#include <time.h>
#include <dirent.h>
#include <unistd.h>

#define W        640           /* layout is designed at 100% scaling, like the Windows version */
#define LEFT_W   410
#define BOX_H    314
#define MAX_BOX_X 455

/* =============================================================================
 *  Theme: KDE / SteamOS colours (kdeglobals), Breeze if there is no KDE theme
 * ============================================================================= */
typedef struct {
    Color bg, fg, sub, warn, accent, on_accent, btn, btn_hover, btn_border, field, field_border, link, box, note;
    bool dark;
} Theme;
static Theme T;

static Color mix(Color a, Color b, float t)
{
    return (Color){ (unsigned char)(a.r + (b.r - a.r) * t), (unsigned char)(a.g + (b.g - a.g) * t),
                    (unsigned char)(a.b + (b.b - a.b) * t), 255 };
}
static Color rgb(int r, int g, int b) { return (Color){ (unsigned char)r, (unsigned char)g, (unsigned char)b, 255 }; }
static float lum(Color c) { return (0.299f * c.r + 0.587f * c.g + 0.114f * c.b) / 255.0f; }

static void theme_breeze(bool dark)
{
    if (dark) {
        T.bg = rgb(32, 35, 38); T.fg = rgb(252, 252, 252); T.sub = rgb(161, 169, 177); T.warn = rgb(218, 68, 83);
        T.btn = rgb(41, 44, 48); T.field = rgb(20, 22, 24); T.accent = rgb(61, 174, 233); T.link = rgb(29, 153, 243);
    } else {
        T.bg = rgb(239, 240, 241); T.fg = rgb(35, 38, 39); T.sub = rgb(112, 125, 138); T.warn = rgb(218, 68, 83);
        T.btn = rgb(252, 252, 252); T.field = rgb(255, 255, 255); T.accent = rgb(61, 174, 233); T.link = rgb(41, 128, 185);
    }
    T.on_accent = rgb(255, 255, 255);
}

static bool parse_rgb(const char *v, Color *out)
{
    int r, g, b;
    if (sscanf(v, "%d,%d,%d", &r, &g, &b) == 3) { *out = rgb(r, g, b); return true; }
    if (v[0] == '#' && strlen(v) >= 7) { unsigned x; sscanf(v + 1, "%6x", &x); *out = rgb(x >> 16, (x >> 8) & 255, x & 255); return true; }
    return false;
}

static char kde_font_family[128] = "Noto Sans";

static void read_kdeglobals(const char *path)
{
    char *t = read_file(path, NULL);
    if (!t) return;
    char section[128] = "";
    char *save = NULL;
    for (char *ln = strtok_r(t, "\n", &save); ln; ln = strtok_r(NULL, "\n", &save)) {
        if (ln[0] == '[') { snprintf(section, sizeof section, "%s", ln); continue; }
        char *eq = strchr(ln, '=');
        if (!eq) continue;
        *eq = 0;
        const char *k = ln, *v = eq + 1;
        Color c;
        if (strcmp(section, "[General]") == 0) {
            if (strcmp(k, "font") == 0) {
                char fam[128]; snprintf(fam, sizeof fam, "%s", v);
                char *comma = strchr(fam, ','); if (comma) *comma = 0;
                if (fam[0]) snprintf(kde_font_family, sizeof kde_font_family, "%s", fam);
            }
            if (strcmp(k, "AccentColor") == 0 && parse_rgb(v, &c)) T.accent = c;
            continue;
        }
        if (!parse_rgb(v, &c)) continue;
        if (strcmp(section, "[Colors:Window]") == 0) {
            if (!strcmp(k, "BackgroundNormal")) T.bg = c;
            else if (!strcmp(k, "ForegroundNormal")) T.fg = c;
            else if (!strcmp(k, "ForegroundInactive")) T.sub = c;
            else if (!strcmp(k, "ForegroundNegative")) T.warn = c;
        } else if (strcmp(section, "[Colors:Button]") == 0) {
            if (!strcmp(k, "BackgroundNormal")) T.btn = c;
            else if (!strcmp(k, "DecorationFocus")) T.accent = c;
        } else if (strcmp(section, "[Colors:View]") == 0) {
            if (!strcmp(k, "BackgroundNormal")) T.field = c;
            else if (!strcmp(k, "ForegroundLink")) T.link = c;
        } else if (strcmp(section, "[Colors:Selection]") == 0) {
            if (!strcmp(k, "BackgroundNormal")) T.accent = c;
            else if (!strcmp(k, "ForegroundNormal")) T.on_accent = c;
        }
    }
    free(t);
}

static void theme_load(void)
{
    /* No KDE theme: follow the desktop's dark / light preference */
    bool dark = true;
    const char *ga[] = { "gsettings", "get", "org.gnome.desktop.interface", "color-scheme", NULL };
    char *pref = command_exists("gsettings") ? run_capture(ga) : NULL;
    if (pref) { dark = strstr(pref, "dark") != NULL; free(pref); }
    theme_breeze(dark);

    read_kdeglobals("/etc/xdg/kdeglobals");
    char p[PATH_MAX];
    const char *x = getenv("XDG_CONFIG_HOME");
    if (x && *x) snprintf(p, sizeof p, "%s/kdeglobals", x);
    else snprintf(p, sizeof p, "%s/.config/kdeglobals", home_dir());
    read_kdeglobals(p);

    const char *force = getenv("PZMT_THEME");          /* testing: "dark" / "light" */
    if (force) theme_breeze(strcmp(force, "light") != 0);

    T.dark = lum(T.bg) < 0.5f;
    T.btn_hover    = mix(T.btn, T.accent, 0.18f);
    T.btn_border   = mix(T.bg, T.fg, 0.25f);
    T.field_border = mix(T.bg, T.fg, 0.25f);
    T.box          = T.sub;
    T.note         = T.dark ? mix(T.accent, rgb(255, 255, 255), 0.25f) : mix(T.accent, rgb(0, 0, 0), 0.25f);
    if (lum(T.link) < 0.05f) T.link = T.accent;
}

/* =============================================================================
 *  Fonts: the system font (Noto Sans on SteamOS / KDE), found with fontconfig
 * ============================================================================= */
typedef struct { Font f; float px; bool loaded; } UFont;
enum { F_UI, F_SMALL, F_SUB, F_STEAM_B, F_BIG_B, F_TITLE_B, F_BTN_B, F_NOTE_I, F_MONO_B, F_DLG_B, F_COUNT };
static UFont FONTS[F_COUNT];
static float S = 1.0f;                 /* display scaling */

static char *font_file(const char *pattern)
{
    const char *a[] = { "fc-match", "-f", "%{file}", pattern, NULL };
    char *f = command_exists("fc-match") ? run_capture(a) : NULL;
    if (f && f[0] && file_exists(f)) return f;
    free(f);
    return NULL;
}

static int codepoints[400];
static int codepoint_count;

static void load_font(int which, const char *file, float px)
{
    UFont *u = &FONTS[which];
    u->px = px;
    int size = (int)roundf(px * S);
    if (file) {
        u->f = LoadFontEx(file, size, codepoints, codepoint_count);
        u->loaded = u->f.texture.id != 0 && u->f.glyphCount > 0;
        if (u->loaded) SetTextureFilter(u->f.texture, TEXTURE_FILTER_BILINEAR);
    }
    if (!u->loaded) u->f = GetFontDefault();
}

static void fonts_load(void)
{
    codepoint_count = 0;
    for (int c = 32; c < 127; c++) codepoints[codepoint_count++] = c;
    for (int c = 160; c < 256; c++) codepoints[codepoint_count++] = c;
    int extra[] = { 0x2013, 0x2014, 0x2018, 0x2019, 0x201C, 0x201D, 0x2022, 0x2026, 0x2212, 0x2192 };
    for (size_t i = 0; i < sizeof extra / sizeof *extra; i++) codepoints[codepoint_count++] = extra[i];

    char pat[256];
    snprintf(pat, sizeof pat, "%s:style=Regular", kde_font_family);
    char *reg = font_file(pat);
    snprintf(pat, sizeof pat, "%s:style=Bold", kde_font_family);
    char *bold = font_file(pat);
    snprintf(pat, sizeof pat, "%s:style=Italic", kde_font_family);
    char *ital = font_file(pat);
    char *mono = font_file("monospace:style=Bold");

    /* Windows point sizes at 96 dpi, so the layout matches the Windows version */
    load_font(F_UI,      reg,  13.33f);
    load_font(F_SMALL,   reg,  12.0f);
    load_font(F_SUB,     reg,  11.33f);
    load_font(F_STEAM_B, bold, 12.67f);
    load_font(F_BIG_B,   bold, 14.67f);
    load_font(F_TITLE_B, bold, 18.67f);
    load_font(F_BTN_B,   bold, 13.33f);
    load_font(F_NOTE_I,  ital, 13.33f);
    load_font(F_MONO_B,  mono ? mono : bold, 13.33f);
    load_font(F_DLG_B,   bold, 14.0f);
    free(reg); free(bold); free(ital); free(mono);
}

/* =============================================================================
 *  Drawing helpers (all coordinates in layout units; multiplied by S here)
 * ============================================================================= */
static Rectangle R(float x, float y, float w, float h) { return (Rectangle){ x, y, w, h }; }
static Rectangle SR(Rectangle r) { return (Rectangle){ roundf(r.x * S), roundf(r.y * S), roundf(r.width * S), roundf(r.height * S) }; }
static Rectangle grow(Rectangle r, float d) { return (Rectangle){ r.x - d, r.y - d, r.width + 2 * d, r.height + 2 * d }; }

static void rrect(Rectangle r, float radius, Color c)
{
    Rectangle s = SR(r);
    float m = fminf(s.width, s.height);
    if (radius <= 0 || m <= 0) { DrawRectangleRec(s, c); return; }
    DrawRectangleRounded(s, fminf(1.0f, 2 * radius * S / m), 8, c);
}
/* Filled rounded rectangle with a 1px border */
static void box_border(Rectangle r, float radius, Color fill, Color border)
{
    rrect(r, radius, border);
    rrect(grow(r, -1.0f / S), radius - 0.5f, fill);
}

static float text_w(int f, const char *s)
{
    UFont *u = &FONTS[f];
    return MeasureTextEx(u->f, s, roundf(u->px * S), 0).x / S;
}
static float line_h(int f) { return FONTS[f].px * 1.36f; }

static void text_at(int f, const char *s, float x, float y, Color c)
{
    UFont *u = &FONTS[f];
    DrawTextEx(u->f, s, (Vector2){ roundf(x * S), roundf(y * S) }, roundf(u->px * S), 0, c);
}

/* Word-wraps text into lines (handles \n). Returns the number of lines. */
#define MAX_LINES 40
static int wrap(int f, const char *s, float width, char lines[][512])
{
    int n = 0;
    char cur[512] = "";
    const char *p = s;
    while (*p && n < MAX_LINES) {
        if (*p == '\n') { snprintf(lines[n++], 512, "%s", cur); cur[0] = 0; p++; continue; }
        const char *e = p;
        while (*e && *e != ' ' && *e != '\n') e++;
        char word[512];
        size_t wl = (size_t)(e - p) < sizeof word - 1 ? (size_t)(e - p) : sizeof word - 1;
        memcpy(word, p, wl); word[wl] = 0;
        /* a single word wider than the box (a long file path): break it, preferably after a '/' */
        while (text_w(f, word) > width && n < MAX_LINES - 1) {
            size_t cut = strlen(word), best = 0, fit = 0, slash = 0;
            char tmp[512];
            for (size_t k = 1; k < cut; k++) {
                memcpy(tmp, word, k); tmp[k] = 0;
                char trial2[1024];
                snprintf(trial2, sizeof trial2, "%s%s%s", cur, cur[0] ? " " : "", tmp);
                if (text_w(f, trial2) > width) break;
                fit = k;
                if (word[k - 1] == '/') slash = k;
            }
            best = slash ? slash : fit;
            if (best == 0) {
                if (cur[0]) { snprintf(lines[n++], 512, "%s", cur); cur[0] = 0; continue; }
                best = 1;
            }
            memcpy(tmp, word, best); tmp[best] = 0;
            snprintf(lines[n++], 512, "%s%s%s", cur, cur[0] ? " " : "", tmp);
            cur[0] = 0;
            memmove(word, word + best, strlen(word + best) + 1);
        }
        char trial[1024];
        snprintf(trial, sizeof trial, "%s%s%s", cur, cur[0] ? " " : "", word);
        if (cur[0] && text_w(f, trial) > width) {
            snprintf(lines[n++], 512, "%s", cur);
            snprintf(cur, sizeof cur, "%s", word);
        } else snprintf(cur, sizeof cur, "%s", trial);
        p = e;
        while (*p == ' ') p++;
    }
    if (n < MAX_LINES && (cur[0] || n == 0 || (p > s && p[-1] == '\n'))) snprintf(lines[n++], 512, "%s", cur);
    return n;
}

enum { AL_LEFT, AL_CENTER, AL_RIGHT };
/* Draws wrapped text in a box. valign: 0 top, 1 middle, 2 bottom. Returns the height used. */
static float text_box(int f, const char *s, Rectangle r, Color c, int align, int valign)
{
    static char lines[MAX_LINES][512];
    int n = wrap(f, s, r.width, lines);
    float lh = line_h(f), total = n * lh;
    float y = r.y + 1;
    if (valign == 1) y = r.y + (r.height - total) / 2;
    if (valign == 2) y = r.y + r.height - total;
    for (int i = 0; i < n; i++) {
        float x = r.x;
        if (align == AL_CENTER) x = r.x + (r.width - text_w(f, lines[i])) / 2;
        if (align == AL_RIGHT) x = r.x + r.width - text_w(f, lines[i]);
        text_at(f, lines[i], x, y + i * lh, c);
    }
    return total;
}

/* =============================================================================
 *  Input: mouse / touch, keyboard and controller, with focus navigation
 * ============================================================================= */
enum { NAV_NONE, NAV_UP, NAV_DOWN, NAV_LEFT, NAV_RIGHT };
typedef struct {
    int  nav;          /* direction pressed this frame */
    int  tab;          /* +1 / -1 */
    bool act, back;
    bool mouse_pressed, mouse_released;
    Vector2 mouse;     /* in layout units */
    float wheel;
} Input;
static Input in;
static bool  show_focus;          /* focus ring only after keyboard / controller use */
static bool  blocked;             /* a dialog or list is open: main window ignores input */

/* A quick tap (touchscreen, Steam Input, scripts) can press and release within one frame,
 * which raylib alone would miss. These hooks remember that it happened. */
static GLFWmousebuttonfun prev_mouse_cb;
static volatile int quick_down, quick_up;
static void mouse_hook(GLFWwindow *w, int button, int action, int mods)
{
    if (button == GLFW_MOUSE_BUTTON_LEFT) { if (action == GLFW_PRESS) quick_down++; else if (action == GLFW_RELEASE) quick_up++; }
    if (prev_mouse_cb) prev_mouse_cb(w, button, action, mods);
}
static void install_input_hooks(void)
{
    prev_mouse_cb = glfwSetMouseButtonCallback((GLFWwindow *)GetWindowHandle(), mouse_hook);
}

static double pad_next;           /* controller auto-repeat */
static int    pad_dir_held;

static int gamepad_dir(void)
{
    for (int g = 0; g < 4; g++) {
        if (!IsGamepadAvailable(g)) continue;
        if (IsGamepadButtonDown(g, GAMEPAD_BUTTON_LEFT_FACE_UP)) return NAV_UP;
        if (IsGamepadButtonDown(g, GAMEPAD_BUTTON_LEFT_FACE_DOWN)) return NAV_DOWN;
        if (IsGamepadButtonDown(g, GAMEPAD_BUTTON_LEFT_FACE_LEFT)) return NAV_LEFT;
        if (IsGamepadButtonDown(g, GAMEPAD_BUTTON_LEFT_FACE_RIGHT)) return NAV_RIGHT;
        float ax = GetGamepadAxisMovement(g, GAMEPAD_AXIS_LEFT_X), ay = GetGamepadAxisMovement(g, GAMEPAD_AXIS_LEFT_Y);
        if (fabsf(ax) > 0.6f || fabsf(ay) > 0.6f) {
            if (fabsf(ay) > fabsf(ax)) return ay < 0 ? NAV_UP : NAV_DOWN;
            return ax < 0 ? NAV_LEFT : NAV_RIGHT;
        }
    }
    return NAV_NONE;
}
static bool gamepad_pressed(int button)
{
    for (int g = 0; g < 4; g++) if (IsGamepadAvailable(g) && IsGamepadButtonPressed(g, button)) return true;
    return false;
}

static void read_input(void)
{
    memset(&in, 0, sizeof in);
    Vector2 m = GetMousePosition();
    in.mouse = (Vector2){ m.x / S, m.y / S };
    in.mouse_pressed = IsMouseButtonPressed(MOUSE_BUTTON_LEFT) || quick_down > 0;
    in.mouse_released = IsMouseButtonReleased(MOUSE_BUTTON_LEFT) || (quick_up > 0 && !IsMouseButtonDown(MOUSE_BUTTON_LEFT));
    if (in.mouse_released) quick_up = 0;
    quick_down = 0;
    in.wheel = GetMouseWheelMove();
    if (in.mouse_pressed) show_focus = false;

    /* keys from the queue too, so very quick presses are never lost */
    for (int k; (k = GetKeyPressed()) != 0; ) {
        switch (k) {
            case KEY_UP: in.nav = NAV_UP; break;
            case KEY_DOWN: in.nav = NAV_DOWN; break;
            case KEY_LEFT: in.nav = NAV_LEFT; break;
            case KEY_RIGHT: in.nav = NAV_RIGHT; break;
            case KEY_TAB: in.tab = (IsKeyDown(KEY_LEFT_SHIFT) || IsKeyDown(KEY_RIGHT_SHIFT)) ? -1 : 1; break;
            case KEY_ENTER: case KEY_KP_ENTER: case KEY_SPACE: in.act = true; break;
            case KEY_ESCAPE: case KEY_BACKSPACE: in.back = true; break;
        }
    }
    if (IsKeyPressedRepeat(KEY_UP)) in.nav = NAV_UP;
    if (IsKeyPressedRepeat(KEY_DOWN)) in.nav = NAV_DOWN;
    if (IsKeyPressedRepeat(KEY_LEFT)) in.nav = NAV_LEFT;
    if (IsKeyPressedRepeat(KEY_RIGHT)) in.nav = NAV_RIGHT;
    if (IsKeyPressedRepeat(KEY_TAB)) in.tab = (IsKeyDown(KEY_LEFT_SHIFT) || IsKeyDown(KEY_RIGHT_SHIFT)) ? -1 : 1;

    int d = gamepad_dir();
    double now = GetTime();
    if (d != NAV_NONE) {
        if (d != pad_dir_held) { in.nav = d; pad_next = now + 0.40; }
        else if (now >= pad_next) { in.nav = d; pad_next = now + 0.12; }
    }
    pad_dir_held = d;
    if (gamepad_pressed(GAMEPAD_BUTTON_RIGHT_FACE_DOWN)) in.act = true;      /* A */
    if (gamepad_pressed(GAMEPAD_BUTTON_RIGHT_FACE_RIGHT)) in.back = true;    /* B */
    if (gamepad_pressed(GAMEPAD_BUTTON_RIGHT_TRIGGER_1)) in.tab = 1;         /* RB / R1 */
    if (gamepad_pressed(GAMEPAD_BUTTON_LEFT_TRIGGER_1)) in.tab = -1;         /* LB / L1 */

    if (in.nav || in.tab || in.act) show_focus = true;
}

static bool hover(Rectangle r) { return CheckCollisionPointRec(in.mouse, r); }

/* ---- Focus registry for the main window ------------------------------------- */
enum { ID_NONE, ID_REFRESH, ID_CHK, ID_XMS, ID_GB, ID_REC, ID_KOFI, ID_COPY, ID_COPYTEXT, ID_SAVE, ID_RESTORE, ID_CLOSE, ID_MAX };
typedef struct { Rectangle r; bool enabled, used; } FocusItem;
static FocusItem focus_items[ID_MAX];
static int focus_id = ID_GB;
static int press_id;                   /* control the mouse went down on */

static void focus_register(int id, Rectangle r, bool enabled) { focus_items[id] = (FocusItem){ r, enabled, true }; }

static int focus_move_dir(int dir)
{
    FocusItem *cur = &focus_items[focus_id];
    if (!cur->used) return focus_id;
    float cx = cur->r.x + cur->r.width / 2, cy = cur->r.y + cur->r.height / 2;
    int best = focus_id; float best_score = 1e9f;
    for (int i = 1; i < ID_MAX; i++) {
        FocusItem *f = &focus_items[i];
        if (i == focus_id || !f->used || !f->enabled) continue;
        float fx = f->r.x + f->r.width / 2, fy = f->r.y + f->r.height / 2;
        float dx = fx - cx, dy = fy - cy, main, side;
        switch (dir) {
            case NAV_UP:    if (f->r.y + f->r.height > cur->r.y + 2) continue; main = -dy; side = fabsf(dx); break;
            case NAV_DOWN:  if (f->r.y < cur->r.y + cur->r.height - 2) continue; main = dy; side = fabsf(dx); break;
            case NAV_LEFT:  if (f->r.x + f->r.width > cur->r.x + 2) continue; main = -dx; side = fabsf(dy); break;
            default:        if (f->r.x < cur->r.x + cur->r.width - 2) continue; main = dx; side = fabsf(dy); break;
        }
        float score = main + side * 2.5f;
        if (score < best_score) { best_score = score; best = i; }
    }
    return best;
}
static void focus_tab(int step)
{
    for (int k = 0; k < ID_MAX; k++) {
        focus_id = (focus_id + step + ID_MAX) % ID_MAX;
        if (focus_id != ID_NONE && focus_items[focus_id].used && focus_items[focus_id].enabled) return;
    }
}

static void focus_ring(Rectangle r, int id, float radius)
{
    if (!show_focus || blocked || focus_id != id) return;
    Rectangle o = grow(r, 3);
    rrect(o, radius + 3, T.accent);
    rrect(grow(r, 1), radius + 1, T.bg);
}

/* ---- Widgets ------------------------------------------------------------------- */
enum { BTN_NORMAL, BTN_PRIMARY };

/* Mouse click (press and release on the control) or A / Enter while focused. */
static bool clicked(int id, Rectangle r, bool enabled)
{
    if (!enabled || blocked) return false;
    bool h = hover(r);
    if (in.mouse_pressed && h) { press_id = id; focus_id = id; }
    bool click = in.mouse_released && h && press_id == id;
    bool key = in.act && focus_id == id && show_focus;
    return click || key;
}

static bool ui_button(int id, Rectangle r, const char *label, bool enabled, int style, int font)
{
    focus_register(id, r, enabled);
    bool h = enabled && !blocked && hover(r);
    bool down = h && IsMouseButtonDown(MOUSE_BUTTON_LEFT) && press_id == id;
    focus_ring(r, id, 4);
    Color fill, border, fg;
    if (style == BTN_PRIMARY && enabled) {
        fill = down ? mix(T.accent, rgb(0, 0, 0), 0.15f) : h ? mix(T.accent, rgb(255, 255, 255), 0.12f) : T.accent;
        border = fill; fg = T.on_accent;
    } else {
        fill = !enabled ? T.btn : down ? mix(T.btn_hover, T.accent, 0.2f) : h ? T.btn_hover : T.btn;
        border = h ? T.accent : T.btn_border; fg = enabled ? T.fg : T.sub;
    }
    box_border(r, 4, fill, border);
    float tw = text_w(font, label);
    text_at(font, label, r.x + (r.width - tw) / 2, r.y + (r.height - FONTS[font].px) / 2 - 1, fg);
    return clicked(id, r, enabled);
}

/* Circular-arrow "refresh" button */
static bool ui_refresh(int id, Rectangle r, bool enabled)
{
    focus_register(id, r, enabled);
    bool h = enabled && !blocked && hover(r);
    focus_ring(r, id, 4);
    box_border(r, 4, h ? T.btn_hover : T.btn, h ? T.accent : T.btn_border);
    Vector2 c = { (r.x + r.width / 2) * S, (r.y + r.height / 2) * S };
    float rad = 7.5f * S, th = 1.8f * S;
    Color col = enabled ? T.fg : T.sub;
    DrawRing(c, rad - th, rad, -60, 230, 32, col);
    float a = -60 * DEG2RAD;
    Vector2 tip = { c.x + cosf(a) * (rad - th / 2), c.y + sinf(a) * (rad - th / 2) };
    float hs = 4.2f * S;
    DrawTriangle((Vector2){ tip.x - hs, tip.y - hs * 0.9f }, (Vector2){ tip.x - hs * 0.2f, tip.y + hs * 1.2f },
                 (Vector2){ tip.x + hs * 1.1f, tip.y - hs * 0.1f }, col);
    return clicked(id, r, enabled);
}

static void draw_check(Rectangle b, Color c)
{
    float s = S;
    Vector2 p1 = { (b.x + 3) * s, (b.y + b.height * 0.52f) * s }, p2 = { (b.x + b.width * 0.42f) * s, (b.y + b.height - 3.5f) * s },
            p3 = { (b.x + b.width - 3) * s, (b.y + 3.5f) * s };
    DrawLineEx(p1, p2, 2.0f * s, c);
    DrawLineEx(p2, p3, 2.0f * s, c);
}

static bool ui_checkbox(int id, Rectangle r, const char *label, bool *value, bool enabled)
{
    focus_register(id, r, enabled);
    focus_ring(r, id, 3);
    Rectangle b = R(r.x + 1, r.y + (r.height - 16) / 2, 16, 16);
    bool h = enabled && !blocked && hover(r);
    Color border = !enabled ? T.btn_border : (*value || h) ? T.accent : T.field_border;
    Color fill = (*value && enabled) ? T.accent : T.field;
    box_border(b, 3, fill, border);
    if (*value) draw_check(b, enabled ? T.on_accent : T.sub);
    text_at(F_UI, label, r.x + 24, r.y + (r.height - FONTS[F_UI].px) / 2 - 1, enabled ? T.fg : T.sub);
    if (clicked(id, r, enabled)) { *value = !*value; return true; }
    return false;
}

/* Dropdown list (open popup is drawn and handled separately, above everything) */
typedef struct { bool open; int id; Rectangle r; int *items; int count; int *sel; int hot; int scroll; bool changed; } Popup;
static Popup pop;
#define POP_ITEM_H 26
#define POP_MAX    10

static void popup_rect(Rectangle *out, int *visible)
{
    int vis = pop.count < POP_MAX ? pop.count : POP_MAX;
    float h = vis * POP_ITEM_H + 4;
    float y = pop.r.y + pop.r.height + 2;
    float win_h = GetScreenHeight() / S;
    if (y + h > win_h - 4) y = pop.r.y - h - 2;
    if (y < 2) y = 2;
    *out = R(pop.r.x, y, fmaxf(pop.r.width, 90), h);
    *visible = vis;
}

static bool ui_combo(int id, Rectangle r, int *items, int count, int *sel, bool enabled, bool show_value)
{
    focus_register(id, r, enabled);
    bool h = enabled && !blocked && hover(r);
    focus_ring(r, id, 4);
    bool is_open = pop.open && pop.id == id;
    box_border(r, 4, enabled ? T.field : T.bg, (h || is_open) ? T.accent : T.field_border);
    char v[32] = "";
    if (show_value && *sel >= 0 && *sel < count) snprintf(v, sizeof v, "%d GB", items[*sel]);
    text_at(F_UI, v, r.x + 9, r.y + (r.height - FONTS[F_UI].px) / 2 - 1, enabled ? T.fg : T.sub);
    /* chevron */
    float cx = (r.x + r.width - 14) * S, cy = (r.y + r.height / 2) * S, k = 3.6f * S;
    Color cc = enabled ? T.fg : T.sub;
    DrawLineEx((Vector2){ cx - k, cy - k * 0.5f }, (Vector2){ cx, cy + k * 0.6f }, 1.6f * S, cc);
    DrawLineEx((Vector2){ cx, cy + k * 0.6f }, (Vector2){ cx + k, cy - k * 0.5f }, 1.6f * S, cc);

    bool changed = false;
    if (pop.changed && pop.id == id) { pop.changed = false; changed = true; }
    if (!enabled || blocked || count == 0) return changed;
    /* Left / Right on a focused dropdown changes the value directly (handy with a controller) */
    if (show_focus && focus_id == id && (in.nav == NAV_LEFT || in.nav == NAV_RIGHT)) {
        int ns = *sel + (in.nav == NAV_RIGHT ? 1 : -1);
        if (ns >= 0 && ns < count) { *sel = ns; changed = true; }
        in.nav = NAV_NONE;
    }
    if (clicked(id, r, enabled)) {
        pop = (Popup){ .open = true, .id = id, .r = r, .items = items, .count = count, .sel = sel,
                       .hot = *sel < 0 ? 0 : *sel };
        int vis = count < POP_MAX ? count : POP_MAX;
        pop.scroll = pop.hot - vis / 2;
        if (pop.scroll > count - vis) pop.scroll = count - vis;
        if (pop.scroll < 0) pop.scroll = 0;
        in.act = false;
    }
    return changed;
}

/* Handles the open dropdown list. Called before the window's controls, so it gets the input first. */
static void popup_input(void)
{
    if (!pop.open) return;
    Rectangle pr; int vis;
    popup_rect(&pr, &vis);
    if (in.nav == NAV_UP && pop.hot > 0) pop.hot--;
    if (in.nav == NAV_DOWN && pop.hot < pop.count - 1) pop.hot++;
    if (in.wheel != 0 && hover(pr)) {
        pop.scroll -= (int)in.wheel;
        if (pop.scroll > pop.count - vis) pop.scroll = pop.count - vis;
        if (pop.scroll < 0) pop.scroll = 0;
    } else {
        if (pop.hot < pop.scroll) pop.scroll = pop.hot;
        if (pop.hot >= pop.scroll + vis) pop.scroll = pop.hot - vis + 1;
    }
    if (hover(pr) && (GetMouseDelta().x != 0 || GetMouseDelta().y != 0 || in.mouse_pressed)) {
        int i = pop.scroll + (int)((in.mouse.y - pr.y - 2) / POP_ITEM_H);
        if (i >= 0 && i < pop.count) pop.hot = i;
    }
    bool choose = in.act || (in.mouse_released && hover(pr) && press_id == -1);
    if (in.mouse_pressed) {
        if (hover(pr)) press_id = -1;
        else { pop.open = false; press_id = 0; }       /* click outside closes the list */
    }
    if (choose) {
        if (*pop.sel != pop.hot) { *pop.sel = pop.hot; pop.changed = true; }
        pop.open = false;
    }
    if (in.back) pop.open = false;
    blocked = true;
}

static void popup_draw(void)
{
    if (!pop.open) return;
    Rectangle pr; int vis;
    popup_rect(&pr, &vis);
    rrect(R(pr.x + 2, pr.y + 3, pr.width, pr.height), 5, (Color){ 0, 0, 0, 70 });
    box_border(pr, 5, T.field, T.field_border);
    for (int k = 0; k < vis; k++) {
        int i = pop.scroll + k;
        Rectangle ir = R(pr.x + 2, pr.y + 2 + k * POP_ITEM_H, pr.width - 4, POP_ITEM_H);
        bool hot = i == pop.hot;
        if (hot) rrect(ir, 3, T.accent);
        char v[32];
        snprintf(v, sizeof v, "%d GB", pop.items[i]);
        text_at(F_UI, v, ir.x + 8, ir.y + (POP_ITEM_H - FONTS[F_UI].px) / 2 - 1, hot ? T.on_accent : T.fg);
        if (i == *pop.sel && !hot) DrawRectangleRec(SR(R(ir.x, ir.y + 5, 3, POP_ITEM_H - 10)), T.accent);
    }
    if (pop.count > vis) {   /* scroll bar */
        float th = pr.height * vis / pop.count, ty = pr.y + (pr.height - th) * pop.scroll / (pop.count - vis);
        rrect(R(pr.x + pr.width - 5, ty + 2, 3, th - 4), 1.5f, T.sub);
    }
}

static bool ui_link(int id, Rectangle r, const char *label)
{
    float tw = text_w(F_UI, label);
    Rectangle hit = R(r.x, r.y, fminf(tw, r.width), r.height);
    focus_register(id, hit, true);
    focus_ring(hit, id, 2);
    bool h = !blocked && hover(hit);
    if (h) SetMouseCursor(MOUSE_CURSOR_POINTING_HAND);
    text_at(F_UI, label, r.x, r.y + 2, T.link);
    if (h || (show_focus && focus_id == id && !blocked)) {
        float y = r.y + 2 + line_h(F_UI) - 3;
        DrawRectangleRec(SR(R(r.x, y, tw, 1)), T.link);
    }
    return clicked(id, hit, true);
}

/* =============================================================================
 *  Dialogs (in-window, so they also work in Game Mode and with a controller)
 * ============================================================================= */
#define DLG_MAX_BTNS 4
typedef void (*DlgCb)(int choice);
typedef void (*DlgTick)(void);
typedef struct { char label[48]; int id; bool primary, enabled; } DlgBtn;
static struct {
    bool open;
    char title[160];
    char msg[4096];
    DlgBtn btn[DLG_MAX_BTNS];
    int nb, focus, cancel_id;
    DlgCb cb;
    DlgTick tick;           /* called once a second while open */
    double next_tick;
    bool wide;
} dlg;

/* Opens a dialog. Varargs: label, id pairs, ending with NULL. The first button is the default. */
static void dialog(const char *title, DlgCb cb, int cancel_id, const char *fmt_msg, ...)
{
    memset(&dlg, 0, sizeof dlg);
    dlg.open = true;
    dlg.cb = cb;
    dlg.cancel_id = cancel_id;
    snprintf(dlg.title, sizeof dlg.title, "%s", title);
    va_list ap;
    va_start(ap, fmt_msg);
    vsnprintf(dlg.msg, sizeof dlg.msg, fmt_msg, ap);
    va_end(ap);
    pop.open = false;
}
static void dialog_button(const char *label, int id, bool primary)
{
    if (dlg.nb >= DLG_MAX_BTNS) return;
    DlgBtn *b = &dlg.btn[dlg.nb++];
    snprintf(b->label, sizeof b->label, "%s", label);
    b->id = id; b->primary = primary; b->enabled = true;
    if (primary) dlg.focus = dlg.nb - 1;
}
static void dialog_enable(int id, bool on)
{
    for (int i = 0; i < dlg.nb; i++) if (dlg.btn[i].id == id) dlg.btn[i].enabled = on;
    if (!dlg.btn[dlg.focus].enabled)
        for (int i = 0; i < dlg.nb; i++) if (dlg.btn[i].enabled) { dlg.focus = i; break; }
}
static void dialog_ticker(DlgTick t) { dlg.tick = t; dlg.next_tick = GetTime() + 1.0; }
static void dialog_text(const char *fmt, ...)
{
    va_list ap;
    va_start(ap, fmt);
    vsnprintf(dlg.msg, sizeof dlg.msg, fmt, ap);
    va_end(ap);
}

static void dialog_layout(Rectangle *panel, Rectangle *msg_r, Rectangle btns[], float *title_y)
{
    float win_w = GetScreenWidth() / S, win_h = GetScreenHeight() / S;
    float pw = dlg.wide ? 580 : 480;
    if (pw > win_w - 20) pw = win_w - 20;
    static char lines[MAX_LINES][512];
    int n = wrap(F_UI, dlg.msg, pw - 40, lines);
    float mh = n * line_h(F_UI);
    float ph = 20 + line_h(F_DLG_B) + 12 + mh + (dlg.nb > 0 ? 20 + 34 + 18 : 22);
    if (ph > win_h - 20) ph = win_h - 20;
    *panel = R((win_w - pw) / 2, (win_h - ph) / 2, pw, ph);
    *title_y = panel->y + 18;
    *msg_r = R(panel->x + 20, *title_y + line_h(F_DLG_B) + 12, pw - 40, mh);
    float x = panel->x + pw - 20, by = panel->y + ph - 18 - 34;
    for (int i = dlg.nb - 1; i >= 0; i--) {
        float bw = fmaxf(96, text_w(F_UI, dlg.btn[i].label) + 30);
        x -= bw;
        btns[i] = R(x, by, bw, 34);
        x -= 8;
    }
    if (x < panel->x + 12) {   /* too many / too wide: spread evenly */
        float bw = (pw - 40 - 8 * (dlg.nb - 1)) / dlg.nb;
        for (int i = 0; i < dlg.nb; i++) btns[i] = R(panel->x + 20 + i * (bw + 8), by, bw, 34);
    }
}

static void dialog_finish(int id)
{
    DlgCb cb = dlg.cb;
    dlg.open = false;
    if (cb) cb(id);
}

static void dialog_input(void)
{
    if (!dlg.open) return;
    blocked = true;
    if (dlg.tick && GetTime() >= dlg.next_tick) { dlg.next_tick = GetTime() + 1.0; dlg.tick(); if (!dlg.open) return; }
    Rectangle panel, msg, b[DLG_MAX_BTNS]; float ty;
    dialog_layout(&panel, &msg, b, &ty);
    if (in.nav == NAV_LEFT || in.nav == NAV_UP || in.tab < 0) {
        for (int i = dlg.focus - 1; i >= 0; i--) if (dlg.btn[i].enabled) { dlg.focus = i; break; }
    }
    if (in.nav == NAV_RIGHT || in.nav == NAV_DOWN || in.tab > 0) {
        for (int i = dlg.focus + 1; i < dlg.nb; i++) if (dlg.btn[i].enabled) { dlg.focus = i; break; }
    }
    for (int i = 0; i < dlg.nb; i++) {
        if (!dlg.btn[i].enabled) continue;
        if (in.mouse_pressed && hover(b[i])) { press_id = 1000 + i; dlg.focus = i; }
        if (in.mouse_released && hover(b[i]) && press_id == 1000 + i) { dialog_finish(dlg.btn[i].id); return; }
    }
    if (in.act && dlg.nb > 0 && dlg.btn[dlg.focus].enabled) { dialog_finish(dlg.btn[dlg.focus].id); return; }
    if (in.back && dlg.cancel_id) {
        for (int i = 0; i < dlg.nb; i++)
            if (dlg.btn[i].id == dlg.cancel_id && dlg.btn[i].enabled) { dialog_finish(dlg.cancel_id); return; }
    }
}

static void dialog_draw(void)
{
    if (!dlg.open) return;
    DrawRectangle(0, 0, GetScreenWidth(), GetScreenHeight(), (Color){ 0, 0, 0, 120 });
    Rectangle panel, msg, b[DLG_MAX_BTNS]; float ty;
    dialog_layout(&panel, &msg, b, &ty);
    rrect(R(panel.x + 3, panel.y + 5, panel.width, panel.height), 8, (Color){ 0, 0, 0, 80 });
    box_border(panel, 8, T.bg, T.btn_border);
    text_at(F_DLG_B, dlg.title, panel.x + 20, ty, T.fg);
    float msg_bottom = dlg.nb > 0 ? b[0].y - 8 : panel.y + panel.height - 12;
    BeginScissorMode((int)(panel.x * S), (int)(msg.y * S), (int)(panel.width * S), (int)((msg_bottom - msg.y) * S));
    text_box(F_UI, dlg.msg, msg, T.fg, AL_LEFT, 0);
    EndScissorMode();
    for (int i = 0; i < dlg.nb; i++) {
        DlgBtn *d = &dlg.btn[i];
        bool h = d->enabled && hover(b[i]);
        if (show_focus && i == dlg.focus && d->enabled) {
            rrect(grow(b[i], 3), 7, T.accent);
            rrect(grow(b[i], 1), 5, T.bg);
        }
        Color fill = !d->enabled ? T.bg : d->primary ? (h ? mix(T.accent, rgb(255, 255, 255), 0.12f) : T.accent) : (h ? T.btn_hover : T.btn);
        Color border = d->primary && d->enabled ? fill : (h ? T.accent : T.btn_border);
        Color fg = !d->enabled ? T.sub : d->primary ? T.on_accent : T.fg;
        box_border(b[i], 4, fill, border);
        int f = d->primary && d->enabled ? F_BTN_B : F_UI;
        float tw = text_w(f, d->label);
        text_at(f, d->label, b[i].x + (b[i].width - tw) / 2, b[i].y + (34 - FONTS[f].px) / 2 - 1, fg);
    }
}

/* =============================================================================
 *  Application state
 * ============================================================================= */
static char  *json_path;
static long   current_mb, current_xms_mb;
static MemInfo mi;
static Plan   plan;
static int    sel_gb_index = -1;          /* index into plan.options */
static int    xms_items[64], xms_count, sel_xms_index = -1;
static bool   xms_on;
static char   steam_cfg[PATH_MAX];
static bool   steam_checked;
static char  *steam_mem;                  /* -Xmx / -Xms found in Steam's launch options ("" if none) */
static char   console_status[256];
static bool   console_status_error;
static bool   quit_requested;
static bool   finish_mode;                /* started by the Game Mode "Fix it for me" autostart */
static bool   ui_ready;                   /* game file found */

static int selected_gb(void) { return (sel_gb_index >= 0 && sel_gb_index < plan.count) ? plan.options[sel_gb_index] : 0; }
static int selected_xms(void)
{
    if (plan.xms != XMS_NORMAL || !xms_on) return 0;
    return (sel_xms_index >= 0 && sel_xms_index < xms_count) ? xms_items[sel_xms_index] : 0;
}

static int index_of(const int *items, int n, int v) { for (int i = 0; i < n; i++) if (items[i] == v) return i; return -1; }

/* Minimum dropdown: 4 GB up to (selected maximum - 1 GB). Keeps the choice if it still fits. */
static void update_xms_options(void)
{
    int keep = (sel_xms_index >= 0 && sel_xms_index < xms_count) ? xms_items[sel_xms_index] : DEFAULT_XMS_GB;
    int top = selected_gb() - 1;
    xms_count = 0;
    for (int g = MIN_XMS_GB; g <= top && xms_count < 64; g++) xms_items[xms_count++] = g;
    if (xms_count == 0) { sel_xms_index = -1; return; }
    sel_xms_index = index_of(xms_items, xms_count, keep < top ? keep : top);
    if (sel_xms_index < 0) sel_xms_index = 0;
}

static void check_steam(void)
{
    free(steam_mem);
    steam_mem = NULL;
    steam_checked = steam_config_file(steam_cfg, sizeof steam_cfg);
    if (!steam_checked) { steam_cfg[0] = 0; return; }
    char *t = read_file(steam_cfg, NULL);
    char *lo = t ? steam_launch_options(t) : NULL;
    steam_mem = lo ? memory_args(lo) : NULL;
    free(lo); free(t);
}

static bool steam_restore_available(void)
{
    if (!steam_cfg[0]) return false;
    char *old = steam_backed_up_value(steam_cfg);
    if (!old) return false;
    char *t = read_file(steam_cfg, NULL);
    char *cur = t ? steam_launch_options(t) : NULL;
    bool diff = !cur || strcmp(cur, old) != 0;
    free(old); free(cur); free(t);
    return diff;
}

static bool backup_exists(void)
{
    if (!json_path) return false;
    char b[PATH_MAX];
    snprintf(b, sizeof b, "%s.bak", json_path);
    return file_exists(b);
}

static void load_values(void)
{
    char *text = read_file(json_path, NULL);
    MemArg x = text ? find_mem_arg(text, 'x') : (MemArg){0};
    MemArg s = text ? find_mem_arg(text, 's') : (MemArg){0};
    current_mb = x.found ? x.mb : 0;
    current_xms_mb = s.found ? s.mb : 0;
    free(text);
    mi = read_memory_info();
    plan = make_plan(&mi);
    check_steam();
    sel_gb_index = index_of(plan.options, plan.count, plan.recommended);
    if (sel_gb_index < 0 && plan.count > 0) sel_gb_index = 0;
    sel_xms_index = -1;
    update_xms_options();
    sel_xms_index = index_of(xms_items, xms_count, DEFAULT_XMS_GB);
    xms_on = plan.xms == XMS_NORMAL && plan.tier >= 12;
    if (plan.tier == 200) xms_on = true;
    if (plan.xms == XMS_LOCKED_OFF) sel_xms_index = -1;
}

/* Refresh button: read everything again, keeping the user's choices where they still apply. */
static void refresh_values(void)
{
    int keep_max = selected_gb();
    int keep_min = (sel_xms_index >= 0 && sel_xms_index < xms_count) ? xms_items[sel_xms_index] : 0;
    bool keep_tick = xms_on;
    load_values();
    if (keep_max) { int i = index_of(plan.options, plan.count, keep_max); if (i >= 0) sel_gb_index = i; }
    update_xms_options();
    if (plan.xms == XMS_NORMAL) xms_on = keep_tick;
    if (keep_min) { int i = index_of(xms_items, xms_count, keep_min); if (i >= 0) sel_xms_index = i; }
}

/* =============================================================================
 *  Guards: Project Zomboid / Steam must be closed (same flow as Windows)
 * ============================================================================= */
static void (*guard_next)(void);
static void (*steam_next)(bool fix_steam);
static int  guard_seconds;
static char steam_reason[128], steam_skip_means[160];

enum { B_OK = 1, B_CANCEL, B_PROCEED, B_CLOSE_STEAM, B_SKIP, B_YES, B_NO, B_NEVER, B_FIX, B_MANUAL,
       B_SEARCH, B_PICK, B_EXIT, B_SWITCH, B_STAY };

static const char *GAME_ASK = "Project Zomboid (ProjectZomboid64) is currently running.\n\n"
                              "Please close Project Zomboid, then select Proceed.";

static void game_guard_tick(void)
{
    if (--guard_seconds > 0) {
        dialog_text("Project Zomboid is still running.\n\nChecking again in %d second%s...", guard_seconds, guard_seconds == 1 ? "" : "s");
        return;
    }
    dlg.tick = NULL;
    if (!is_game_running()) { dlg.open = false; guard_next(); return; }
    dialog_text("Project Zomboid is still running.\n\n%s", GAME_ASK);
    dialog_enable(B_PROCEED, true);
}
static void game_guard_cb(int id)
{
    if (id != B_PROCEED) return;
    if (!is_game_running()) { guard_next(); return; }
    /* still running: wait 5 seconds, then check again (loops until closed or cancelled) */
    dialog("PZ Memory Tool - Project Zomboid is running", game_guard_cb, B_CANCEL, "%s", "");
    dialog_button("Proceed", B_PROCEED, true);
    dialog_button("Cancel", B_CANCEL, false);
    dialog_enable(B_PROCEED, false);
    guard_seconds = GAME_CHECK_WAIT_S;
    dialog_text("Project Zomboid is still running.\n\nChecking again in %d seconds...", guard_seconds);
    dialog_ticker(game_guard_tick);
}
static void ensure_game_closed(void (*next)(void))
{
    guard_next = next;
    if (!is_game_running()) { next(); return; }
    dialog("PZ Memory Tool - Project Zomboid is running", game_guard_cb, B_CANCEL, "%s", GAME_ASK);
    dialog_button("Proceed", B_PROCEED, true);
    dialog_button("Cancel", B_CANCEL, false);
}

static int steam_close_left;
static void steam_dialog(const char *msg);
static void steam_close_tick(void)
{
    if (!is_steam_running()) {
        dlg.tick = NULL;
        dlg.open = false;
        usleep(1000 * 1000);    /* give Steam a moment to finish writing its settings */
        steam_next(true);
        return;
    }
    if (--steam_close_left > 0) {
        dialog_text("Closing Steam...\n\nWaiting for Steam to finish closing (%ds).", steam_close_left);
        return;
    }
    dlg.tick = NULL;
    dialog_text("Steam is taking a long time to close.\n\n"
                "Close it from the Steam icon in the system tray (right-click > Exit Steam), then select Proceed. "
                "Or select \"Close Steam for me\" to try again.");
    dialog_enable(B_CLOSE_STEAM, true);
    dialog_enable(B_PROCEED, true);
}
static void steam_guard_cb(int id)
{
    if (id == B_SKIP) { steam_next(false); return; }
    if (id == B_PROCEED) {
        if (!is_steam_running()) { steam_next(true); return; }
        steam_dialog("Steam is still running.\n\nClose Steam (Steam > Exit Steam) and select Proceed, or select \"Close Steam for me\".");
        return;
    }
    if (id == B_CLOSE_STEAM) {
        steam_dialog("Closing Steam...\n\nWaiting for Steam to finish closing. This can take a few seconds.");
        dialog_enable(B_CLOSE_STEAM, false);
        dialog_enable(B_PROCEED, false);
        dlg.focus = 2;
        steam_request_shutdown();
        steam_close_left = STEAM_CLOSE_WAIT_S;
        dialog_ticker(steam_close_tick);
    }
}
static void steam_dialog(const char *msg)
{
    dialog("PZ Memory Tool - Steam is running", steam_guard_cb, B_SKIP, "%s", msg);
    dialog_button("Close Steam for me", B_CLOSE_STEAM, true);
    dialog_button("Proceed", B_PROCEED, false);
    dialog_button("Skip", B_SKIP, false);
}
static void ensure_steam_closed(const char *reason, const char *skip_means, void (*next)(bool))
{
    steam_next = next;
    snprintf(steam_reason, sizeof steam_reason, "%s", reason);
    snprintf(steam_skip_means, sizeof steam_skip_means, "%s", skip_means);
    if (!is_steam_running()) { next(true); return; }
    char msg[1024];
    snprintf(msg, sizeof msg,
             "Steam is running. To %s Project Zomboid's Steam launch options, Steam must be fully closed first, "
             "otherwise Steam puts the old settings back.\n\n"
             "Select \"Close Steam for me\", or close Steam yourself (Steam > Exit Steam) and select Proceed. "
             "Select Skip to %s.", reason, skip_means);
    steam_dialog(msg);
}

/* =============================================================================
 *  Save
 * ============================================================================= */
static int  pending_game_mode_fix;        /* 0 none, 1 = "Fix it for me" chosen, 2 = "I'll clear it myself" */
static char pending_action[16];

static void steam_report(bool fix_steam, Buf *out)
{
    if (!steam_mem || !steam_mem[0]) {
        buf_adds(out, steam_checked ? "Steam launch options: no memory settings found, nothing to change."
                                    : "Steam launch options: could not be checked (Steam settings not found).");
        return;
    }
    if (pending_game_mode_fix == 1) {
        buf_addf(out, "Steam launch options: %s will be removed in Desktop Mode (\"Fix it for me\").", steam_mem);
        return;
    }
    if (pending_game_mode_fix == 2) {
        buf_addf(out, "Steam launch options were NOT changed and still contain %s. These override the setting above. "
                      "Remove them in Steam: select Project Zomboid in your Library > gear icon (Settings) > Properties > "
                      "General > Launch Options.", steam_mem);
        return;
    }
    char backup[PATH_MAX] = "";
    if (fix_steam && steam_remove_memory_args(steam_cfg, backup, sizeof backup)) {
        buf_addf(out, "Steam launch options: successfully removed %s.\nSteam backup: %s", steam_mem, backup);
        return;
    }
    buf_addf(out, "Steam launch options were NOT changed and still contain %s. These override the setting above. "
                  "Remove them in Steam: right-click Project Zomboid > Properties > General > Launch Options.", steam_mem);
}

static void after_save_cb(int id);

static void do_save(bool fix_steam)
{
    int gb = selected_gb(), xms = selected_xms();
    char backup[PATH_MAX];
    snprintf(backup, sizeof backup, "%s.bak", json_path);
    char *text = read_file(json_path, NULL);
    if (!text) {
        dialog("PZ Memory Tool", NULL, B_OK, "Could not read the file:\n%s", json_path);
        dialog_button("OK", B_OK, true);
        return;
    }
    if (!file_exists(backup) && !copy_file(json_path, backup)) {
        dialog("PZ Memory Tool", NULL, B_OK, "Could not save the file:\nthe backup %s could not be created.", backup);
        dialog_button("OK", B_OK, true);
        free(text);
        return;
    }
    char *updated = set_memory(text, gb, xms);
    free(text);
    bool ok = write_file(json_path, updated, strlen(updated));
    free(updated);
    if (!ok) {
        dialog("PZ Memory Tool", NULL, B_OK, "Could not save the file:\n%s\n\nCheck that you are allowed to change files in that folder.", json_path);
        dialog_button("OK", B_OK, true);
        return;
    }

    /* Read the file back and check both values really are in it */
    char *check = read_file(json_path, NULL);
    MemArg cx = check ? find_mem_arg(check, 'x') : (MemArg){0}, cs = check ? find_mem_arg(check, 's') : (MemArg){0};
    free(check);
    bool xmx_ok = cx.found && cx.mb == gb * 1024L;
    bool xms_ok = xms > 0 ? (cs.found && cs.mb == xms * 1024L && cs.pos < cx.pos) : !cs.found;

    Buf m = {0};
    buf_adds(&m, "Saved. Project Zomboid will now use:\n\n");
    if (xms > 0) buf_addf(&m, "     Minimum memory (-Xms):   %d GB   (-Xms%dm)\n", xms, xms * 1024);
    else buf_addf(&m, "     Minimum memory (-Xms):   %s\n",
                  plan.xms == XMS_HIDDEN ? "not set (not used on Steam Deck)" :
                  plan.tier == 8 ? "not set (8 GB system)" : "not set (turned off)");
    buf_addf(&m, "     Maximum memory (-Xmx):   %d GB   (-Xmx%dm)\n\n", gb, gb * 1024);
    steam_report(fix_steam, &m);
    buf_addf(&m, "\n\nA backup of the original file is at:\n%s", backup);
    if (!(xmx_ok && xms_ok))
        buf_addf(&m, "\n\nWARNING: the file could not be fully updated (%s%snot as expected). "
                     "Please send your ProjectZomboid64.json to ComfyPlacebo so this can be fixed.",
                 xmx_ok ? "" : "-Xmx ", xms_ok ? "" : "-Xms ");

    if (pending_game_mode_fix == 1) {
        buf_adds(&m, "\n\nNext: your device switches to Desktop Mode, clears Steam's launch options there, "
                     "and comes back to Game Mode by itself. This takes about a minute.");
        dialog("PZ Memory Tool", after_save_cb, B_CANCEL, "%s", m.s);
        dialog_button("Switch to Desktop Mode", B_SWITCH, true);
        dialog_button("Not now", B_CANCEL, false);
        dlg.wide = true;
    } else {
        dialog("PZ Memory Tool", after_save_cb, B_OK, "%s", m.s);
        dialog_button("OK", B_OK, true);
        dlg.wide = true;
    }
    buf_free(&m);
    load_values();
}

static void after_save_cb(int id)
{
    if (pending_game_mode_fix == 1) {
        if (id == B_SWITCH) {
            if (pending_fix_write(pending_action)) {
                switch_to_desktop_mode();
                quit_requested = true;
            } else {
                dialog("PZ Memory Tool", NULL, B_OK, "Could not prepare the Desktop Mode step. "
                       "Remove the memory settings in Steam yourself: select Project Zomboid > gear icon (Settings) > "
                       "Properties > General > Launch Options.");
                dialog_button("OK", B_OK, true);
            }
        }
    }
    pending_game_mode_fix = 0;
}

static void save_with_steam(bool fix) { do_save(fix); }

/* Game Mode: Steam can't be closed here, so offer the automatic Desktop Mode round trip */
static void (*gm_next)(bool);
static void game_mode_cb(int id)
{
    if (id == B_CANCEL) return;
    if (id == B_FIX) pending_game_mode_fix = 1;
    else if (id == B_MANUAL) pending_game_mode_fix = 2;
    else pending_game_mode_fix = 0;
    gm_next(false);
}
static void game_mode_dialog(const char *what, void (*next)(bool))
{
    gm_next = next;
    bool auto_ok = can_switch_session();
    dialog("PZ Memory Tool - Game Mode", game_mode_cb, B_CANCEL,
           "Steam's launch options for Project Zomboid contain %s. They override the game file, and Steam can't be "
           "closed in Game Mode to %s them.\n\n"
           "%s"
           "I'll clear it myself: saves the game file. Then select Project Zomboid in your Library > gear icon (Settings) > "
           "Properties > General > Launch Options, and delete %s.\n\n"
           "Skip: saves the game file only.",
           steam_mem, what,
           auto_ok ? "Fix it for me: saves the game file, switches to Desktop Mode, clears the launch options there and "
                     "returns to Game Mode by itself.\n\n" : "",
           steam_mem);
    if (auto_ok) dialog_button("Fix it for me", B_FIX, true);
    dialog_button("I'll clear it myself", B_MANUAL, !auto_ok);
    dialog_button("Skip", B_SKIP, false);
    dialog_button("Cancel", B_CANCEL, false);
    dlg.wide = true;
}

static void save_after_game(void)
{
    check_steam();     /* Steam launch options with -Xmx / -Xms need Steam closed, or Steam writes them back */
    pending_game_mode_fix = 0;
    if (!steam_mem || !steam_mem[0]) { do_save(false); return; }
    if (in_game_mode()) { snprintf(pending_action, sizeof pending_action, "remove"); game_mode_dialog("remove", save_with_steam); return; }
    ensure_steam_closed("remove the memory settings from", "save without changing the Steam launch options", save_with_steam);
}
static void save(void) { if (selected_gb() >= MIN_ALLOC_GB || (selected_gb() > 0)) ensure_game_closed(save_after_game); }

/* =============================================================================
 *  Restore backup
 * ============================================================================= */
static bool restore_file_wanted, restore_steam_wanted;

static void do_restore(bool do_steam)
{
    Buf done = {0};
    if (restore_file_wanted) {
        char backup[PATH_MAX];
        snprintf(backup, sizeof backup, "%s.bak", json_path);
        if (!copy_file(backup, json_path)) {
            dialog("PZ Memory Tool", NULL, B_OK, "Restore failed:\nthe game file could not be written.");
            dialog_button("OK", B_OK, true);
            buf_free(&done);
            return;
        }
        buf_adds(&done, "Game file restored.");
    }
    if (restore_steam_wanted) {
        if (done.len) buf_adds(&done, "\n\n");
        if (pending_game_mode_fix == 1) buf_adds(&done, "Steam launch options will be restored in Desktop Mode (\"Fix it for me\").");
        else if (!do_steam) buf_adds(&done, "Steam launch options were NOT restored (Steam was still running).");
        else if (steam_restore_from_backup(steam_cfg)) {
            char *t = read_file(steam_cfg, NULL);
            char *lo = t ? steam_launch_options(t) : NULL;
            buf_addf(&done, "Steam launch options restored to: %s", lo ? lo : "");
            free(lo); free(t);
        } else buf_adds(&done, "Steam launch options could not be restored.");
    }
    load_values();
    if (pending_game_mode_fix == 1) {
        buf_adds(&done, "\n\nNext: your device switches to Desktop Mode, restores Steam's launch options there, "
                        "and comes back to Game Mode by itself.");
        dialog("PZ Memory Tool", after_save_cb, B_CANCEL, "%s", done.s ? done.s : "");
        dialog_button("Switch to Desktop Mode", B_SWITCH, true);
        dialog_button("Not now", B_CANCEL, false);
    } else {
        dialog("PZ Memory Tool", NULL, B_OK, "%s", done.s ? done.s : "Nothing to restore.");
        dialog_button("OK", B_OK, true);
    }
    buf_free(&done);
}

static void restore_after_game(void)
{
    pending_game_mode_fix = 0;
    if (!restore_steam_wanted) { do_restore(false); return; }
    if (in_game_mode()) {
        snprintf(pending_action, sizeof pending_action, "restore");
        free(steam_mem);
        steam_mem = strdup("your previous settings");
        game_mode_dialog("restore", do_restore);
        return;
    }
    ensure_steam_closed("restore", "restore only the game file and leave the Steam launch options as they are", do_restore);
}
static void restore_confirm_cb(int id) { if (id == B_YES) ensure_game_closed(restore_after_game); }
static void restore_backup(void)
{
    restore_file_wanted = backup_exists();
    restore_steam_wanted = steam_restore_available();
    if (!restore_file_wanted && !restore_steam_wanted) return;
    dialog("PZ Memory Tool", restore_confirm_cb, B_NO, "Restore the original settings from the backup?\n\n%s%s",
           restore_file_wanted ? "  - the game file (ProjectZomboid64.json)\n" : "",
           restore_steam_wanted ? "  - Project Zomboid's Steam launch options\n" : "");
    dialog_button("Yes", B_YES, true);
    dialog_button("No", B_NO, false);
}

/* =============================================================================
 *  Console Copier
 * ============================================================================= */
static void copy_console(bool as_text)
{
    char console[PATH_MAX];
    snprintf(console, sizeof console, "%s/Zomboid/console.txt", home_dir());
    console_status_error = false;
    struct stat st;
    if (stat(console, &st) != 0) {
        snprintf(console_status, sizeof console_status, "console.txt not found.\nStart Project Zomboid once to create it.");
        console_status_error = true;
        return;
    }
    /* A snapshot is copied first, so the game can keep writing its log */
    char dir[PATH_MAX], copy[PATH_MAX];
    const char *xc = getenv("XDG_CACHE_HOME");
    if (xc && *xc) snprintf(dir, sizeof dir, "%s/PZMemoryTool", xc);
    else snprintf(dir, sizeof dir, "%s/.cache/PZMemoryTool", home_dir());
    mkdir_p(dir);
    snprintf(copy, sizeof copy, "%s/console.txt", dir);
    size_t len = 0;
    char *data = read_file(console, &len);
    if (!data || !write_file(copy, data, len)) {
        free(data);
        snprintf(console_status, sizeof console_status, "Could not copy console.txt.");
        console_status_error = true;
        return;
    }
    bool ok = as_text ? clip_set_text(data, len) : clip_set_file(copy);
    free(data);
    if (!ok) {
        snprintf(console_status, sizeof console_status, "Could not use the clipboard.");
        console_status_error = true;
        return;
    }
    char size[32], when[32];
    long kb = (long)(len / 1024);
    if (kb >= 1024) snprintf(size, sizeof size, "%.1f MB", kb / 1024.0);
    else snprintf(size, sizeof size, "%ld KB", kb);
    strftime(when, sizeof when, "%H:%M", localtime(&st.st_mtime));
    snprintf(console_status, sizeof console_status, "%s\n(%s, %s)",
             as_text ? "Copied console.txt as text" : "Copied console.txt", size, when);
}

static void open_kofi(void)
{
    if (in_game_mode() && command_exists("steam")) {
        const char *a[] = { "steam", "steam://openurl/" KOFI_URL, NULL };
        spawn_detached(a);
        return;
    }
    const char *a[] = { "xdg-open", KOFI_URL, NULL };
    if (command_exists("xdg-open")) spawn_detached(a);
    else {
        dialog("PZ Memory Tool", NULL, B_OK, "Could not open your web browser. You can visit:\n%s", KOFI_URL);
        dialog_button("OK", B_OK, true);
    }
}

/* =============================================================================
 *  Finding the file when Steam doesn't know where it is
 * ============================================================================= */
typedef struct { pthread_t th; volatile bool running, done, cancel; char *result; char status[512]; int kind; } Job;
static Job job;
enum { JOB_SEARCH = 1, JOB_PICK };

static void search_dir(const char *dir, int depth)
{
    if (job.cancel || job.result || depth > 9) return;
    DIR *d = opendir(dir);
    if (!d) return;
    snprintf(job.status, sizeof job.status, "%s", dir);
    struct dirent *e;
    while (!job.cancel && !job.result && (e = readdir(d))) {
        const char *n = e->d_name;
        if (!strcmp(n, ".") || !strcmp(n, "..")) continue;
        /* skip folders that are huge and never contain the game */
        if (!strcmp(n, ".cache") || !strcmp(n, "compatdata") || !strcmp(n, "shadercache") || !strcmp(n, "proc") ||
            !strcmp(n, "sys") || !strcmp(n, ".git") || !strcmp(n, "node_modules") || !strcmp(n, "Trash")) continue;
        char p[PATH_MAX];
        if (snprintf(p, sizeof p, "%s/%s", dir, n) >= (int)sizeof p) continue;
        if (e->d_type == DT_DIR) search_dir(p, depth + 1);
        else if ((e->d_type == DT_REG || e->d_type == DT_UNKNOWN) && strcasecmp(n, JSON_NAME) == 0 && is_valid_pz_json(p))
            job.result = strdup(p);
    }
    closedir(d);
}
static void *job_main(void *arg)
{
    (void)arg;
    if (job.kind == JOB_SEARCH) {
        const char *roots[] = { home_dir(), "/run/media", "/media", "/mnt", "/home" };
        for (size_t i = 0; i < sizeof roots / sizeof *roots && !job.result && !job.cancel; i++) search_dir(roots[i], 0);
    } else {
        char start[PATH_MAX];
        snprintf(start, sizeof start, "%s/", home_dir());
        if (command_exists("kdialog")) {
            const char *a[] = { "kdialog", "--title", "Locate " JSON_NAME, "--getopenfilename", start,
                                JSON_NAME " (" JSON_NAME ")\nJSON files (*.json)", NULL };
            job.result = run_capture(a);
        } else if (command_exists("zenity")) {
            const char *a[] = { "zenity", "--file-selection", "--title=Locate " JSON_NAME,
                                "--file-filter=" JSON_NAME " | " JSON_NAME, "--file-filter=JSON files | *.json", NULL };
            job.result = run_capture(a);
        }
    }
    job.done = true;
    return NULL;
}
static void job_start(int kind)
{
    free(job.result);
    memset(&job, 0, sizeof job);
    job.kind = kind;
    job.running = true;
    pthread_create(&job.th, NULL, job_main, NULL);
}

static void not_found_dialog(void);
static void after_found(void);
static void pick_cb(int id);
static void pick_file(void)
{
    if (!command_exists("kdialog") && !command_exists("zenity")) {
        dialog("PZ Memory Tool - File not found", NULL, B_EXIT,
               "No file picker was found on this system.\n\nStart the tool with the file's location instead, for example:\n\n"
               "./PZMemoryTool \"/path/to/ProjectZomboid/ProjectZomboid64.json\"");
        dialog_button("Exit", B_EXIT, true);
        dlg.cb = pick_cb;
        return;
    }
    job_start(JOB_PICK);
    dialog("PZ Memory Tool", NULL, 0, "Choose %s in the file window that opened.", JSON_NAME);
}
static void pick_cb(int id)
{
    if (id == B_PICK) pick_file();
    else if (id == B_EXIT) quit_requested = true;
}
static void search_cb(int id) { if (id == B_CANCEL) job.cancel = true; }

static void job_poll(void)
{
    if (!job.running) return;
    if (job.kind == JOB_SEARCH && dlg.open && !job.done)
        dialog_text("Searching your home folder and drives for %s...\n\n%s", JSON_NAME, job.status);
    if (!job.done) return;
    pthread_join(job.th, NULL);
    job.running = false;
    dlg.open = false;
    char *r = job.result;
    job.result = NULL;
    if (job.kind == JOB_SEARCH) {
        if (r) { json_path = r; after_found(); return; }
        if (job.cancel) { not_found_dialog(); return; }
        dialog("PZ Memory Tool", pick_cb, B_EXIT, "The file was not found on any drive.\n\nDo you want to pick it yourself?");
        dialog_button("Pick the file", B_PICK, true);
        dialog_button("Exit", B_EXIT, false);
        return;
    }
    if (!r || !r[0]) { free(r); not_found_dialog(); return; }
    if (!is_valid_pz_json(r)) {
        free(r);
        dialog("PZ Memory Tool", pick_cb, B_EXIT, "That file does not look like Project Zomboid's %s.\n"
               "(It must contain the Project Zomboid main class and an -Xmx memory argument.)", JSON_NAME);
        dialog_button("Pick again", B_PICK, true);
        dialog_button("Exit", B_EXIT, false);
        return;
    }
    json_path = r;
    after_found();
}

static void not_found_cb(int id)
{
    if (id == B_SEARCH) {
        job_start(JOB_SEARCH);
        dialog("PZ Memory Tool - Searching", search_cb, B_CANCEL, "Searching...");
        dialog_button("Cancel", B_CANCEL, true);
    } else if (id == B_PICK) pick_file();
    else quit_requested = true;
}
static void not_found_dialog(void)
{
    dialog("PZ Memory Tool - File not found", not_found_cb, B_EXIT,
           "%s was not found in your Steam folders or in any Steam library.\n\n"
           "Search: search your home folder and drives for it\n"
           "Pick the file: choose it yourself\n"
           "Exit: close the tool", JSON_NAME);
    dialog_button("Search", B_SEARCH, true);
    dialog_button("Pick the file", B_PICK, false);
    dialog_button("Exit", B_EXIT, false);
}

/* =============================================================================
 *  First run: offer to add the tool to Steam (SteamOS)
 * ============================================================================= */
static void add_to_steam_cb(int id)
{
    if (id == B_NEVER) { settings_set("ask_add_to_steam", "never"); return; }
    if (id != B_YES) return;      /* Not now: ask again next time */
    if (steam_add_to_steam()) {
        settings_set("ask_add_to_steam", "done");
        dialog("PZ Memory Tool", NULL, B_OK,
               "PZ Memory Tool was sent to Steam. You'll find it in your Steam Library under \"Non-Steam\", "
               "also in Game Mode.\n\nIf you move this file later, add it to Steam again.");
    } else {
        dialog("PZ Memory Tool", NULL, B_OK,
               "Steam didn't accept the request. Make sure Steam is running and try again, or add it yourself: "
               "Steam > Games > Add a Non-Steam Game to My Library.");
    }
    dialog_button("OK", B_OK, true);
}
static void maybe_ask_add_to_steam(void)
{
    if (finish_mode || in_game_mode() || getenv("SteamGameId") || getenv("SteamAppId")) return;  /* already in Steam */
    if (!steam_add_to_steam_supported()) return;
    char *v = settings_get("ask_add_to_steam");
    bool skip = v && (!strcmp(v, "never") || !strcmp(v, "done"));
    free(v);
    if (skip) return;
    dialog("PZ Memory Tool - Add to Steam?", add_to_steam_cb, B_CANCEL,
           "Add PZ Memory Tool to Steam?\n\nThen you can open it from your Steam Library, also in Game Mode, "
           "and use your controller in it.");
    dialog_button("Yes", B_YES, true);
    dialog_button("Not now", B_CANCEL, false);
    dialog_button("Don't ask again", B_NEVER, false);
}

/* =============================================================================
 *  Finishing a Game Mode "Fix it for me" (runs once, automatically, in Desktop Mode)
 * ============================================================================= */
static int return_left;
static char finish_action[16];

static void finish_return_tick(void)
{
    if (--return_left > 0) {
        char *p = strstr(dlg.msg, "Returning to Game Mode in");
        if (p) snprintf(p, sizeof dlg.msg - (size_t)(p - dlg.msg), "Returning to Game Mode in %d seconds...", return_left);
        return;
    }
    dlg.tick = NULL;
    dlg.open = false;
    switch_to_game_mode();
    quit_requested = true;
}
static void finish_return_cb(int id)
{
    if (id == B_SWITCH) { switch_to_game_mode(); quit_requested = true; }
}
static void finish_do(bool steam_closed)
{
    Buf m = {0};
    bool restore = !strcmp(finish_action, "restore");
    check_steam();
    if (!steam_closed) buf_adds(&m, "Steam's launch options were NOT changed (Steam was still running).");
    else if (restore) {
        if (steam_restore_from_backup(steam_cfg)) buf_adds(&m, "Steam's launch options for Project Zomboid were restored.");
        else buf_adds(&m, "Nothing to restore in Steam's launch options.");
    } else if (steam_mem && steam_mem[0]) {
        char b[PATH_MAX] = "";
        if (steam_remove_memory_args(steam_cfg, b, sizeof b))
            buf_addf(&m, "Removed %s from Steam's launch options for Project Zomboid.\nSteam backup: %s", steam_mem, b);
        else buf_addf(&m, "Could not change Steam's launch options. Remove %s yourself: Project Zomboid > Properties > "
                          "General > Launch Options.", steam_mem);
    } else buf_adds(&m, "Steam's launch options for Project Zomboid contain no memory settings. Nothing to change.");
    load_values();
    return_left = 10;
    buf_addf(&m, "\n\nReturning to Game Mode in %d seconds...", return_left);
    dialog("PZ Memory Tool - Steam fix", finish_return_cb, B_STAY, "%s", m.s);
    dialog_button("Return to Game Mode", B_SWITCH, true);
    dialog_button("Stay in Desktop Mode", B_STAY, false);
    dialog_ticker(finish_return_tick);
    buf_free(&m);
}
static int finish_wait;
static void finish_wait_tick(void)
{
    /* Steam starts with Desktop Mode: give it a few seconds, then ask it to close */
    if (--finish_wait > 0) return;
    dlg.tick = NULL;
    dlg.open = false;
    ensure_steam_closed(!strcmp(finish_action, "restore") ? "restore" : "remove the memory settings from",
                        "leave the Steam launch options as they are", finish_do);
    if (dlg.open && dlg.cb == steam_guard_cb) steam_guard_cb(B_CLOSE_STEAM);   /* start closing right away */
}
static void start_finish(void)
{
    char *a = pending_fix_read();
    snprintf(finish_action, sizeof finish_action, "%s", a ? a : "remove");
    free(a);
    pending_fix_clear();          /* runs once, even if something goes wrong */
    dialog("PZ Memory Tool - Steam fix", NULL, 0,
           "Finishing the Steam fix from Game Mode.\n\nClosing Steam to change Project Zomboid's launch options...");
    finish_wait = 3;
    dialog_ticker(finish_wait_tick);
}

static void after_found(void)
{
    ui_ready = true;
    load_values();
    if (finish_mode) start_finish();
    else maybe_ask_add_to_steam();
}

/* =============================================================================
 *  The main window
 * ============================================================================= */
static float combo_x, rec_x, rec_y, rec_w, box_x, box_y, box_w, client_h;
static float y_warn, y_note, y_kofi, y_save;
static bool  button_below;

static void compute_layout(void)
{
    /* Same measuring as the Windows version, so nothing gets cut off */
    float chk_w = text_w(F_UI, "Minimum allocation (-Xms):") + 24;
    float max_w = text_w(F_UI, "Maximum allocation (-Xmx):");
    /* never closer than the Windows version (220 / 153), so both look the same */
    combo_x = fmaxf(220, 15 + ceilf(fmaxf(chk_w, max_w)) + 10);
    rec_w = fmaxf(153, ceilf(text_w(F_UI, "Use recommended")) + 26);
    float y = 335;                                   /* maximum dropdown row */
    button_below = combo_x + 93 + rec_w + 15 > MAX_BOX_X;
    float left_right;
    if (!button_below) { rec_x = combo_x + 93; rec_y = y - 1; left_right = rec_x + rec_w; y += 44; }
    else { rec_x = combo_x; rec_y = y + 36; left_right = combo_x + 85; y += 36 + 42; }
    y_warn = y; y += 82;
    y_note = y; y += 44;
    y_kofi = y; y += 30;
    float region_top = 237, region_bottom = y - 6;
    box_x = fminf(MAX_BOX_X, fmaxf(435, left_right + 15));
    box_w = W - 20 - box_x;
    box_y = region_top + (region_bottom - region_top - BOX_H) / 2;
    y_save = y;
    client_h = y + 34 + 15;
}

static void draw_titled_box(Rectangle r, const char *title)
{
    Rectangle frame = R(r.x + 1, r.y + 9, r.width - 2, r.height - 10);
    rrect(frame, 5, T.box);
    rrect(grow(frame, -1.5f), 4, T.bg);
    float tw = text_w(F_MONO_B, title);
    float tx = r.x + (r.width - tw) / 2;
    DrawRectangleRec(SR(R(tx - 6, r.y, tw + 12, line_h(F_MONO_B))), T.bg);
    text_at(F_MONO_B, title, tx, r.y, T.fg);
}

static void draw_main(void)
{
    SetMouseCursor(MOUSE_CURSOR_DEFAULT);
    float bh = (float)banner_design_height();
    banner_draw(0, 0, W * S, bh * S);

    float y = bh + 4;
    text_at(F_TITLE_B, "Project Zomboid Memory Allocation Discombobulation Tool", 15, y + 2, T.fg);
    if (ui_refresh(ID_REFRESH, R(W - 15 - 30, y + 1, 30, 30), ui_ready)) refresh_values();
    y += 38;

    char line[1024], a[32], b[32];
    snprintf(line, sizeof line, "File Found: %s", json_path ? json_path : "(not found yet)");
    text_box(F_SUB, line, R(15, y, W - 30, 36), T.sub, AL_LEFT, 0);
    y += 38;

    /* Steam launch options report */
    bool steam_bad = steam_mem && steam_mem[0];
    if (!ui_ready) snprintf(line, sizeof line, "Steam Memory Allocation Launch Option:");
    else if (steam_bad) snprintf(line, sizeof line, "Steam Memory Allocation Launch Option:   %s", steam_mem);
    else if (steam_checked) snprintf(line, sizeof line, "Steam Memory Allocation Launch Option:   none");
    else snprintf(line, sizeof line, "Steam Memory Allocation Launch Option:   could not check (Steam settings not found)");
    text_at(F_STEAM_B, line, 15, y + 1, steam_bad ? T.warn : T.sub);
    y += 22;
    if (steam_bad)
        text_at(F_SUB, "This overrides the file above. It will be removed from Steam's launch options when you save.", 15, y + 1, T.warn);
    y += 28;

    /* Memory */
    if (ui_ready) snprintf(line, sizeof line, "Total installed memory:   %d GB   (%.1f GB usable)", mi.installed_gb, mi.usable_gb);
    else snprintf(line, sizeof line, "Total installed memory:");
    text_at(F_BIG_B, line, 15, y + 1, T.fg);
    y += 28;
    if (ui_ready) snprintf(line, sizeof line, "Current allocation (-Xmx / -Xms):   %s / %s",
                           describe_gb(current_mb, a, sizeof a), current_xms_mb > 0 ? describe_gb(current_xms_mb, b, sizeof b) : "not set");
    else snprintf(line, sizeof line, "Current allocation (-Xmx / -Xms):");
    text_at(F_BIG_B, line, 15, y + 1, T.fg);
    y += 28;
    const char *device = mi.kind == HW_STEAM_DECK ? "Steam Deck" : mi.kind == HW_STEAM_MACHINE ? "Steam Machine" : "PC";
    snprintf(line, sizeof line, "Recommended allocation for this %s (-Xmx):   %s", device, ui_ready ? plan.recommend_text : "");
    text_at(F_UI, line, 15, y + 2, T.fg);
    y += 34;

    bool locked = !ui_ready || plan.locked;

    /* Minimum (-Xms) */
    float row_h = 26;
    if (ui_ready && plan.xms == XMS_HIDDEN && mi.kind == HW_STEAM_DECK) {
        text_at(F_UI, "Minimum allocation (-Xms):   not used on Steam Deck", 15, y + (row_h - line_h(F_UI)) / 2 + 1, T.sub);
    } else if (ui_ready && plan.xms != XMS_HIDDEN) {
        bool chk_enabled = plan.xms == XMS_NORMAL;
        bool v = xms_on && chk_enabled;
        if (ui_checkbox(ID_CHK, R(15, y + 1, combo_x - 17, row_h), "Minimum allocation (-Xms):", &v, chk_enabled)) xms_on = v;
        ui_combo(ID_XMS, R(combo_x, y, 85, 27), xms_items, xms_count, &sel_xms_index, chk_enabled && xms_on,
                 plan.xms == XMS_NORMAL);
    }
    y += 36;

    /* Maximum (-Xmx) */
    text_at(F_UI, "Maximum allocation (-Xmx):", 15, y + 3 + 1, T.fg);
    if (ui_combo(ID_GB, R(combo_x, y, 85, 27), plan.options, plan.count, &sel_gb_index, !locked, true)) update_xms_options();
    if (ui_button(ID_REC, R(rec_x, rec_y, rec_w, 30), "Use recommended", !locked, BTN_NORMAL, F_UI)) {
        int i = index_of(plan.options, plan.count, plan.recommended);
        if (i >= 0) { sel_gb_index = i; update_xms_options(); }
    }

    /* Warnings, then the note */
    if (ui_ready) {
        char w[1024], n[512];
        plan_messages(&plan, &mi, selected_gb(), w, sizeof w, n, sizeof n);
        text_box(F_UI, w, R(15, y_warn, LEFT_W, 80), T.warn, AL_LEFT, 0);
        text_box(F_NOTE_I, n, R(15, y_note, LEFT_W, 40), T.note, AL_LEFT, 0);
    }

    if (ui_link(ID_KOFI, R(15, y_kofi, LEFT_W, 24), "Support ComfyPlacebo on Ko-fi: ko-fi.com/comfyplacebo")) open_kofi();

    /* Console Copier */
    Rectangle bx = R(box_x, box_y, box_w, BOX_H);
    draw_titled_box(bx, "Console Copier");
    text_box(F_SMALL, "Copies Project Zomboid's console.txt log to your clipboard, as a file or as text.\n\n"
                      "Paste it into Discord, a message or a folder with right click or Ctrl+V.",
             R(bx.x + 14, bx.y + 30, box_w - 28, 122), T.fg, AL_CENTER, 0);
    text_box(F_SUB, console_status, R(bx.x + 10, bx.y + BOX_H - 154, box_w - 20, 54),
             console_status_error ? T.warn : T.note, AL_CENTER, 2);
    float copy_w = fminf(130, box_w - 24);
    if (ui_button(ID_COPY, R(bx.x + (box_w - copy_w) / 2, bx.y + BOX_H - 94, copy_w, 34), "Copy Console", true, BTN_NORMAL, F_UI))
        copy_console(false);
    if (ui_button(ID_COPYTEXT, R(bx.x + (box_w - copy_w) / 2, bx.y + BOX_H - 52, copy_w, 34), "Copy as text", true, BTN_NORMAL, F_UI))
        copy_console(true);

    /* Bottom buttons */
    if (ui_button(ID_SAVE, R(15, y_save, 120, 34), "Save", !locked, BTN_PRIMARY, F_BTN_B)) save();
    bool can_restore = !locked && (backup_exists() || steam_restore_available());
    if (ui_button(ID_RESTORE, R(145, y_save, 140, 34), "Restore backup", can_restore, BTN_NORMAL, F_UI)) restore_backup();
    if (ui_button(ID_CLOSE, R(W - 125, y_save, 110, 34), "Close", true, BTN_NORMAL, F_UI)) quit_requested = true;
}

/* Window icon: a tiny version of the banner colours */
static void set_icon(void)
{
    Image img = GenImageColor(64, 64, (Color){ 8, 8, 32, 255 });
    Color keys[6] = { rgb(0, 120, 248), rgb(0, 216, 248), rgb(224, 248, 248), rgb(248, 136, 216), rgb(232, 40, 184), rgb(136, 32, 216) };
    /* "PZ" in blocks */
    const char *glyph[7] = { "#### #####", "#  #    ## ", "#  #   ##  ", "####  ##   ", "#    ##    ", "#   ##     ", "#   #####  " };
    for (int r = 0; r < 7; r++)
        for (int c = 0; glyph[r][c]; c++)
            if (glyph[r][c] == '#') ImageDrawRectangle(&img, 6 + c * 5, 12 + r * 6, 5, 6, keys[(c + r) % 6]);
    ImageDrawRectangle(&img, 0, 0, 64, 3, keys[1]);
    ImageDrawRectangle(&img, 0, 61, 64, 3, keys[4]);
    SetWindowIcon(img);
    UnloadImage(img);
}

int main(int argc, char **argv)
{
    const char *arg_path = NULL;
    for (int i = 1; i < argc; i++) {
        if (!strcmp(argv[i], "--finish-steam-fix")) finish_mode = true;
        else if (!strcmp(argv[i], "--version")) { printf("PZ Memory Tool %s (build %s)\n", PZMT_VERSION, PZMT_BUILD); return 0; }
        else if (!strcmp(argv[i], "--help")) {
            printf("PZ Memory Tool %s - sets Project Zomboid's memory allocation.\n\n"
                   "Usage: PZMemoryTool [path/to/ProjectZomboid64.json]\n", PZMT_VERSION);
            return 0;
        } else arg_path = argv[i];
    }
    if (finish_mode && !pending_fix_exists()) { pending_fix_clear(); return 0; }   /* nothing left to do */

    srand((unsigned)time(NULL));
    banner_init();

    SetConfigFlags(FLAG_MSAA_4X_HINT | FLAG_VSYNC_HINT);
    SetTraceLogLevel(LOG_WARNING);
    InitWindow(W, 618, "PZ Memory Tool v" PZMT_VERSION " - Now with Console copier!!");
    SetExitKey(KEY_NULL);

    /* Display scaling (HiDPI): from the desktop, or PZMT_SCALE=1.5 to force */
    Vector2 dpi = GetWindowScaleDPI();
    S = dpi.x > 0.5f ? dpi.x : 1.0f;
    const char *fs = getenv("PZMT_SCALE");
    if (fs && atof(fs) > 0.5) S = (float)atof(fs);
    S = roundf(S * 4) / 4;

    theme_load();
    fonts_load();
    compute_layout();
    SetWindowSize((int)(W * S), (int)(client_h * S));
    int mon = GetCurrentMonitor();
    SetWindowPosition((GetMonitorWidth(mon) - (int)(W * S)) / 2, (GetMonitorHeight(mon) - (int)(client_h * S)) / 2);
    set_icon();
    SetTargetFPS(50);
    install_input_hooks();
    clip_init();

    if (arg_path && is_valid_pz_json(arg_path)) json_path = strdup(arg_path);
    else json_path = find_game_json();
    if (json_path) after_found();
    else not_found_dialog();

    double banner_t = GetTime();
    const char *shot = getenv("PZMT_SCREENSHOT");       /* testing: save a screenshot and exit */
    int frames = 0;
    while (!quit_requested && !WindowShouldClose()) {
        clip_pump();
        read_input();
        blocked = false;
        job_poll();
        dialog_input();
        if (!dlg.open) popup_input();
        if (!blocked) {
            if (in.tab) focus_tab(in.tab);
            bool on_combo = (focus_id == ID_GB || focus_id == ID_XMS) && focus_items[focus_id].enabled;
            bool lr = in.nav == NAV_LEFT || in.nav == NAV_RIGHT;
            /* Left / Right on a dropdown change its value (handled by the dropdown itself) */
            if (in.nav && show_focus && !(on_combo && lr)) focus_id = focus_move_dir(in.nav);
            if (!focus_items[focus_id].used || !focus_items[focus_id].enabled) focus_tab(1);
        }

        while (GetTime() - banner_t >= 0.04) { banner_step(); banner_t += 0.04; }

        BeginDrawing();
        ClearBackground(T.bg);
        bool keep_blocked = blocked;
        /* when the list just closed this frame, don't let the same key press a control underneath */
        draw_main();
        blocked = keep_blocked;
        popup_draw();
        dialog_draw();
        EndDrawing();
        if (in.mouse_released) press_id = 0;

        if (shot && ++frames == 30) {
            TakeScreenshot(shot);
            break;
        }
    }
    if (job.running) { job.cancel = true; pthread_join(job.th, NULL); }
    CloseWindow();
    clip_shutdown();
    return 0;
}
