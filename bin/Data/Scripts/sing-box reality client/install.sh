set -eu

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

SERVER_LINK=${SERVER_LINK:-}
LISTEN_PORT=${LISTEN_PORT:-}
ALLOW_EXTERNAL=${ALLOW_EXTERNAL:-false}
ENABLE_TUN=${ENABLE_TUN:-false}

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

case "$ENABLE_TUN" in
    true|false)
        ;;
    *)
        fail "ENABLE_TUN must be true or false."
        ;;
esac

if [ "$ALLOW_EXTERNAL" = "true" ]; then
    listen_address=0.0.0.0
else
    listen_address=127.0.0.1
fi

# Parse the vless:// REALITY share link produced by the server install script.
case "$SERVER_LINK" in
    "")
        fail "SERVER_LINK is required. Paste the vless:// REALITY link from the server."
        ;;
    vless://*)
        ;;
    *)
        fail "SERVER_LINK must start with vless:// (a REALITY share link)."
        ;;
esac

link_body=${SERVER_LINK#vless://}

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
        fail "SERVER_LINK is missing the UUID. Expected vless://UUID@host:port?...."
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
        fail "SERVER_LINK is missing the server port. Expected vless://UUID@host:port?...."
        ;;
esac

flow=
security=
sni=
fingerprint=chrome
public_key=
short_id=

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
        fail "SERVER_LINK has an invalid UUID."
        ;;
esac

case "$host" in
    ""|*[!0-9A-Za-z.:_-]*)
        fail "SERVER_LINK has an invalid server host."
        ;;
esac

case "$server_port" in
    ""|*[!0-9]*)
        fail "SERVER_LINK has an invalid server port."
        ;;
esac

if [ "$server_port" -lt 1 ] || [ "$server_port" -gt 65535 ]; then
    fail "SERVER_LINK has an invalid server port."
fi

if [ "$security" != "reality" ]; then
    fail "SERVER_LINK is not a REALITY link (security='$security'). This script only supports REALITY."
fi

case "$sni" in
    ""|*[!A-Za-z0-9.-]*|.*|*.|-*|*-|*..*)
        fail "SERVER_LINK has an invalid SNI."
        ;;
esac

case "$public_key" in
    ""|*[!0-9A-Za-z_-]*)
        fail "SERVER_LINK is missing a valid public key (pbk)."
        ;;
esac

case "$short_id" in
    *[!0-9A-Fa-f]*)
        fail "SERVER_LINK has an invalid short_id (sid)."
        ;;
esac

case "$transport_type" in
    ""|tcp)
        transport_type=tcp
        ;;
    *)
        fail "SERVER_LINK uses unsupported transport '$transport_type'. Only tcp REALITY is supported."
        ;;
esac

case "$fingerprint" in
    ""|*[!0-9A-Za-z]*)
        fingerprint=chrome
        ;;
esac

if [ -n "$flow" ]; then
    case "$flow" in
        *[!A-Za-z-]*)
            fail "SERVER_LINK has an invalid flow value."
            ;;
    esac
fi

# When TUN is enabled, keep the SSH session that runs this script reachable so
# enabling system-wide routing does not lock the connection out.
ssh_client_ip=
if [ -n "${SSH_CONNECTION:-}" ]; then
    ssh_client_ip=${SSH_CONNECTION%% *}
elif [ -n "${SSH_CLIENT:-}" ]; then
    ssh_client_ip=${SSH_CLIENT%% *}
fi
case "$ssh_client_ip" in
    ""|*[!0-9A-Fa-f:.]*)
        ssh_client_ip=
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

ensure_official_installer_dependencies

printf 'Installing or updating sing-box to the latest available version...\n'
tmp_install=$(mktemp)
download_to_file https://sing-box.app/install.sh "$tmp_install" ||
    fail "Could not download the official sing-box install script."
sh "$tmp_install" ||
    fail "The official sing-box install script failed."

sing_box=$(find_sing_box) ||
    fail "sing-box was installed, but the sing-box command could not be found."

tun_inbound=
if [ "$ENABLE_TUN" = "true" ]; then
    tun_inbound="    {
      \"type\": \"tun\",
      \"tag\": \"tun-in\",
      \"address\": [
        \"172.18.0.1/30\"
      ],
      \"auto_route\": true,
      \"strict_route\": true,
      \"stack\": \"system\"
    },
"
fi

flow_line=
if [ -n "$flow" ]; then
    flow_line="      \"flow\": \"$flow\",
"
fi

route_rules=
if [ "$ENABLE_TUN" = "true" ] && [ -n "$ssh_client_ip" ]; then
    case "$ssh_client_ip" in
        *:*) ssh_cidr="$ssh_client_ip/128" ;;
        *) ssh_cidr="$ssh_client_ip/32" ;;
    esac
    route_rules="      {
        \"ip_cidr\": [
          \"$ssh_cidr\"
        ],
        \"outbound\": \"direct\"
      },
"
fi

mkdir -p /etc/sing-box
tmp_config=$(mktemp)
printf 'Writing sing-box reality client config from the current parameters...\n'

cat > "$tmp_config" <<EOF_CONFIG
{
  "log": {
    "level": "info",
    "timestamp": true
  },
  "inbounds": [
${tun_inbound}    {
      "type": "mixed",
      "tag": "mixed-in",
      "listen": "$listen_address",
      "listen_port": $LISTEN_PORT
    }
  ],
  "outbounds": [
    {
      "type": "vless",
      "tag": "proxy",
      "server": "$host",
      "server_port": $server_port,
      "uuid": "$uuid",
${flow_line}      "tls": {
        "enabled": true,
        "server_name": "$sni",
        "utls": {
          "enabled": true,
          "fingerprint": "$fingerprint"
        },
        "reality": {
          "enabled": true,
          "public_key": "$public_key",
          "short_id": "$short_id"
        }
      }
    },
    {
      "type": "direct",
      "tag": "direct"
    }
  ],
  "route": {
    "auto_detect_interface": true,
    "rules": [
${route_rules}      {
        "ip_is_private": true,
        "outbound": "direct"
      }
    ],
    "final": "proxy"
  }
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

printf '\n'
printf 'sing-box reality client install/update completed.\n'
printf 'Repeated runs update sing-box and replace the config with the current parameters.\n'
printf 'Server: %s:%s\n' "$host" "$server_port"
printf 'SNI: %s\n' "$sni"
printf 'Mixed (SOCKS/HTTP) proxy: %s:%s\n' "$listen_address" "$LISTEN_PORT"
printf 'Allow external connections: %s\n' "$ALLOW_EXTERNAL"
printf 'TUN: %s\n' "$ENABLE_TUN"

if [ "$ALLOW_EXTERNAL" = "true" ]; then
    printf '\n'
    printf 'The mixed inbound listens on all interfaces with no authentication.\n'
    printf 'Restrict %s/tcp with a firewall so it is not left open to the public network.\n' "$LISTEN_PORT"
else
    printf '\n'
    printf 'The mixed inbound listens on 127.0.0.1 only and is not reachable from other hosts.\n'
    printf 'Set ALLOW_EXTERNAL to true to accept connections from the local network.\n'
fi

if [ "$ENABLE_TUN" = "true" ]; then
    printf '\n'
    if [ -n "$ssh_client_ip" ]; then
        printf 'TUN routes all traffic through the proxy. Traffic to %s is kept direct to protect this SSH session.\n' "$ssh_client_ip"
    else
        warn "TUN routes all traffic through the proxy and the SSH client address could not be detected. This SSH session may drop. Keep console access available."
    fi
fi
