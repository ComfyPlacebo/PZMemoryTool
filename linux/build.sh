#!/bin/sh
# =============================================================================
#  PZ Memory Tool - Linux / SteamOS build script
#
#  Builds one self-contained file, ./PZMemoryTool (about 1 MB), with raylib
#  built in. Needs: a C compiler, make, git, and the X11 / OpenGL development
#  packages. On Debian / Ubuntu:
#
#    sudo apt install build-essential git libx11-dev libxrandr-dev libxinerama-dev \
#                     libxcursor-dev libxi-dev libgl1-mesa-dev
#
#  On Arch / SteamOS (Desktop Mode, after "sudo steamos-readonly disable"):
#    sudo pacman -S base-devel git libx11 libxrandr libxinerama libxcursor libxi mesa
#
#  Usage:  sh build.sh   (optional: PZMT_VERSION=1.2.0-preview PZMT_BUILD=12 sh build.sh)
#
#  PZMT_ZIG=1 builds with Zig's C compiler (pip install ziglang) against an old
#  glibc (2.31), so the program also runs on older Linux and SteamOS versions.
#  The GitHub builds use this.
# =============================================================================
set -e
cd "$(dirname "$0")"

RAYLIB_VERSION=5.5
RAYLIB_DIR=raylib-$RAYLIB_VERSION
CC=${CC:-cc}
AR=${AR:-ar}
X11_LIB=-lX11

if [ "$PZMT_ZIG" = "1" ]; then
    # Zig compiler wrappers; system X11 headers only (not the system's newer glibc headers)
    mkdir -p .zig/inc
    [ -e .zig/inc/X11 ] || ln -s /usr/include/X11 .zig/inc/X11
    printf '#!/bin/sh\nexec python3 -m ziglang cc -target x86_64-linux-gnu.2.31 -isystem "%s/.zig/inc" "$@"\n' "$(pwd)" > .zig/cc
    printf '#!/bin/sh\nexec python3 -m ziglang ar "$@"\n' > .zig/ar
    chmod +x .zig/cc .zig/ar
    CC="$(pwd)/.zig/cc"; AR="$(pwd)/.zig/ar"
    RAYLIB_DIR=raylib-$RAYLIB_VERSION-zig
    X11_LIB=$(ls /usr/lib/x86_64-linux-gnu/libX11.so /usr/lib/libX11.so 2>/dev/null | head -1)
fi

if [ ! -f "$RAYLIB_DIR/src/libraylib.a" ]; then
    if [ ! -d "$RAYLIB_DIR" ]; then
        echo "Downloading raylib $RAYLIB_VERSION..."
        git clone --depth 1 --branch "$RAYLIB_VERSION" https://github.com/raysan5/raylib.git "$RAYLIB_DIR"
    fi
    echo "Building raylib..."
    make -C "$RAYLIB_DIR/src" PLATFORM=PLATFORM_DESKTOP RAYLIB_LIBTYPE=STATIC CC="$CC" AR="$AR" \
         GLFW_LINUX_ENABLE_WAYLAND=FALSE CUSTOM_CFLAGS="-O2" -j"$(nproc 2>/dev/null || echo 2)"
fi

VERSION_FLAG=""
[ -n "$PZMT_VERSION" ] && VERSION_FLAG="-DPZMT_VERSION=\"$PZMT_VERSION\""
[ -n "$PZMT_BUILD" ] && VERSION_FLAG="$VERSION_FLAG -DPZMT_BUILD=\"$PZMT_BUILD\""

echo "Building PZMemoryTool..."
"$CC" -std=c99 -O2 -Wall -Wextra -Wno-unused-parameter -Wno-format-truncation $VERSION_FLAG \
   -I"$RAYLIB_DIR/src" -I"$RAYLIB_DIR/src/external/glfw/include" \
   src/main.c src/core.c src/banner.c src/clipboard.c src/log.c src/zb.c \
   "$RAYLIB_DIR/src/libraylib.a" \
   -lm -lpthread -ldl $X11_LIB \
   -s -o PZMemoryTool

echo "Done: $(pwd)/PZMemoryTool ($(du -h PZMemoryTool | cut -f1))"
