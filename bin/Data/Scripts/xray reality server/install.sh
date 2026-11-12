set -eu
umask 077

tmp_config=
tmp_install=
tmp_link_info=

cleanup() {
    if [ -n "$tmp_config" ]; then
        rm -f "$tmp_config"
    fi
    if [ -n "$tmp_install" ]; then
        rm -f "$tmp_install"
    fi
    if [ -n "$tmp_link_info" ]; then
        rm -f "$tmp_link_info"
    fi
}

trap cleanup EXIT HUP INT TERM

fail() {
    printf 'ERROR: %s\n' "$1" >&2
    exit 1
}

warn() {
    printf 'WARNING: %s\n' "$1" >&2
}

PORT=${PORT:-}
SNI=${SNI:-}

if [ "$(uname -s)" != "Linux" ]; then
    fail "This deployment script only supports Linux."
fi

if [ "$(id -u)" != "0" ]; then
    fail "Root privileges are required. Connect as root or run this script with a root account."
fi

if ! command -v systemctl >/dev/null 2>&1 || [ ! -d /run/systemd/system ]; then
    fail "systemd is required. OpenWrt/OpenRC systems are not supported by this script."
fi

case "$PORT" in
    ""|*[!0-9]*)
        fail "PORT must be a number between 1 and 65535."
        ;;
esac

if [ "$PORT" -lt 1 ] || [ "$PORT" -gt 65535 ]; then
    fail "PORT must be a number between 1 and 65535."
fi

case "$SNI" in
    ""|*[!A-Za-z0-9.-]*|.*|*.|-*|*-|*..*)
        fail "SNI must be a hostname such as www.example.com."
        ;;
esac

install_downloader_packages() {
    if command -v apt-get >/dev/null 2>&1; then
        export DEBIAN_FRONTEND=noninteractive
        apt-get update
        apt-get install -y curl ca-certificates
    elif command -v dnf >/dev/null 2>&1; then
        dnf install -y curl ca-certificates
    elif command -v yum >/dev/null 2>&1; then
        yum install -y curl ca-certificates
    elif command -v zypper >/dev/null 2>&1; then
        zypper --non-interactive install curl ca-certificates
    elif command -v pacman >/dev/null 2>&1; then
        pacman -Sy --noconfirm --needed curl ca-certificates
    else
        return 1
    fi
}

ensure_official_installer_dependencies() {
    if command -v curl >/dev/null 2>&1; then
        return 0
    fi

    printf 'curl is required by the official Xray install script. Trying to install curl and ca-certificates...\n'
    install_downloader_packages || true

    if command -v curl >/dev/null 2>&1; then
        return 0
    fi

    fail "curl is required by the official Xray install script, and curl could not be installed."
}

download_to_file() {
    url=$1
    target=$2

    if command -v curl >/dev/null 2>&1; then
        curl -fsSL --connect-timeout 5 --max-time 120 -o "$target" "$url"
    elif command -v wget >/dev/null 2>&1; then
        wget -T 30 -qO "$target" "$url"
    else
        return 1
    fi
}

download_to_stdout() {
    url=$1

    if command -v curl >/dev/null 2>&1; then
        curl -fsSL --connect-timeout 5 --max-time 120 "$url"
    elif command -v wget >/dev/null 2>&1; then
        wget -T 30 -qO- "$url"
    else
        return 1
    fi
}

bbr_fail() {
    printf 'ERROR: %s\n' "$1" >&2
    return 1
}

