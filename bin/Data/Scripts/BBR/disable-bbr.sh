set -eu

fail() {
    printf 'ERROR: %s\n' "$1" >&2
    exit 1
}

if [ "$(uname -s)" != "Linux" ]; then
    fail "BBR can only be disabled on Linux."
fi

if ! command -v sysctl >/dev/null 2>&1; then
    fail "sysctl is not available on this server."
fi

if [ "$(id -u)" != "0" ]; then
    fail "Root privileges are required. Connect as root or run this script with a root account."
fi

conf_file=/etc/sysctl.d/99-jeekremote-bbr.conf
if [ -f "$conf_file" ]; then
    rm -f "$conf_file"
    printf 'Removed %s\n' "$conf_file"
else
    printf 'Configuration file not found: %s\n' "$conf_file"
fi

available_file=/proc/sys/net/ipv4/tcp_available_congestion_control
target_cc=

if [ -r "$available_file" ]; then
    available=$(cat "$available_file")
    if printf '%s\n' "$available" | tr ' ' '\n' | grep -qx cubic; then
        target_cc=cubic
    else
        target_cc=$(printf '%s\n' "$available" | tr ' ' '\n' | grep -vx bbr | head -n 1 || true)
    fi
else
    target_cc=cubic
fi

if [ -z "$target_cc" ]; then
    fail "No available non-BBR TCP congestion control was found."
fi

sysctl -w "net.ipv4.tcp_congestion_control=$target_cc"

if sysctl -w net.core.default_qdisc=fq_codel; then
    printf 'net.core.default_qdisc reset to fq_codel\n'
else
    printf 'WARNING: Could not reset net.core.default_qdisc to fq_codel. BBR was still disabled if the congestion control changed.\n' >&2
fi

current_qdisc=$(sysctl -n net.core.default_qdisc 2>/dev/null || printf 'unknown')
current_cc=$(sysctl -n net.ipv4.tcp_congestion_control 2>/dev/null || printf 'unknown')

printf 'net.core.default_qdisc=%s\n' "$current_qdisc"
printf 'net.ipv4.tcp_congestion_control=%s\n' "$current_cc"

if [ "$current_cc" = "bbr" ]; then
    fail "BBR is still active."
fi

printf 'BBR is disabled.\n'
