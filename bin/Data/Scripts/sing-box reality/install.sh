set -eu

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

    printf 'curl is required by the official sing-box install script. Trying to install curl and ca-certificates...\n'
    install_downloader_packages || true

    if command -v curl >/dev/null 2>&1; then
        return 0
    fi

    fail "curl is required by the official sing-box install script, and curl could not be installed."
}

download_to_file() {
    url=$1
    target=$2

    if command -v curl >/dev/null 2>&1; then
        curl -fsSL -o "$target" "$url"
    elif command -v wget >/dev/null 2>&1; then
        wget -qO "$target" "$url"
    else
        return 1
    fi
}

download_to_stdout() {
    url=$1

    if command -v curl >/dev/null 2>&1; then
        curl -fsSL "$url"
    elif command -v wget >/dev/null 2>&1; then
        wget -qO- "$url"
    else
        return 1
    fi
}

find_sing_box() {
    if command -v sing-box >/dev/null 2>&1; then
        command -v sing-box
    elif [ -x /usr/local/bin/sing-box ]; then
        printf '/usr/local/bin/sing-box\n'
    elif [ -x /usr/bin/sing-box ]; then
        printf '/usr/bin/sing-box\n'
    else
        return 1
    fi
}

bbr_fail() {
    printf 'ERROR: %s\n' "$1" >&2
    return 1
}

set_sysctl_conf_value_strict() {
    config_file=$1
    config_key=$2
    config_value=$3
    tmp_bbr_file="${config_file}.tmp.$$"

    if [ ! -f "$config_file" ] && ! : > "$config_file"; then
        bbr_fail "Could not create $config_file."
        return 1
    fi

    if ! awk -v key="$config_key" -v value="$config_value" '
        BEGIN { updated = 0 }
        {
            trimmed = $0
            sub(/^[[:space:]]*/, "", trimmed)
            if (substr(trimmed, 1, 1) != "#") {
                separator = index(trimmed, "=")
                if (separator > 0) {
                    lhs = substr(trimmed, 1, separator - 1)
                    gsub(/[[:space:]]/, "", lhs)
                    if (lhs == key) {
                        if (!updated) {
                            print key " = " value
                            updated = 1
                        }
                        next
                    }
                }
            }

            print
        }
        END {
            if (!updated)
                print key " = " value
        }
    ' "$config_file" > "$tmp_bbr_file"; then
        rm -f "$tmp_bbr_file"
        bbr_fail "Could not update $config_file."
        return 1
    fi

    if ! mv "$tmp_bbr_file" "$config_file"; then
        rm -f "$tmp_bbr_file"
        bbr_fail "Could not write $config_file."
        return 1
    fi
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

    if ! set_sysctl_conf_value_strict /etc/sysctl.conf net.core.default_qdisc fq; then
        return 1
    fi

    if ! set_sysctl_conf_value_strict /etc/sysctl.conf net.ipv4.tcp_congestion_control bbr; then
        return 1
    fi

    sysctl -w net.core.default_qdisc=fq
    sysctl -w net.ipv4.tcp_congestion_control=bbr

    current_qdisc=$(sysctl -n net.core.default_qdisc 2>/dev/null || printf 'unknown')
    current_cc=$(sysctl -n net.ipv4.tcp_congestion_control 2>/dev/null || printf 'unknown')

    printf 'BBR configuration written to /etc/sysctl.conf\n'
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
        warn "BBR could not be enabled. sing-box deployment will continue."
        printf '%s\n' "$bbr_output" >&2
        bbr_status=warning
    fi
}

generate_uuid() {
    sing_box_bin=$1

    if uuid_value=$("$sing_box_bin" generate uuid 2>/dev/null); then
        uuid_value=$(printf '%s' "$uuid_value" | tr -d '[:space:]')
        if [ -n "$uuid_value" ]; then
            printf '%s\n' "$uuid_value"
            return 0
        fi
    fi

    if [ -r /proc/sys/kernel/random/uuid ]; then
        tr -d '[:space:]' < /proc/sys/kernel/random/uuid
        printf '\n'
        return 0
    fi

    return 1
}