enable_bbr_strict() {
    if [ "$(uname -s)" != "Linux" ]; then
        bbr_fail "BBR can only be enabled on Linux."
        return 1
    fi

    if ! command -v sysctl >/dev/null 2>&1; then
        bbr_fail "sysctl is not available on this server."
        return 1
    fi

    if [ "$(id -u)" != "0" ]; then
        bbr_fail "Root privileges are required. Connect as root or run this script with a root account."
        return 1
    fi

    available_file=/proc/sys/net/ipv4/tcp_available_congestion_control
    if [ -r "$available_file" ] && ! grep -qw bbr "$available_file"; then
        if command -v modprobe >/dev/null 2>&1; then
            modprobe tcp_bbr 2>/dev/null || true
        fi
    fi

    if [ -r "$available_file" ] && ! grep -qw bbr "$available_file"; then
        available=$(cat "$available_file")
        bbr_fail "Kernel does not report BBR support. Available congestion controls: $available"
        return 1
    fi

    bbr_config_file=/etc/sysctl.d/99-jeekremote-bbr.conf
    if ! mkdir -p /etc/sysctl.d; then
        bbr_fail "Could not create /etc/sysctl.d."
        return 1
    fi

    if ! cat > "$bbr_config_file" <<'EOF'
# Managed by JeekRemoteManager.
net.core.default_qdisc = fq
net.ipv4.tcp_congestion_control = bbr
EOF
    then
        bbr_fail "Could not write $bbr_config_file."
        return 1
    fi
    chmod 0644 "$bbr_config_file" 2>/dev/null || true

    sysctl -w net.core.default_qdisc=fq
    sysctl -w net.ipv4.tcp_congestion_control=bbr

    current_qdisc=$(sysctl -n net.core.default_qdisc 2>/dev/null || printf 'unknown')
    current_cc=$(sysctl -n net.ipv4.tcp_congestion_control 2>/dev/null || printf 'unknown')

    printf 'BBR configuration written to %s\n' "$bbr_config_file"
    printf 'net.core.default_qdisc=%s\n' "$current_qdisc"
    printf 'net.ipv4.tcp_congestion_control=%s\n' "$current_cc"

    if [ "$current_cc" != "bbr" ]; then
        bbr_fail "BBR was configured but is not active."
        return 1
    fi

    printf 'BBR is enabled.\n'
}

enable_bbr_best_effort() {
    if bbr_output=$(enable_bbr_strict 2>&1); then
        printf '%s\n' "$bbr_output"
        bbr_status=enabled
    else
        warn "BBR could not be enabled. Xray deployment will continue."
        printf '%s\n' "$bbr_output" >&2
        bbr_status=warning
    fi
}

detect_public_address() {
    for address_url in \
        https://api.ipify.org \
        https://ifconfig.me/ip \
        https://icanhazip.com
    do
        address=$(download_to_stdout "$address_url" 2>/dev/null | tr -d '[:space:]' || true)
        case "$address" in
            ""|*[!A-Za-z0-9:.-]*)
                ;;
            *.*|*:*)
                printf '%s\n' "$address"
                return 0
                ;;
        esac
    done

    printf 'YOUR_SERVER_ADDRESS\n'
}

configure_firewall() {
    if command -v ufw >/dev/null 2>&1 && ufw status 2>/dev/null | grep -qi '^Status: active'; then
        if ufw allow "${PORT}/tcp"; then
            printf 'ufw allowed %s/tcp\n' "$PORT"
        else
            warn "ufw is active but could not allow ${PORT}/tcp."
        fi
    elif command -v firewall-cmd >/dev/null 2>&1 && systemctl is-active --quiet firewalld 2>/dev/null; then
        if firewall-cmd --permanent --add-port="${PORT}/tcp" && firewall-cmd --reload; then
            printf 'firewalld allowed %s/tcp\n' "$PORT"
        else
            warn "firewalld is active but could not allow ${PORT}/tcp."
        fi
    else
        printf 'No active supported local firewall detected. Skipping local firewall changes.\n'
    fi
}


find_xray() {
    if [ -x /usr/local/bin/xray ]; then
        printf '/usr/local/bin/xray\n'
    else
        return 1
    fi
}

install_or_update_xray() {
    if [ "${UPDATE_XRAY:-true}" = "false" ]; then
        printf 'Skipping Xray install/update because UPDATE_XRAY=false.\n'
        return 0
    fi
    ensure_official_installer_dependencies
    command -v bash >/dev/null 2>&1 || fail "bash is required by the official Xray installer."
    tmp_install=$(mktemp)
    download_to_file https://raw.githubusercontent.com/XTLS/Xray-install/main/install-release.sh "$tmp_install" || return 1
    # Use the official single-config layout, and activate only after validation.
    JSON_PATH=/usr/local/etc/xray JSONS_PATH= bash "$tmp_install" install --without-geodata --manually-restart-later
}

activate_config() {
    config_file=/usr/local/etc/xray/config.json
    if [ -f "$config_file" ]; then
        backup_file="${config_file}.backup-$(date +%Y%m%d-%H%M%S)"
        cp -p "$config_file" "$backup_file"
        printf 'Existing Xray config backed up to %s\n' "$backup_file"
    fi
    # Official installers have used both nobody and xray service accounts.
    service_user=$(systemctl show xray.service -p User --value)
    service_user=${service_user:-root}
    service_group=$(id -gn "$service_user") || fail "Could not resolve the Xray service account."
    chown "root:$service_group" /usr/local/etc/xray "$tmp_config"
    chmod 0750 /usr/local/etc/xray
    chmod 0640 "$tmp_config"
    mv "$tmp_config" "$config_file"
    tmp_config=
    systemctl enable xray
    if ! systemctl restart xray || ! systemctl is-active --quiet xray; then
        systemctl status xray --no-pager -l || true
        fail "Xray service is not active after restart."
    fi
}

