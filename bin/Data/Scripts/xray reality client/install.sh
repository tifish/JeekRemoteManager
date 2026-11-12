set -eu
umask 077
set -f

tmp_config=
tmp_install=

cleanup() {
    if [ -n "$tmp_config" ]; then
        rm -f "$tmp_config"
    fi
    if [ -n "$tmp_install" ]; then
        rm -f "$tmp_install"
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

SERVER_LINK_1=${SERVER_LINK_1:-}
SERVER_LINK_2=${SERVER_LINK_2:-}
SERVER_LINK_3=${SERVER_LINK_3:-}
SERVER_LINK_4=${SERVER_LINK_4:-}
SERVER_LINK_5=${SERVER_LINK_5:-}
SERVER_LINK_6=${SERVER_LINK_6:-}
SERVER_LINK_7=${SERVER_LINK_7:-}
SERVER_LINK_8=${SERVER_LINK_8:-}
SERVER_LINK_9=${SERVER_LINK_9:-}
LISTEN_PORT=${LISTEN_PORT:-}
ALLOW_EXTERNAL=${ALLOW_EXTERNAL:-false}
UPDATE_XRAY=${UPDATE_XRAY:-true}

if [ "$(uname -s)" != "Linux" ]; then
    fail "This deployment script only supports Linux."
fi

if [ "$(id -u)" != "0" ]; then
    fail "Root privileges are required. Connect as root or run this script with a root account."
fi

if ! command -v systemctl >/dev/null 2>&1 || [ ! -d /run/systemd/system ]; then
    fail "systemd is required. OpenWrt/OpenRC systems are not supported by this script."
fi

case "$LISTEN_PORT" in
    ""|*[!0-9]*)
        fail "LISTEN_PORT must be a number between 1 and 65535."
        ;;
esac

if [ "$LISTEN_PORT" -lt 1 ] || [ "$LISTEN_PORT" -gt 65535 ]; then
    fail "LISTEN_PORT must be a number between 1 and 65535."
fi

case "$ALLOW_EXTERNAL" in
    true|false)
        ;;
    *)
        fail "ALLOW_EXTERNAL must be true or false."
        ;;
esac

case "$UPDATE_XRAY" in
    true|false)
        ;;
    *)
        fail "UPDATE_XRAY must be true or false."
        ;;
esac

if [ "$ALLOW_EXTERNAL" = "true" ]; then
    listen_address=0.0.0.0
else
    listen_address=127.0.0.1
fi

# Parse the vless:// REALITY share links produced by the server install script.
# Up to 9 links are supported. Each filled-in link gets its own mixed inbound;
# the first link listens on LISTEN_PORT and every following link on the next
# port (+1 per link, in the order the links are filled in).
max_server_links=9

parse_server_link() {
    link=$1
    link_label=$2

    case "$link" in
        vless://*)
            ;;
        *)
            fail "$link_label must start with vless:// (a REALITY share link)."
            ;;
    esac

    link_body=${link#vless://}

    # Drop the #name fragment.
    case "$link_body" in
        *#*)
            link_body=${link_body%%#*}
            ;;
    esac

    # Split off the ?query part.
    link_query=
    case "$link_body" in
        *\?*)
            link_query=${link_body#*\?}
            link_body=${link_body%%\?*}
            ;;
    esac

    case "$link_body" in
        *@*)
            ;;
        *)
            fail "$link_label is missing the UUID. Expected vless://UUID@host:port?...."
            ;;
    esac

    uuid=${link_body%%@*}
    authority=${link_body#*@}

    case "$authority" in
        \[*\]:*)
            host=${authority#\[}
            host=${host%%\]*}
            server_port=${authority##*]:}
            ;;
        *:*)
            host=${authority%:*}
            server_port=${authority##*:}
            ;;
        *)
            fail "$link_label is missing the server port. Expected vless://UUID@host:port?...."
            ;;
    esac

    flow=
    security=
    sni=
    fingerprint=chrome
    public_key=
    short_id=
    transport_type=

    old_ifs=$IFS
    IFS='&'
    for kv in $link_query; do
        key=${kv%%=*}
        value=${kv#*=}
        case "$key" in
            flow) flow=$value ;;
            security) security=$value ;;
            sni) sni=$value ;;
            fp) fingerprint=$value ;;
            pbk) public_key=$value ;;
            sid) short_id=$value ;;
            type) transport_type=$value ;;
        esac
    done
    IFS=$old_ifs
    transport_type=${transport_type:-tcp}

    case "$uuid" in
        ""|*[!0-9A-Fa-f-]*)
            fail "$link_label has an invalid UUID."
            ;;
    esac

    case "$host" in
        ""|*[!0-9A-Za-z.:_-]*)
            fail "$link_label has an invalid server host."
            ;;
    esac

    case "$server_port" in
        ""|*[!0-9]*)
            fail "$link_label has an invalid server port."
            ;;
    esac

    if [ "$server_port" -lt 1 ] || [ "$server_port" -gt 65535 ]; then
        fail "$link_label has an invalid server port."
    fi

    if [ "$security" != "reality" ]; then
        fail "$link_label is not a REALITY link (security='$security'). This script only supports REALITY."
    fi

    case "$sni" in
        ""|*[!A-Za-z0-9.-]*|.*|*.|-*|*-|*..*)
            fail "$link_label has an invalid SNI."
            ;;
    esac

    case "$public_key" in
        ""|*[!0-9A-Za-z_-]*)
            fail "$link_label is missing a valid public key (pbk)."
            ;;
    esac

    if [ "${#public_key}" -ne 43 ]; then
        fail "$link_label has an invalid public key length."
    fi
    if [ "${#short_id}" -gt 16 ] || [ "$(( ${#short_id} % 2 ))" -ne 0 ]; then
        fail "$link_label has an invalid short_id length."
    fi
    case "$short_id" in
        *[!0-9A-Fa-f]*)
            fail "$link_label has an invalid short_id (sid)."
            ;;
    esac

    case "$transport_type" in
        ""|tcp|raw)
            transport_type=tcp
            ;;
        *)
            fail "$link_label uses unsupported transport '$transport_type'. Only tcp REALITY is supported."
            ;;
    esac

    case "$fingerprint" in
        ""|*[!0-9A-Za-z]*)
            fingerprint=chrome
            ;;
    esac

    case "$flow" in
        ""|xtls-rprx-vision) ;;
        *) fail "$link_label uses an unsupported flow." ;;
    esac

}

