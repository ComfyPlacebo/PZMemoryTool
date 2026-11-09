/* =============================================================================
 *  PZ Memory Tool - Linux / SteamOS version: the animated ComfyPlacebo's banner
 *  Same design as BannerRenderer in the Windows version: logo with a travelling
 *  colour wave, stars scrolling right -> left, colour bars at the edges.
 * ============================================================================= */
#include "pzmt.h"
#include "raylib.h"

#include <math.h>
#include <stdlib.h>
#include <string.h>

/* Logo in "ANSI Shadow" style.  # = solid block, = | r 7 L J = double-line
 * shadow pieces (horizontal, vertical, top-left, top-right, bottom-left, bottom-right). */
static const char *LOGO[] = {
    " ######7 ######7 ###7   ###7#######7##7   ##7######7 ##7      #####7  ######7#######7######7  ######7  ##7 #######7",
    "##r====J##r===##7####7 ####|##r====JL##7 ##rJ##r==##7##|     ##r==##7##r====J##r====J##r==##7##r===##7 ##| ##r====J",
    "##|     ##|   ##|##r####r##|#####7   L####rJ ######rJ##|     #######|##|     #####7  ######rJ##|   ##| L=J #######7",
    "##|     ##|   ##|##|L##rJ##|##r==J    L##rJ  ##r===J ##|     ##r==##|##|     ##r==J  ##r==##7##|   ##|     L====##|",
    "L######7L######rJ##| L=J ##|##|        ##|   ##|     #######7##|  ##|L######7#######7######rJL######rJ     #######|",
    " L=====J L=====J L=J     L=JL=J        L=J   L=J     L======JL=J  L=J L=====JL======JL=====J  L=====J      L======J",
};
#define LOGO_ROWS   6
#define ROWS        8          /* banner height in text rows               */
#define LOGO_TOP    1          /* rows 0 and 7 are open sky                */
#define CELL_ASPECT 2.1f       /* text cell height / width                 */
#define STAR_COUNT  70
#define WAVE_STEPS  64
#define DESIGN_W    560        /* banner height is based on this width     */

static const float STAR_SPEED[3] = { 0.18f, 0.45f, 1.1f };   /* cells per frame, per layer */

static int   logo_w, design_cols, cols;
static Color wave[WAVE_STEPS], shadow[WAVE_STEPS], star_col[5], bg;
static float sx[STAR_COUNT];
static int   sy[STAR_COUNT], layer[STAR_COUNT];
static int   frame;

/* Snap a colour to the SNES 15-bit palette (5 bits per channel). */
static Color snes(int r, int g, int b)
{
    r = (r < 0 ? 0 : r > 255 ? 255 : r) >> 3;
    g = (g < 0 ? 0 : g > 255 ? 255 : g) >> 3;
    b = (b < 0 ? 0 : b > 255 ? 255 : b) >> 3;
    return (Color){ (unsigned char)((r << 3) | (r >> 2)), (unsigned char)((g << 3) | (g >> 2)),
                    (unsigned char)((b << 3) | (b >> 2)), 255 };
}
static int mod(int a, int n) { int r = a % n; return r < 0 ? r + n : r; }
static float frand(void) { return (float)rand() / (float)RAND_MAX; }

static bool is_sky(int col, int row)
{
    if (row < 0 || row >= LOGO_ROWS || col < 0 || col >= (int)strlen(LOGO[row])) return true;
    return LOGO[row][col] == ' ';
}

int banner_design_height(void)
{
    return (int)roundf(DESIGN_W / (float)design_cols * CELL_ASPECT * ROWS);
}

