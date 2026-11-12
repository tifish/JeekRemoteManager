set -eu
umask 077

fail() {
    printf 'ERROR: %s\n' "$1" >&2
    exit 1
}

warn() {
    printf 'WARNING: %s\n' "$1" >&2
}

PORT=${PORT:-}

if [ "$(uname -s)" != "Linux" ]; then
    fail "This uninstall script only supports Linux."
fi

if [ "$(id -u)" != "0" ]; then
    fail "Root privileges are required. Connect as root or run this script with a root account."
fi

if ! command -v systemctl >/dev/null 2>&1 || [ ! -d /run/systemd/system ]; then
    fail "systemd is required. OpenWrt/OpenRC systems are not supported by this script."
fi

if [ -n "$PORT" ]; then
    case "$PORT" in
        *[!0-9]*)
            fail "PORT must be a number between 1 and 65535."
            ;;
    esac

    if [ "$PORT" -lt 1 ] || [ "$PORT" -gt 65535 ]; then
        fail "PORT must be a number between 1 and 65535."
    fi
fi

remove_firewall_rule() {
    if [ -z "$PORT" ]; then
        printf 'PORT is empty. Skipping local firewall cleanup.\n'
        return 0
    fi

    if command -v ufw >/dev/null 2>&1 && ufw status 2>/dev/null | grep -qi '^Status: active'; then
        if ufw --force delete allow "${PORT}/tcp"; then
            printf 'ufw removed %s/tcp\n' "$PORT"
        else
            warn "ufw is active but could not remove ${PORT}/tcp. The rule may not exist."
        fi
    elif command -v firewall-cmd >/dev/null 2>&1 && systemctl is-active --quiet firewalld 2>/dev/null; then
        if firewall-cmd --permanent --remove-port="${PORT}/tcp" && firewall-cmd --reload; then
            printf 'firewalld removed %s/tcp\n' "$PORT"
        else
            warn "firewalld is active but could not remove ${PORT}/tcp. The rule may not exist."
        fi
    else
        printf 'No active supported local firewall detected. Skipping local firewall cleanup.\n'
    fi
}


printf 'Stopping Xray service (shared by the xray reality server/client suites)...\n'
if ! systemctl stop xray.service 2>/dev/null; then
    if systemctl is-active --quiet xray.service; then
        fail "Could not stop Xray. No files were removed."
    fi
fi
systemctl disable xray.service >/dev/null 2>&1 || true
config_file=/usr/local/etc/xray/config.json
if [ -f "$config_file" ]; then
    backup_file="/root/xray-config-backup-before-uninstall-$(date +%Y%m%d-%H%M%S).json"
    cp -p "$config_file" "$backup_file"
    printf 'Xray config backed up to %s\n' "$backup_file"
fi
rm -f "$config_file" /usr/local/etc/xray/jeekremote-reality-link.conf
rm -f /usr/local/bin/xray /etc/systemd/system/xray.service /etc/systemd/system/xray@.service
rm -f /etc/systemd/system/xray.service.d/10-donot_touch_single_conf.conf
rm -f /etc/systemd/system/xray@.service.d/10-donot_touch_single_conf.conf
rmdir /etc/systemd/system/xray.service.d /etc/systemd/system/xray@.service.d /usr/local/etc/xray 2>/dev/null || true
systemctl daemon-reload
printf 'Xray uninstall completed. Backups, geodata, logs and BBR settings were left unchanged.\n'
remove_firewall_rule