printf 'Installing or updating Xray...\n'
if ! install_or_update_xray; then
    find_xray >/dev/null || fail "Could not install Xray and no existing official-layout binary was found."
    warn "Xray update failed. Continuing with the existing binary."
fi
xray=$(find_xray) || fail "Install Xray first, or enable UPDATE_XRAY."

uuid=$("$xray" uuid) || fail "Could not generate a UUID."
key_pair=$("$xray" x25519) || fail "Could not generate a REALITY key pair."
private_key=$(printf '%s\n' "$key_pair" | awk -F': *' '/^Private[ Kk]ey:|^PrivateKey:/ {print $2; exit}')
public_key=$(printf '%s\n' "$key_pair" | awk -F': *' '/^Password( \(PublicKey\))?:|^Public[ Kk]ey:|^PublicKey:/ {print $2; exit}')
[ -n "$private_key" ] && [ -n "$public_key" ] || fail "Could not parse the Xray REALITY key pair."
short_id=$(od -An -N8 -tx1 /dev/urandom | tr -d ' \n')
[ "${#short_id}" -eq 16 ] || fail "Could not generate a REALITY short ID."
enable_bbr_best_effort
mkdir -p /usr/local/etc/xray
tmp_config=$(mktemp /usr/local/etc/xray/.jeekremote-config.XXXXXX.json)
cat > "$tmp_config" <<EOF_CONFIG
{
  "log": { "loglevel": "warning" },
  "inbounds": [{
    "tag": "vless-in", "listen": "0.0.0.0", "port": $PORT,
    "protocol": "vless",
    "settings": {
      "clients": [{ "id": "$uuid", "flow": "xtls-rprx-vision" }],
      "decryption": "none"
    },
    "streamSettings": {
      "network": "raw", "security": "reality",
      "realitySettings": {
        "show": false, "target": "$SNI:443", "xver": 0,
        "serverNames": ["$SNI"], "privateKey": "$private_key",
        "shortIds": ["$short_id"]
      }
    }
  }],
  "outbounds": [{ "tag": "direct", "protocol": "freedom" }]
}
EOF_CONFIG
"$xray" run -test -format json -config "$tmp_config" || fail "Generated Xray config did not pass validation."
activate_config
configure_firewall
server_address=$(detect_public_address)
case "$server_address" in
    *:*)
        uri_host="[$server_address]"
        ;;
    *)
        uri_host="$server_address"
        ;;
esac

vless_uri="vless://${uuid}@${uri_host}:${PORT}?encryption=none&flow=xtls-rprx-vision&security=reality&sni=${SNI}&fp=chrome&pbk=${public_key}&sid=${short_id}&type=tcp#xray-reality-server"
link_info_file=/usr/local/etc/xray/jeekremote-reality-link.conf
tmp_link_info=$(mktemp)
cat > "$tmp_link_info" <<EOF_LINK_INFO
SERVER_ADDRESS=$server_address
PORT=$PORT
SNI=$SNI
UUID=$uuid
PUBLIC_KEY=$public_key
SHORT_ID=$short_id
EOF_LINK_INFO
chmod 600 "$tmp_link_info"
mv "$tmp_link_info" "$link_info_file"
tmp_link_info=

printf '\n'
printf 'xray reality server install/update completed.\n'
printf 'Repeated runs update Xray and replace the config with the current PORT and SNI.\n'
printf 'Server address: %s\n' "$server_address"
printf 'Port: %s\n' "$PORT"
printf 'SNI: %s\n' "$SNI"
printf 'UUID: %s\n' "$uuid"
printf 'Public key: %s\n' "$public_key"
printf 'Short ID: %s\n' "$short_id"
printf 'Flow: xtls-rprx-vision\n'
printf 'BBR: %s\n' "$bbr_status"
printf '\n'
printf 'Client URI:\n'
printf '%s\n' "$vless_uri"
printf '\n'
printf 'If the server is behind a cloud security group, allow %s/tcp there as well.\n' "$PORT"

if [ "$server_address" = "YOUR_SERVER_ADDRESS" ]; then
    warn "Public address detection failed. Replace YOUR_SERVER_ADDRESS in the URI manually."
fi