void banner_init(void)
{
    logo_w = 0;
    for (int i = 0; i < LOGO_ROWS; i++) { int l = (int)strlen(LOGO[i]); if (l > logo_w) logo_w = l; }
    design_cols = cols = logo_w + 4;

    /* 90s cracktro copper bars: deep blue -> cyan -> white -> pink -> magenta -> purple */
    Color keys[8] = { snes(16, 24, 160), snes(0, 120, 248), snes(0, 216, 248), snes(224, 248, 248),
                      snes(248, 136, 216), snes(232, 40, 184), snes(136, 32, 216), snes(40, 16, 168) };
    for (int i = 0; i < WAVE_STEPS; i++) {
        double pos = i * 8 / (double)WAVE_STEPS;
        int k = (int)pos;
        double f = pos - k;
        Color a = keys[k], b = keys[(k + 1) % 8];
        wave[i] = snes((int)(a.r + (b.r - a.r) * f), (int)(a.g + (b.g - a.g) * f), (int)(a.b + (b.b - a.b) * f));
        shadow[i] = snes(wave[i].r * 45 / 100, wave[i].g * 45 / 100, wave[i].b * 45 / 100 + 24);
    }
    star_col[0] = snes(64, 72, 128);     /* far, dim             */
    star_col[1] = snes(136, 160, 232);   /* middle               */
    star_col[2] = snes(248, 248, 248);   /* near, bright         */
    star_col[3] = snes(248, 232, 88);    /* near, twinkle        */
    star_col[4] = snes(56, 64, 136);     /* streak behind stars  */
    bg = snes(8, 8, 32);

    for (int i = 0; i < STAR_COUNT; i++) {
        sx[i] = frand() * cols;
        sy[i] = rand() % ROWS;
        layer[i] = i % 3;
    }
}

/* Advance the animation one frame (~25 times a second). */
void banner_step(void)
{
    frame++;
    for (int i = 0; i < STAR_COUNT; i++) {
        sx[i] -= STAR_SPEED[layer[i]];
        if (sx[i] < -2) { sx[i] += cols + 2; sy[i] = rand() % ROWS; }
    }
}

static void line(float x1, float y1, float x2, float y2, float w, Color c)
{
    /* axis-aligned lines drawn as rectangles: crisp, no anti-aliasing */
    if (x1 > x2) { float t = x1; x1 = x2; x2 = t; }
    if (y1 > y2) { float t = y1; y1 = y2; y2 = t; }
    if (y1 == y2) DrawRectangleRec((Rectangle){ floorf(x1), floorf(y1 - w / 2), ceilf(x2 - x1), w }, c);
    else DrawRectangleRec((Rectangle){ floorf(x1 - w / 2), floorf(y1), w, ceilf(y2 - y1) }, c);
}

static void draw_shadow(char c, float l, float t, float r, float b, float w, Color col)
{
    float cw = r - l, ch = b - t;
    float xa = l + cw * 0.25f, xb = l + cw * 0.70f;
    float ya = t + ch * 0.35f, yb = t + ch * 0.65f;
    switch (c) {
        case '=': line(l, ya, r, ya, w, col); line(l, yb, r, yb, w, col); break;
        case '|': line(xa, t, xa, b, w, col); line(xb, t, xb, b, w, col); break;
        case 'r': line(xa, ya, xa, b, w, col); line(xa - w / 2, ya, r, ya, w, col);
                  line(xb, yb, xb, b, w, col); line(xb - w / 2, yb, r, yb, w, col); break;
        case '7': line(l, ya, xb + w / 2, ya, w, col); line(xb, ya, xb, b, w, col);
                  line(l, yb, xa + w / 2, yb, w, col); line(xa, yb, xa, b, w, col); break;
        case 'L': line(xa, t, xa, yb, w, col); line(xa - w / 2, yb, r, yb, w, col);
                  line(xb, t, xb, ya, w, col); line(xb - w / 2, ya, r, ya, w, col); break;
        case 'J': line(xb, t, xb, yb, w, col); line(l, yb, xb + w / 2, yb, w, col);
                  line(xa, t, xa, ya, w, col); line(l, ya, xa + w / 2, ya, w, col); break;
    }
}

