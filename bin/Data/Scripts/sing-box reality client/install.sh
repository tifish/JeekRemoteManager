set -eu

tmp_config=
tmp_package_dir=

cleanup() {
    if [ -n "$tmp_config" ]; then
        rm -f "$tmp_config"
    fi
    if [ -n "$tmp_package_dir" ]; then
        rm -rf "$tmp_package_dir"
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
ENABLE_TUN=${ENABLE_TUN:-false}
UPDATE_SING_BOX=${UPDATE_SING_BOX:-true}
download_connect_timeout_seconds=2
download_response_timeout_seconds=4
download_package_timeout_seconds=20
download_stall_timeout_seconds=3
download_min_speed_bytes=65536

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

case "$UPDATE_SING_BOX" in
    true|false)
        ;;
    *)
        fail "UPDATE_SING_BOX must be true or false."
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

    case "$short_id" in
        *[!0-9A-Fa-f]*)
            fail "$link_label has an invalid short_id (sid)."
            ;;
    esac

    case "$transport_type" in
        ""|tcp)
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

    if [ -n "$flow" ]; then
        case "$flow" in
            *[!A-Za-z-]*)
                fail "$link_label has an invalid flow value."
                ;;
        esac
    fi
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

    flow_line=
    if [ -n "$flow" ]; then
        flow_line="      \"flow\": \"$flow\",
"
    fi

    if [ -n "$mixed_inbounds" ]; then
        mixed_inbounds="$mixed_inbounds,
"
    fi
    mixed_inbounds="$mixed_inbounds    {
      \"type\": \"mixed\",
      \"tag\": \"mixed-in-$server_link_count\",
      \"listen\": \"$listen_address\",
      \"listen_port\": $listen_port
    }"

    proxy_outbounds="$proxy_outbounds    {
      \"type\": \"vless\",
      \"tag\": \"proxy-$server_link_count\",
      \"server\": \"$host\",
      \"server_port\": $server_port,
      \"uuid\": \"$uuid\",
${flow_line}      \"tls\": {
        \"enabled\": true,
        \"server_name\": \"$sni\",
        \"utls\": {
          \"enabled\": true,
          \"fingerprint\": \"$fingerprint\"
        },
        \"reality\": {
          \"enabled\": true,
          \"public_key\": \"$public_key\",
          \"short_id\": \"$short_id\"
        }
      }
    },
"

    if [ -n "$inbound_rules" ]; then
        inbound_rules="$inbound_rules,
"
    fi
    inbound_rules="$inbound_rules      {
        \"inbound\": \"mixed-in-$server_link_count\",
        \"outbound\": \"proxy-$server_link_count\"
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

ensure_download_tools() {
    if command -v curl >/dev/null 2>&1 || command -v wget >/dev/null 2>&1; then
        return 0
    fi

    printf 'curl or wget is required to download sing-box. Trying to install curl and ca-certificates...\n'
    install_downloader_packages || true

    if command -v curl >/dev/null 2>&1 || command -v wget >/dev/null 2>&1; then
        return 0
    fi

    return 1
}

download_to_file() {
    url=$1
    target=$2

    if command -v curl >/dev/null 2>&1; then
        curl -fsSL \
            --connect-timeout "$download_connect_timeout_seconds" \
            --max-time "$download_package_timeout_seconds" \
            --retry 0 \
            --speed-limit "$download_min_speed_bytes" \
            --speed-time "$download_stall_timeout_seconds" \
            -o "$target" \
            "$url"
    elif command -v wget >/dev/null 2>&1; then
        if command -v timeout >/dev/null 2>&1; then
            timeout "$download_package_timeout_seconds" \
                wget \
                    --connect-timeout="$download_connect_timeout_seconds" \
                    --read-timeout="$download_stall_timeout_seconds" \
                    --timeout="$download_stall_timeout_seconds" \
                    --tries=1 \
                    -qO "$target" \
                    "$url"
        else
            wget \
                --connect-timeout="$download_connect_timeout_seconds" \
                --read-timeout="$download_stall_timeout_seconds" \
                --timeout="$download_stall_timeout_seconds" \
                --tries=1 \
                -qO "$target" \
                "$url"
        fi
    else
        return 1
    fi
}

download_to_stdout() {
    url=$1

    if command -v curl >/dev/null 2>&1; then
        curl -fsSL \
            --connect-timeout "$download_connect_timeout_seconds" \
            --max-time "$download_response_timeout_seconds" \
            --retry 0 \
            "$url"
    elif command -v wget >/dev/null 2>&1; then
        if command -v timeout >/dev/null 2>&1; then
            timeout "$download_response_timeout_seconds" \
                wget \
                    --connect-timeout="$download_connect_timeout_seconds" \
                    --read-timeout="$download_response_timeout_seconds" \
                    --timeout="$download_response_timeout_seconds" \
                    --tries=1 \
                    -qO- \
                    "$url"
        else
            wget \
                --connect-timeout="$download_connect_timeout_seconds" \
                --read-timeout="$download_response_timeout_seconds" \
                --timeout="$download_response_timeout_seconds" \
                --tries=1 \
                -qO- \
                "$url"
        fi
    else
        return 1
    fi
}

