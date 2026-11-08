/* =============================================================================
 *  PZ Memory Tool - Linux / SteamOS version: clipboard
 *
 *  On Linux the clipboard is not a store: the program that copied something
 *  hands it over when another program pastes. This file does that for X11
 *  (also used by Wayland desktops through XWayland):
 *    - Copy Console: offers console.txt as a FILE (text/uri-list and the
 *      GNOME/KDE "copied files" format), so it pastes as an attachment.
 *    - Copy as text: offers the log's text (UTF-8), sent in pieces (INCR)
 *      when it is large.
 *  When the tool closes, a small background copy of itself keeps the clipboard
 *  available until something else is copied, so pasting still works.
 * ============================================================================= */
#define _GNU_SOURCE
#include "pzmt.h"

#include <X11/Xatom.h>
#include <X11/Xlib.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/wait.h>
#include <unistd.h>

static Display *dpy;
static Window   win;
static Atom A_CLIPBOARD, A_TARGETS, A_UTF8, A_TEXT, A_PLAIN, A_PLAIN_UTF8, A_URILIST,
            A_GNOME_FILES, A_INCR, A_TIMESTAMP, A_MULTIPLE;

static char  *data_text;      /* text or file path, depending on mode */
static size_t data_len;
static bool   mode_file;      /* true = file, false = text */
static bool   owned;
static Time   owned_at;

/* One running large (INCR) transfer per requesting program is plenty here */
#define MAX_XFERS 8
typedef struct { bool active; Window requestor; Atom property, type; const char *data; size_t len, sent; char *owned_buf; } Xfer;
static Xfer xfers[MAX_XFERS];
static size_t chunk_max;

static void intern_atoms(void)
{
    A_CLIPBOARD   = XInternAtom(dpy, "CLIPBOARD", False);
    A_TARGETS     = XInternAtom(dpy, "TARGETS", False);
    A_UTF8        = XInternAtom(dpy, "UTF8_STRING", False);
    A_TEXT        = XInternAtom(dpy, "TEXT", False);
    A_PLAIN       = XInternAtom(dpy, "text/plain", False);
    A_PLAIN_UTF8  = XInternAtom(dpy, "text/plain;charset=utf-8", False);
    A_URILIST     = XInternAtom(dpy, "text/uri-list", False);
    A_GNOME_FILES = XInternAtom(dpy, "x-special/gnome-copied-files", False);
    A_INCR        = XInternAtom(dpy, "INCR", False);
    A_TIMESTAMP   = XInternAtom(dpy, "TIMESTAMP", False);
    A_MULTIPLE    = XInternAtom(dpy, "MULTIPLE", False);
}

static bool open_display(void)
{
    dpy = XOpenDisplay(NULL);
    if (!dpy) return false;
    win = XCreateSimpleWindow(dpy, DefaultRootWindow(dpy), -10, -10, 1, 1, 0, 0, 0);
    XSelectInput(dpy, win, PropertyChangeMask);
    intern_atoms();
    long ext = XExtendedMaxRequestSize(dpy);
    long max = ext ? ext : XMaxRequestSize(dpy);
    chunk_max = (size_t)(max * 4 - 256);
    if (chunk_max > 256 * 1024) chunk_max = 256 * 1024;
    return true;
}

bool clip_init(void) { return dpy || open_display(); }

/* file:// URL with unsafe characters percent-encoded */
static char *file_uri(const char *path)
{
    Buf b = {0};
    buf_adds(&b, "file://");
    for (const unsigned char *p = (const unsigned char *)path; *p; p++) {
        if ((*p >= 'a' && *p <= 'z') || (*p >= 'A' && *p <= 'Z') || (*p >= '0' && *p <= '9') ||
            strchr("/-_.~", *p)) { char c = (char)*p; buf_add(&b, &c, 1); }
        else buf_addf(&b, "%%%02X", *p);
    }
    return b.s;
}

/* Builds what to send for a given format; returns NULL if not offered. */
static char *payload(Atom target, size_t *len, bool *must_free)
{
    *must_free = false;
    if (mode_file) {
        if (target == A_URILIST) {
            char *u = file_uri(data_text);
            Buf b = {0}; buf_adds(&b, u); buf_adds(&b, "\r\n"); free(u);
            *len = b.len; *must_free = true; return b.s;
        }
        if (target == A_GNOME_FILES) {
            char *u = file_uri(data_text);
            Buf b = {0}; buf_adds(&b, "copy\n"); buf_adds(&b, u); free(u);
            *len = b.len; *must_free = true; return b.s;
        }
        if (target == A_UTF8 || target == XA_STRING || target == A_TEXT || target == A_PLAIN || target == A_PLAIN_UTF8) {
            *len = data_len; return data_text;        /* plain-text apps get the file's path */
        }
        return NULL;
    }
    if (target == A_UTF8 || target == XA_STRING || target == A_TEXT || target == A_PLAIN || target == A_PLAIN_UTF8) {
        *len = data_len; return data_text;
    }
    return NULL;
}