generate_short_id() {
    sing_box_bin=$1

    if short_value=$("$sing_box_bin" generate rand --hex 8 2>/dev/null); then
        short_value=$(printf '%s' "$short_value" | tr -d '[:space:]')
        if [ -n "$short_value" ]; then
            printf '%s\n' "$short_value"
            return 0
        fi
    fi

    if [ -r /dev/urandom ] && command -v od >/dev/null 2>&1; then
        od -An -N8 -tx1 /dev/urandom | tr -d ' \n'
        printf '\n'
        return 0
    fi

    return 1
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

ensure_official_installer_dependencies

printf 'Installing or updating sing-box to the latest available version...\n'
tmp_install=$(mktemp)
download_to_file https://sing-box.app/install.sh "$tmp_install" ||
    fail "Could not download the official sing-box install script."
sh "$tmp_install" ||
    fail "The official sing-box install script failed."

sing_box=$(find_sing_box) ||
    fail "sing-box was installed, but the sing-box command could not be found."

uuid=$(generate_uuid "$sing_box") ||
    fail "Could not generate a UUID."

key_pair=$("$sing_box" generate reality-keypair) ||
    fail "Could not generate a REALITY key pair."
private_key=$(printf '%s\n' "$key_pair" | awk '/PrivateKey/ {print $2; exit}' | tr -d '"')
public_key=$(printf '%s\n' "$key_pair" | awk '/PublicKey/ {print $2; exit}' | tr -d '"')

if [ -z "$private_key" ] || [ -z "$public_key" ]; then
    fail "Could not parse the REALITY key pair generated by sing-box."
fi

short_id=$(generate_short_id "$sing_box") ||
    fail "Could not generate a REALITY short_id."

printf 'Enabling BBR if supported...\n'
enable_bbr_best_effort

mkdir -p /etc/sing-box
tmp_config=$(mktemp)
printf 'Writing sing-box reality config from current PORT and SNI parameters...\n'

cat > "$tmp_config" <<EOF_CONFIG
{
  "log": {
    "level": "info",
    "timestamp": true
  },
  "inbounds": [
    {
      "type": "vless",
      "tag": "vless-in",
      "listen": "0.0.0.0",
      "listen_port": $PORT,
      "users": [
        {
          "uuid": "$uuid",
          "flow": "xtls-rprx-vision"
        }
      ],
      "tls": {
        "enabled": true,
        "server_name": "$SNI",
        "reality": {
          "enabled": true,
          "handshake": {
            "server": "$SNI",
            "server_port": 443
          },
          "private_key": "$private_key",
          "short_id": [
            "$short_id"
          ]
        }
      }
    }
  ],
  "outbounds": [
    {
      "type": "direct",
      "tag": "direct"
    },
    {
      "type": "block",
      "tag": "block"
    }
  ]
}
EOF_CONFIG

"$sing_box" check -c "$tmp_config" ||
    fail "Generated sing-box config did not pass sing-box check."

config_file=/etc/sing-box/config.json
if [ -f "$config_file" ]; then
    backup_file="${config_file}.backup-$(date +%Y%m%d-%H%M%S)"
    cp -p "$config_file" "$backup_file"
    printf 'Existing sing-box config backed up to %s\n' "$backup_file"
fi

mv "$tmp_config" "$config_file"
tmp_config=

configure_firewall

if ! systemctl enable sing-box >/dev/null 2>&1; then
    warn "Could not enable sing-box at boot. The service restart will still be attempted."
fi

if ! systemctl restart sing-box; then
    systemctl status sing-box --no-pager -l || true
    fail "Could not restart sing-box."
fi

if ! systemctl is-active --quiet sing-box; then
    systemctl status sing-box --no-pager -l || true
    fail "sing-box service is not active after restart."
fi

server_address=$(detect_public_address)
case "$server_address" in
    *:*)
        uri_host="[$server_address]"
        ;;
    *)
        uri_host="$server_address"
        ;;
esac

vless_uri="vless://${uuid}@${uri_host}:${PORT}?encryption=none&flow=xtls-rprx-vision&security=reality&sni=${SNI}&fp=chrome&pbk=${public_key}&sid=${short_id}&type=tcp#sing-box-reality"
link_info_file=/etc/sing-box/jeekremote-reality-link.conf
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
printf 'sing-box reality install/update completed.\n'
printf 'Repeated runs update sing-box and replace the config with the current PORT and SNI.\n'
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
