#!/bin/sh

set -eu

detect_os_version() {
    if [ -r /etc/os-release ]; then
        (
            . /etc/os-release
            if [ -n "${PRETTY_NAME:-}" ]; then
                printf '%s\n' "$PRETTY_NAME"
            elif [ -n "${NAME:-}" ] && [ -n "${VERSION:-}" ]; then
                printf '%s %s\n' "$NAME" "$VERSION"
            elif [ -n "${NAME:-}" ]; then
                printf '%s\n' "$NAME"
            else
                exit 1
            fi
        ) && return
    fi

    if command -v lsb_release >/dev/null 2>&1; then
        lsb_release -ds 2>/dev/null && return
    fi

    if [ -r /etc/redhat-release ]; then
        head -n 1 /etc/redhat-release
        return
    fi

    if [ -r /etc/alpine-release ]; then
        printf 'Alpine Linux %s\n' "$(head -n 1 /etc/alpine-release)"
        return
    fi

    if [ -r /etc/debian_version ]; then
        printf 'Debian %s\n' "$(head -n 1 /etc/debian_version)"
        return
    fi

    uname -s
}

printf 'Operating system: %s\n' "$(detect_os_version)"
printf 'Kernel: %s\n' "$(uname -sr)"
printf 'Architecture: %s\n' "$(uname -m)"
