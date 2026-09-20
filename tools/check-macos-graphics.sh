#!/usr/bin/env bash
# 77 means the host has no accelerated renderer; other failures are real errors.
set -euo pipefail
[[ $(uname -s) == Darwin ]] || { echo 'error: graphics probe requires macOS' >&2; exit 1; }
probe=$(mktemp -d)
trap 'rm -rf "$probe"' EXIT
cat > "$probe/renderer.c" <<'C'
#include <OpenGL/OpenGL.h>
#include <stdio.h>
int main(void) {
    CGLRendererInfoObj info = NULL;
    GLint count = 0;
    CGLError error = CGLQueryRendererInfo(0xffffffff, &info, &count);
    if (error != kCGLNoError) {
        fprintf(stderr, "CGL renderer query failed: %s\n", CGLErrorString(error));
        return 1;
    }
    int accelerated = 0;
    for (int i = 0; i < count; i++) {
        GLint available = 0;
        error = CGLDescribeRenderer(info, i, kCGLRPAccelerated, &available);
        if (error != kCGLNoError) {
            CGLDestroyRendererInfo(info);
            fprintf(stderr, "CGL renderer description failed: %s\n", CGLErrorString(error));
            return 1;
        }
        accelerated += available != 0;
    }
    CGLDestroyRendererInfo(info);
    printf("macOS CGL renderers: %d total, %d accelerated\n", count, accelerated);
    return accelerated ? 0 : 77;
}
C
clang -Wno-deprecated-declarations "$probe/renderer.c" -framework OpenGL -o "$probe/renderer"
"$probe/renderer"