server_link_count=0
mixed_inbounds=
proxy_outbounds=
inbound_rules=
server_summary=

link_index=0
while [ "$link_index" -lt "$max_server_links" ]; do
    link_index=$((link_index + 1))
    eval "server_link=\$SERVER_LINK_$link_index"
    if [ -z "$server_link" ]; then
        continue
    fi

    server_link_count=$((server_link_count + 1))
    listen_port=$((LISTEN_PORT + server_link_count - 1))
    if [ "$listen_port" -gt 65535 ]; then
        fail "Listen port $listen_port for SERVER_LINK_$link_index is above 65535. Use a lower LISTEN_PORT."
    fi

    parse_server_link "$server_link" "SERVER_LINK_$link_index"

    if [ -n "$mixed_inbounds" ]; then
        mixed_inbounds="$mixed_inbounds,
"
    fi
    mixed_inbounds="$mixed_inbounds    {
      \"protocol\": \"socks\", \"tag\": \"mixed-in-$server_link_count\",
      \"listen\": \"$listen_address\", \"port\": $listen_port,
      \"settings\": { \"auth\": \"noauth\", \"udp\": true }
    }"
    proxy_outbounds="$proxy_outbounds    {
      \"protocol\": \"vless\", \"tag\": \"proxy-$server_link_count\",
      \"settings\": { \"vnext\": [{
        \"address\": \"$host\", \"port\": $server_port,
        \"users\": [{ \"id\": \"$uuid\", \"encryption\": \"none\", \"flow\": \"$flow\" }]
      }] },
      \"streamSettings\": {
        \"network\": \"raw\", \"security\": \"reality\",
        \"realitySettings\": {
          \"serverName\": \"$sni\", \"fingerprint\": \"$fingerprint\",
          \"password\": \"$public_key\", \"shortId\": \"$short_id\"
        }
      }
    },
"

    if [ -n "$inbound_rules" ]; then
        inbound_rules="$inbound_rules,
"
    fi
    inbound_rules="$inbound_rules      {
        \"type\": \"field\",
        \"inboundTag\": [\"mixed-in-$server_link_count\"],
        \"outboundTag\": \"proxy-$server_link_count\"
      }"

    server_summary="${server_summary}Server $server_link_count (SERVER_LINK_$link_index): $host:$server_port (SNI $sni) <- mixed $listen_address:$listen_port
"
done

if [ "$server_link_count" -eq 0 ]; then
    fail "At least one server link is required. Paste the vless:// REALITY link from the server into SERVER_LINK_1."
fi

last_listen_port=$((LISTEN_PORT + server_link_count - 1))
if [ "$server_link_count" -gt 1 ]; then
    listen_port_text="${LISTEN_PORT}-${last_listen_port}"
else
    listen_port_text=$LISTEN_PORT
fi

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

mkdir -p /usr/local/etc/xray
tmp_config=$(mktemp /usr/local/etc/xray/.jeekremote-config.XXXXXX.json)
cat > "$tmp_config" <<EOF_CONFIG
{
  "log": { "loglevel": "warning" },
  "inbounds": [
${mixed_inbounds}
  ],
  "outbounds": [
${proxy_outbounds}    { "tag": "direct", "protocol": "freedom" }
  ],
  "routing": {
    "domainStrategy": "AsIs",
    "rules": [
      { "type": "field", "ip": ["10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "127.0.0.0/8", "169.254.0.0/16", "::1/128", "fc00::/7", "fe80::/10"], "outboundTag": "direct" },
${inbound_rules}
    ]
  }
}
EOF_CONFIG
"$xray" run -test -format json -config "$tmp_config" || fail "Generated Xray config did not pass validation."
activate_config
printf '\nxray reality client install/update completed.\n'
printf '%s' "$server_summary"
printf 'Update Xray: %s\n' "$UPDATE_XRAY"
printf 'Each listening port accepts SOCKS and HTTP proxy connections.\n'
if [ "$ALLOW_EXTERNAL" = "true" ]; then
    warn "Proxy listeners have no authentication. Restrict $listen_port_text/tcp and UDP with a firewall."
fi