static void answer(XSelectionRequestEvent *r)
{
    XSelectionEvent ev = { .type = SelectionNotify, .display = r->display, .requestor = r->requestor,
                           .selection = r->selection, .target = r->target, .time = r->time, .property = None };
    Atom prop = r->property != None ? r->property : r->target;

    if (r->target == A_TARGETS) {
        Atom t[10]; int n = 0;
        t[n++] = A_TARGETS; t[n++] = A_TIMESTAMP;
        if (mode_file) { t[n++] = A_URILIST; t[n++] = A_GNOME_FILES; }
        t[n++] = A_UTF8; t[n++] = A_PLAIN_UTF8; t[n++] = A_PLAIN; t[n++] = XA_STRING; t[n++] = A_TEXT;
        XChangeProperty(dpy, r->requestor, prop, XA_ATOM, 32, PropModeReplace, (unsigned char *)t, n);
        ev.property = prop;
    } else if (r->target == A_TIMESTAMP) {
        long ts = (long)owned_at;
        XChangeProperty(dpy, r->requestor, prop, XA_INTEGER, 32, PropModeReplace, (unsigned char *)&ts, 1);
        ev.property = prop;
    } else {
        size_t len; bool must_free;
        char *d = payload(r->target, &len, &must_free);
        if (d) {
            Atom type = (r->target == A_TEXT) ? A_UTF8 : r->target;
            if (len <= chunk_max) {
                XChangeProperty(dpy, r->requestor, prop, type, 8, PropModeReplace, (unsigned char *)d, (int)len);
                if (must_free) free(d);
                ev.property = prop;
            } else {
                /* large: announce INCR, then send pieces each time the requestor deletes the property */
                int slot = -1;
                for (int i = 0; i < MAX_XFERS; i++) if (!xfers[i].active) { slot = i; break; }
                if (slot >= 0) {
                    Xfer *x = &xfers[slot];
                    *x = (Xfer){ .active = true, .requestor = r->requestor, .property = prop, .type = type,
                                 .data = d, .len = len, .sent = 0, .owned_buf = must_free ? d : NULL };
                    XSelectInput(dpy, r->requestor, PropertyChangeMask);
                    long size = (long)len;
                    XChangeProperty(dpy, r->requestor, prop, A_INCR, 32, PropModeReplace, (unsigned char *)&size, 1);
                    ev.property = prop;
                } else if (must_free) free(d);
            }
        }
    }
    XSendEvent(dpy, r->requestor, False, 0, (XEvent *)&ev);
    XFlush(dpy);
}

static void incr_step(XPropertyEvent *p)
{
    if (p->state != PropertyDelete) return;
    for (int i = 0; i < MAX_XFERS; i++) {
        Xfer *x = &xfers[i];
        if (!x->active || x->requestor != p->window || x->property != p->atom) continue;
        size_t n = x->len - x->sent;
        if (n > chunk_max) n = chunk_max;
        XChangeProperty(dpy, x->requestor, x->property, x->type, 8, PropModeReplace,
                        (const unsigned char *)x->data + x->sent, (int)n);
        x->sent += n;
        if (n == 0) {                       /* the empty piece marks the end */
            XSelectInput(dpy, x->requestor, NoEventMask);
            free(x->owned_buf);
            x->active = false;
        }
        XFlush(dpy);
    }
}

static void handle(XEvent *e)
{
    switch (e->type) {
        case SelectionRequest: answer(&e->xselectionrequest); break;
        case SelectionClear:   owned = false; break;
        case PropertyNotify:   incr_step(&e->xproperty); break;
    }
}

void clip_pump(void)
{
    if (!dpy) return;
    while (XPending(dpy)) { XEvent e; XNextEvent(dpy, &e); handle(&e); }
}

static Time server_time(void)
{
    /* a zero-length property change gives us the X server's current time */
    Atom a = XInternAtom(dpy, "PZMT_TIME", False);
    XChangeProperty(dpy, win, a, XA_STRING, 8, PropModeAppend, NULL, 0);
    XEvent e;
    for (;;) {
        XWindowEvent(dpy, win, PropertyChangeMask, &e);
        if (e.xproperty.atom == a) return e.xproperty.time;
    }
}

static bool take_ownership(void)
{
    owned_at = server_time();
    XSetSelectionOwner(dpy, A_CLIPBOARD, win, owned_at);
    owned = XGetSelectionOwner(dpy, A_CLIPBOARD) == win;
    XFlush(dpy);
    return owned;
}

static bool set_data(const char *d, size_t len, bool file)
{
    if (!clip_init()) return false;
    for (int i = 0; i < MAX_XFERS; i++) if (xfers[i].active) { free(xfers[i].owned_buf); xfers[i].active = false; }
    free(data_text);
    data_text = malloc(len + 1);
    memcpy(data_text, d, len);
    data_text[len] = 0;
    data_len = len;
    mode_file = file;
    return take_ownership();
}

bool clip_set_file(const char *path) { return set_data(path, strlen(path), true); }
bool clip_set_text(const char *text, size_t len) { return set_data(text, len, false); }

/* Keeps serving the clipboard after the window closes, until something else is copied. */
void clip_shutdown(void)
{
    if (!dpy) return;
    clip_pump();
    if (!owned) { XCloseDisplay(dpy); dpy = NULL; return; }
    XCloseDisplay(dpy);                   /* the child opens its own connection */
    dpy = NULL;
    pid_t pid = fork();
    if (pid != 0) { if (pid > 0) waitpid(pid, NULL, 0); return; }
    setsid();
    if (fork() != 0) _exit(0);
    if (!open_display()) _exit(0);
    if (!take_ownership()) _exit(0);
    while (owned) {
        XEvent e;
        XNextEvent(dpy, &e);
        handle(&e);
    }
    /* finish any transfer still running, then leave */
    for (int tries = 0; tries < 1000; tries++) {
        bool any = false;
        for (int i = 0; i < MAX_XFERS; i++) if (xfers[i].active) any = true;
        if (!any) break;
        XEvent e;
        XNextEvent(dpy, &e);
        handle(&e);
    }
    _exit(0);
}
