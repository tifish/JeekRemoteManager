set -eu

link_info_file=/etc/sing-box/jeekremote-reality-link.conf

fail() {
    printf 'ERROR: %s\n' "$1" >&2
    exit 1
}

warn() {
    printf 'WARNING: %s\n' "$1" >&2
}

if [ "$(uname -s)" != "Linux" ]; then
    fail "This script only supports Linux."
fi

if [ "$(id -u)" != "0" ]; then
    fail "Root privileges are required. Connect as root or run this script with a root account."
fi

if [ ! -r "$link_info_file" ]; then
    fail "Link metadata was not found. Run install.sh once to create or refresh the sing-box reality link."
fi

read_link_value() {
    key=$1
    awk -F= -v key="$key" '
        $1 == key {
            print substr($0, index($0, "=") + 1)
            found = 1
            exit
        }
        END {
            if (!found)
                exit 1
        }
    ' "$link_info_file"
}

read_required_link_value() {
    key=$1
    value=$(read_link_value "$key" || true)
    if [ -z "$value" ]; then
        fail "Link metadata is missing $key. Run install.sh once to refresh the sing-box reality link."
    fi
    printf '%s\n' "$value"
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

    return 1
}

stored_server_address=$(read_required_link_value SERVER_ADDRESS)
port=$(read_required_link_value PORT)
sni=$(read_required_link_value SNI)
uuid=$(read_required_link_value UUID)
public_key=$(read_required_link_value PUBLIC_KEY)
short_id=$(read_required_link_value SHORT_ID)

server_address=$(detect_public_address || true)
if [ -z "$server_address" ]; then
    server_address=$stored_server_address
    warn "Public address detection failed. Using the address saved during install."
elif [ "$stored_server_address" != "$server_address" ]; then
    warn "Current public address differs from the address saved during install: $stored_server_address"
fi

case "$server_address" in
    *:*)
        uri_host="[$server_address]"
        ;;
    *)
        uri_host="$server_address"
        ;;
esac

vless_uri="vless://${uuid}@${uri_host}:${port}?encryption=none&flow=xtls-rprx-vision&security=reality&sni=${sni}&fp=chrome&pbk=${public_key}&sid=${short_id}&type=tcp#sing-box-reality"

printf 'sing-box reality client link:\n'
printf '%s\n' "$vless_uri"
printf '\n'
printf 'Server address: %s\n' "$server_address"
printf 'Port: %s\n' "$port"
printf 'SNI: %s\n' "$sni"
printf 'UUID: %s\n' "$uuid"
printf 'Public key: %s\n' "$public_key"
printf 'Short ID: %s\n' "$short_id"

if [ "$server_address" = "YOUR_SERVER_ADDRESS" ]; then
    warn "Replace YOUR_SERVER_ADDRESS in the URI manually."
fi
