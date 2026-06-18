set -eu

fail() {
    printf 'ERROR: %s\n' "$1" >&2
    exit 1
}

if [ "$(uname -s)" != "Linux" ]; then
    fail "BBR can only be enabled on Linux."
fi

if ! command -v sysctl >/dev/null 2>&1; then
    fail "sysctl is not available on this server."
fi

if [ "$(id -u)" != "0" ]; then
    fail "Root privileges are required. Connect as root or run this script with a root account."
fi

available_file=/proc/sys/net/ipv4/tcp_available_congestion_control
if [ -r "$available_file" ] && ! grep -qw bbr "$available_file"; then
    if command -v modprobe >/dev/null 2>&1; then
        modprobe tcp_bbr 2>/dev/null || true
    fi
fi

if [ -r "$available_file" ] && ! grep -qw bbr "$available_file"; then
    available=$(cat "$available_file")
    fail "Kernel does not report BBR support. Available congestion controls: $available"
fi

conf_file=/etc/sysctl.d/99-jeekremote-bbr.conf
tmp_file="${conf_file}.tmp.$$"
trap 'rm -f "$tmp_file"' EXIT HUP INT TERM

mkdir -p /etc/sysctl.d
{
    printf '# Managed by JeekRemoteManager.\n'
    printf 'net.core.default_qdisc=fq\n'
    printf 'net.ipv4.tcp_congestion_control=bbr\n'
} > "$tmp_file"

mv "$tmp_file" "$conf_file"
trap - EXIT HUP INT TERM

sysctl -w net.core.default_qdisc=fq
sysctl -w net.ipv4.tcp_congestion_control=bbr

current_qdisc=$(sysctl -n net.core.default_qdisc 2>/dev/null || printf 'unknown')
current_cc=$(sysctl -n net.ipv4.tcp_congestion_control 2>/dev/null || printf 'unknown')

printf 'BBR configuration written to %s\n' "$conf_file"
printf 'net.core.default_qdisc=%s\n' "$current_qdisc"
printf 'net.ipv4.tcp_congestion_control=%s\n' "$current_cc"

if [ "$current_cc" != "bbr" ]; then
    fail "BBR was configured but is not active."
fi

printf 'BBR is enabled.\n'