void banner_draw(float ox, float oy, float width, float height)
{
    float ch = height / ROWS;
    float cw = ch / CELL_ASPECT;
    int new_cols = (int)ceilf(width / cw);
    if (new_cols < design_cols) new_cols = design_cols;
    if (new_cols != cols) {
        for (int i = 0; i < STAR_COUNT; i++) sx[i] = frand() * new_cols;
        cols = new_cols;
    }
    int logo_left = (cols - logo_w) / 2;
    /* centre the design in the given width (extra columns spill equally both sides) */
    float shift = floorf((width - cols * cw) / 2);

    BeginScissorMode((int)ox, (int)oy, (int)width, (int)height);
    DrawRectangleRec((Rectangle){ ox, oy, width, height }, bg);

    float lw = fmaxf(1.0f, roundf(cw / 4.7f));
    float bar = 2 * lw;

    /* scrolling colour bars at the top and bottom edge */
    for (int x = 0; x < cols; x++) {
        Color c = wave[mod(x + frame * 2, WAVE_STEPS)];
        float px = floorf(ox + shift + x * cw), pw = floorf(ox + shift + (x + 1) * cw) - px;
        DrawRectangleRec((Rectangle){ px, oy, pw, bar }, c);
        DrawRectangleRec((Rectangle){ px, oy + height - bar, pw, bar }, c);
    }

    /* stars (only where the logo is empty): . far, + middle, * near with a streak */
    for (int i = 0; i < STAR_COUNT; i++) {
        int cx = (int)roundf(sx[i]);
        if (cx < 0 || cx >= cols || !is_sky(cx - logo_left, sy[i] - LOGO_TOP)) continue;
        float mx = floorf(ox + shift + cx * cw + cw / 2), my = floorf(oy + sy[i] * ch + ch / 2);
        float u = fmaxf(1.0f, roundf(cw / 5.0f));
        switch (layer[i]) {
            case 0:
                DrawRectangleRec((Rectangle){ mx, my + u, u, u }, star_col[0]);
                break;
            case 1:
                DrawRectangleRec((Rectangle){ mx - u, my, 3 * u, u }, star_col[1]);
                DrawRectangleRec((Rectangle){ mx, my - u, u, 3 * u }, star_col[1]);
                break;
            default: {
                Color c = ((frame / 4 + i) % 5 == 0) ? star_col[3] : star_col[2];
                for (int k = 1; k <= 2; k++)
                    if (cx + k < cols && is_sky(cx + k - logo_left, sy[i] - LOGO_TOP))
                        DrawRectangleRec((Rectangle){ floorf(ox + shift + (cx + k) * cw), my, ceilf(cw * 0.8f), u }, star_col[4]);
                DrawRectangleRec((Rectangle){ mx - u, my, 3 * u, u }, c);
                DrawRectangleRec((Rectangle){ mx, my - u, u, 3 * u }, c);
                DrawRectangleRec((Rectangle){ mx - u, my - u, u, u }, c);
                DrawRectangleRec((Rectangle){ mx + u, my - u, u, u }, c);
                DrawRectangleRec((Rectangle){ mx - u, my + u, u, u }, c);
                DrawRectangleRec((Rectangle){ mx + u, my + u, u, u }, c);
                break;
            }
        }
    }

    /* logo with a colour wave travelling right -> left */
    for (int row = 0; row < LOGO_ROWS; row++) {
        const char *ln = LOGO[row];
        float y0 = floorf(oy + (row + LOGO_TOP) * ch), y1 = floorf(oy + (row + LOGO_TOP + 1) * ch);
        for (int col = 0; ln[col]; col++) {
            char c = ln[col];
            if (c == ' ') continue;
            int x = col + logo_left;
            float x0 = floorf(ox + shift + x * cw), x1 = floorf(ox + shift + (x + 1) * cw);
            int idx = mod(x + frame + row * 2 + (int)(3 * sin((x + frame) * 0.12)), WAVE_STEPS);
            if (c == '#') DrawRectangleRec((Rectangle){ x0, y0, x1 - x0, y1 - y0 }, wave[idx]);
            else draw_shadow(c, x0, y0, x1, y1, lw, shadow[idx]);
        }
    }
    EndScissorMode();
}