download_first_available() {
    target=$1
    shift

    for url in "$@"; do
        printf 'Trying sing-box download: %s\n' "$url"
        if download_to_file "$url" "$target"; then
            return 0
        fi
        warn "Download failed from $url"
    done

    return 1
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

detect_sing_box_package_target() {
    if command -v pacman >/dev/null 2>&1; then
        package_manager=pacman
        package_os=linux
        package_arch=$(uname -m)
        package_suffix=.pkg.tar.zst
    elif command -v dpkg >/dev/null 2>&1; then
        package_manager=dpkg
        package_os=linux
        package_arch=$(dpkg --print-architecture)
        package_suffix=.deb
    elif command -v dnf >/dev/null 2>&1; then
        package_manager=dnf
        package_os=linux
        package_arch=$(uname -m)
        package_suffix=.rpm
    elif command -v yum >/dev/null 2>&1; then
        package_manager=yum
        package_os=linux
        package_arch=$(uname -m)
        package_suffix=.rpm
    elif command -v zypper >/dev/null 2>&1; then
        package_manager=zypper
        package_os=linux
        package_arch=$(uname -m)
        package_suffix=.rpm
    elif command -v rpm >/dev/null 2>&1; then
        package_manager=rpm
        package_os=linux
        package_arch=$(uname -m)
        package_suffix=.rpm
    else
        return 1
    fi
}

install_package_file() {
    package_file=$1

    case "$package_manager" in
        pacman) pacman -U --noconfirm "$package_file" ;;
        dpkg) dpkg -i "$package_file" ;;
        dnf) dnf install -y "$package_file" ;;
        yum) yum install -y "$package_file" ;;
        zypper) zypper --non-interactive install "$package_file" ;;
        rpm) rpm -Uvh --replacepkgs "$package_file" ;;
        *) return 1 ;;
    esac
}

get_latest_sing_box_version() {
    for url in \
        https://api.github.com/repos/SagerNet/sing-box/releases/latest \
        https://ghfast.top/https://api.github.com/repos/SagerNet/sing-box/releases/latest \
        https://gh-proxy.com/https://api.github.com/repos/SagerNet/sing-box/releases/latest
    do
        printf 'Checking latest sing-box release: %s\n' "$url" >&2
        latest_release=$(download_to_stdout "$url" 2>/dev/null || true)
        tag=$(printf '%s\n' "$latest_release" | grep '"tag_name"' | head -n 1 | awk -F: '{print $2}' | sed 's/[", ]//g')
        version=${tag#v}
        case "$version" in
            ""|*[!0-9A-Za-z._-]*)
                warn "Could not read a valid sing-box release tag from $url"
                ;;
            *)
                printf '%s\n' "$version"
                return 0
                ;;
        esac
    done

    return 1
}

install_or_update_sing_box() {
    if [ "$UPDATE_SING_BOX" = "false" ]; then
        printf 'Skipping sing-box install/update because UPDATE_SING_BOX=false.\n'
        return 0
    fi

    if ! ensure_download_tools; then
        warn "curl or wget could not be installed, so sing-box cannot be downloaded."
        return 1
    fi

    if ! detect_sing_box_package_target; then
        warn "No supported package manager was found for installing sing-box."
        return 1
    fi

    version=$(get_latest_sing_box_version) ||
        return 1

    package_name="sing-box_${version}_${package_os}_${package_arch}${package_suffix}"
    package_url="https://github.com/SagerNet/sing-box/releases/download/v${version}/${package_name}"
    package_url_ghfast="https://ghfast.top/${package_url}"
    package_url_ghproxy="https://gh-proxy.com/github.com/SagerNet/sing-box/releases/download/v${version}/${package_name}"

    tmp_package_dir=$(mktemp -d)
    package_file="${tmp_package_dir}/${package_name}"

    download_first_available "$package_file" \
        "$package_url" \
        "$package_url_ghfast" \
        "$package_url_ghproxy" ||
        return 1

    printf 'Installing sing-box package with %s...\n' "$package_manager"
    install_package_file "$package_file"
}

printf 'Installing or updating sing-box to the latest available version...\n'
if ! install_or_update_sing_box; then
    if existing_sing_box=$(find_sing_box); then
        warn "Could not download or install the latest sing-box. Continuing with existing sing-box: $existing_sing_box"
    else
        fail "Could not download or install sing-box, and no existing sing-box binary was found. Check access to api.github.com and github.com, or install sing-box manually and rerun with UPDATE_SING_BOX=false."
    fi
fi

sing_box=$(find_sing_box) ||
    fail "sing-box is required, but the sing-box command could not be found."

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
${tun_inbound}${mixed_inbounds}
  ],
  "outbounds": [
${proxy_outbounds}    {
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
      },
${inbound_rules}
    ],
    "final": "proxy-1"
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
printf '%s' "$server_summary"
printf 'Allow external connections: %s\n' "$ALLOW_EXTERNAL"
printf 'TUN: %s\n' "$ENABLE_TUN"
printf 'Update sing-box: %s\n' "$UPDATE_SING_BOX"

if [ "$server_link_count" -gt 1 ]; then
    printf 'Traffic that does not come from a mixed inbound (for example TUN) uses server 1.\n'
fi

if [ "$ALLOW_EXTERNAL" = "true" ]; then
    printf '\n'
    printf 'The mixed inbounds listen on all interfaces with no authentication.\n'
    printf 'Restrict %s/tcp with a firewall so they are not left open to the public network.\n' "$listen_port_text"
else
    printf '\n'
    printf 'The mixed inbounds listen on 127.0.0.1 only and are not reachable from other hosts.\n'
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
