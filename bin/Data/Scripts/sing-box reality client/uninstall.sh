set -eu

fail() {
    printf 'ERROR: %s\n' "$1" >&2
    exit 1
}

warn() {
    printf 'WARNING: %s\n' "$1" >&2
}

if [ "$(uname -s)" != "Linux" ]; then
    fail "This uninstall script only supports Linux."
fi

if [ "$(id -u)" != "0" ]; then
    fail "Root privileges are required. Connect as root or run this script with a root account."
fi

if ! command -v systemctl >/dev/null 2>&1 || [ ! -d /run/systemd/system ]; then
    fail "systemd is required. OpenWrt/OpenRC systems are not supported by this script."
fi

backup_and_remove_config() {
    config_file=/etc/sing-box/config.json

    if [ -f "$config_file" ]; then
        backup_file="/root/sing-box-config-backup-before-uninstall-$(date +%Y%m%d-%H%M%S).json"
        cp -p "$config_file" "$backup_file"
        rm -f "$config_file"
        printf 'Existing sing-box config backed up to %s and removed.\n' "$backup_file"
    else
        printf 'sing-box config not found: %s\n' "$config_file"
    fi

    rmdir /etc/sing-box 2>/dev/null || true
}

remove_package() {
    removed=0

    if command -v dpkg-query >/dev/null 2>&1 \
        && dpkg-query -W -f='${Status}' sing-box 2>/dev/null | grep -q 'install ok installed'; then
        if command -v apt-get >/dev/null 2>&1; then
            export DEBIAN_FRONTEND=noninteractive
            apt-get purge -y sing-box || apt-get remove -y sing-box
            removed=1
        fi
    elif command -v rpm >/dev/null 2>&1 && rpm -q sing-box >/dev/null 2>&1; then
        if command -v dnf >/dev/null 2>&1; then
            dnf remove -y sing-box
            removed=1
        elif command -v yum >/dev/null 2>&1; then
            yum remove -y sing-box
            removed=1
        elif command -v zypper >/dev/null 2>&1; then
            zypper --non-interactive remove sing-box
            removed=1
        fi
    elif command -v pacman >/dev/null 2>&1 && pacman -Q sing-box >/dev/null 2>&1; then
        pacman -Rns --noconfirm sing-box
        removed=1
    fi

    if [ "$removed" -eq 0 ]; then
        warn "sing-box package was not found through a supported package manager."
    fi
}

printf 'Stopping sing-box service...\n'
if systemctl list-unit-files sing-box.service >/dev/null 2>&1 \
    || systemctl status sing-box >/dev/null 2>&1; then
    systemctl stop sing-box 2>/dev/null || true
    systemctl disable sing-box >/dev/null 2>&1 || true
else
    printf 'sing-box service is not registered.\n'
fi

backup_and_remove_config
remove_package

systemctl daemon-reload

rm -rf /var/lib/sing-box /var/log/sing-box 2>/dev/null || true

printf '\n'
printf 'sing-box reality client uninstall completed.\n'
printf 'Stopping the service also tears down the TUN interface and its routes if TUN was enabled.\n'
