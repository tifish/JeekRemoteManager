set -eu

fail() {
    printf 'ERROR: %s\n' "$1" >&2
    exit 1
}

warn() {
    printf 'WARNING: %s\n' "$1" >&2
}

info() {
    printf '%s\n' "$1"
}

is_enabled() {
    enabled_value=$(printf '%s' "$1" | tr '[:upper:]' '[:lower:]')
    case "$enabled_value" in
        true|1|yes|y)
            return 0
            ;;
        false|0|no|n)
            return 1
            ;;
        *)
            fail "Invalid boolean value '$1'. Use true or false."
            ;;
    esac
}

if [ "$(uname -s)" != "Linux" ]; then
    fail "This optimization script only supports Linux."
fi

if [ "$(id -u)" != "0" ]; then
    fail "Root privileges are required. Connect as root or run this script with a root account."
fi

if ! command -v systemctl >/dev/null 2>&1 || [ ! -d /run/systemd/system ]; then
    fail "systemd is required. OpenWrt/OpenRC systems are not supported by this script."
fi

APT_UPDATED=0
ENABLE_FAIL2BAN=${ENABLE_FAIL2BAN:-true}
ENABLE_FIREWALL=${ENABLE_FIREWALL:-true}
ENABLE_AUTO_UPDATES=${ENABLE_AUTO_UPDATES:-true}
ENABLE_BBR=${ENABLE_BBR:-true}
ENABLE_APT_AUTOREMOVE=${ENABLE_APT_AUTOREMOVE:-false}
ENABLE_COMMAND_COLORS=${ENABLE_COMMAND_COLORS:-true}

detect_package_manager() {
    if command -v apt-get >/dev/null 2>&1; then
        printf 'apt\n'
    elif command -v dnf >/dev/null 2>&1; then
        printf 'dnf\n'
    elif command -v yum >/dev/null 2>&1; then
        printf 'yum\n'
    else
        printf 'none\n'
    fi
}

apt_update_once() {
    if [ "$APT_UPDATED" -eq 0 ]; then
        export DEBIAN_FRONTEND=noninteractive
        apt-get update
        APT_UPDATED=1
    fi
}

install_packages() {
    package_manager=$(detect_package_manager)
    case "$package_manager" in
        apt)
            export DEBIAN_FRONTEND=noninteractive
            apt_update_once
            apt-get install -y "$@"
            ;;
        dnf)
            dnf install -y "$@"
            ;;
        yum)
            yum install -y "$@"
            ;;
        *)
            fail "No supported package manager found to install: $*"
            ;;
    esac
}

find_sshd() {
    for candidate in sshd /usr/sbin/sshd /usr/local/sbin/sshd; do
        if command -v "$candidate" >/dev/null 2>&1; then
            command -v "$candidate"
            return 0
        fi

        if [ -x "$candidate" ]; then
            printf '%s\n' "$candidate"
            return 0
        fi
    done

    return 1
}

detect_ssh_port() {
    if sshd_bin=$(find_sshd); then
        detected_port=$("$sshd_bin" -T 2>/dev/null | awk '$1 == "port" {print $2; exit}' || true)
        case "$detected_port" in
            ''|*[!0-9]*)
                ;;
            *)
                if [ "$detected_port" -ge 1 ] && [ "$detected_port" -le 65535 ]; then
                    printf '%s\n' "$detected_port"
                    return 0
                fi
                ;;
        esac
    fi

    printf '22\n'
}

configure_ufw() {
    ssh_port=$1

    ufw allow "${ssh_port}/tcp"
    ufw allow 443/tcp

    if ufw status 2>/dev/null | grep -qi '^Status: active'; then
        ufw reload
    else
        ufw --force enable
    fi

    info "ufw is enabled; allowed ${ssh_port}/tcp and 443/tcp."
}

configure_firewalld() {
    ssh_port=$1

    firewall-cmd --permanent --add-port="${ssh_port}/tcp"
    firewall-cmd --permanent --add-port=443/tcp
    firewall-cmd --reload
    systemctl enable firewalld >/dev/null 2>&1 || true

    info "firewalld is enabled; allowed ${ssh_port}/tcp and 443/tcp."
}

configure_firewall() {
    ssh_port=$1

    if command -v firewall-cmd >/dev/null 2>&1 && systemctl is-active --quiet firewalld 2>/dev/null; then
        configure_firewalld "$ssh_port"
        return 0
    fi

    if command -v ufw >/dev/null 2>&1; then
        configure_ufw "$ssh_port"
        return 0
    fi

    if [ "$(detect_package_manager)" = "apt" ]; then
        install_packages ufw
        configure_ufw "$ssh_port"
        return 0
    fi

    fail "No active firewalld or installable ufw was found; firewall was not enabled."
}

fail2ban_has_sshd_jail() {
    fail2ban-client status 2>/dev/null |
        awk -F: '/Jail list/ {print $2}' |
        tr ',' ' ' |
        grep -Eq '(^|[[:space:]])sshd([[:space:]]|$)'
}

ensure_fail2ban() {
    ssh_port=$1

    if ! command -v fail2ban-client >/dev/null 2>&1; then
        install_packages fail2ban
    fi

    # Always write a managed jail so the ban port follows the detected SSH port,
    # overriding any distro default sshd jail (e.g. Debian's port = ssh).
    mkdir -p /etc/fail2ban/jail.d
    cat > /etc/fail2ban/jail.d/jeekremote-sshd.conf <<EOF
# Managed by JeekRemoteManager.
[sshd]
enabled = true
port = $ssh_port
maxretry = 5
findtime = 10m
bantime = 1h
EOF

    systemctl enable fail2ban >/dev/null 2>&1 || true
    systemctl restart fail2ban

    if ! systemctl is-active --quiet fail2ban; then
        fail "fail2ban service is not active after setup."
    fi

    if fail2ban_has_sshd_jail; then
        info "fail2ban is enabled; managed sshd jail uses port ${ssh_port}."
    else
        warn "fail2ban is enabled, but the sshd jail was not reported by fail2ban-client."
    fi
}

set_or_append_key_value() {
    config_file=$1
    config_key=$2
    config_value=$3

    if [ -f "$config_file" ] && grep -Eq "^[[:space:]]*${config_key}[[:space:]]*=" "$config_file"; then
        sed -i "s|^[[:space:]]*${config_key}[[:space:]]*=.*|${config_key} = ${config_value}|" "$config_file"
    else
        printf '%s = %s\n' "$config_key" "$config_value" >> "$config_file"
    fi
}

enable_bbr() {
    if ! command -v sysctl >/dev/null 2>&1; then
        fail "sysctl is not available on this server."
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

    bbr_config_file=/etc/sysctl.d/99-jeekremote-bbr.conf
    mkdir -p /etc/sysctl.d
    cat > "$bbr_config_file" <<'EOF'
# Managed by JeekRemoteManager.
net.core.default_qdisc = fq
net.ipv4.tcp_congestion_control = bbr
EOF
    chmod 0644 "$bbr_config_file"

    sysctl -w net.core.default_qdisc=fq
    sysctl -w net.ipv4.tcp_congestion_control=bbr

    current_qdisc=$(sysctl -n net.core.default_qdisc 2>/dev/null || printf 'unknown')
    current_cc=$(sysctl -n net.ipv4.tcp_congestion_control 2>/dev/null || printf 'unknown')

    info "BBR configuration written to ${bbr_config_file}."
    info "net.core.default_qdisc=${current_qdisc}"
    info "net.ipv4.tcp_congestion_control=${current_cc}"

    if [ "$current_cc" != "bbr" ]; then
        fail "BBR was configured but is not active."
    fi

    info "BBR is enabled."
}

enable_apt_auto_updates() {
    install_packages unattended-upgrades

    mkdir -p /etc/apt/apt.conf.d
    cat > /etc/apt/apt.conf.d/20auto-upgrades <<'EOF'
APT::Periodic::Update-Package-Lists "1";
APT::Periodic::Unattended-Upgrade "1";
EOF

    systemctl enable --now unattended-upgrades >/dev/null 2>&1 || true
    systemctl enable --now apt-daily.timer >/dev/null 2>&1 || true
    systemctl enable --now apt-daily-upgrade.timer >/dev/null 2>&1 || true

    info "Automatic security updates are enabled with unattended-upgrades."
}

enable_dnf_auto_updates() {
    install_packages dnf-automatic

    if [ -f /etc/dnf/automatic.conf ]; then
        set_or_append_key_value /etc/dnf/automatic.conf upgrade_type security
        set_or_append_key_value /etc/dnf/automatic.conf download_updates yes
        set_or_append_key_value /etc/dnf/automatic.conf apply_updates yes
    fi

    systemctl enable --now dnf-automatic.timer

    info "Automatic security updates are enabled with dnf-automatic."
}

enable_yum_auto_updates() {
    install_packages yum-cron

    if [ -f /etc/yum/yum-cron.conf ]; then
        set_or_append_key_value /etc/yum/yum-cron.conf update_cmd security
        set_or_append_key_value /etc/yum/yum-cron.conf apply_updates yes
    fi

    systemctl enable --now yum-cron

    info "Automatic security updates are enabled with yum-cron."
}

enable_auto_updates() {
    package_manager=$(detect_package_manager)
    case "$package_manager" in
        apt)
            enable_apt_auto_updates
            ;;
        dnf)
            enable_dnf_auto_updates
            ;;
        yum)
            enable_yum_auto_updates
            ;;
        *)
            warn "Unsupported package manager; automatic updates were not configured."
            ;;
    esac
}

run_apt_autoremove() {
    if [ "$(detect_package_manager)" != "apt" ]; then
        warn "apt autoremove is only supported on apt-based systems; skipping."
        return 0
    fi

    install_packages unattended-upgrades

    mkdir -p /etc/apt/apt.conf.d
    cat > /etc/apt/apt.conf.d/52unattended-upgrades-jeekremote-autoremove <<'EOF'
// Managed by JeekRemoteManager.
Unattended-Upgrade::Remove-Unused-Dependencies "true";
Unattended-Upgrade::Remove-New-Unused-Dependencies "true";
EOF

    info "unattended-upgrades is configured to remove unused dependencies."

    if [ -e /var/run/reboot-required ] || [ -e /run/reboot-required ]; then
        warn "A reboot is required; skipping immediate apt autoremove to keep rollback kernels available."
        return 0
    fi

    apt-get autoremove -y
    info "apt autoremove completed."
}

enable_command_colors() {
    mkdir -p /etc/profile.d
    cat > /etc/profile.d/jeekremote-command-colors.sh <<'EOF'
# Managed by JeekRemoteManager.

case "$-" in
    *i*)
        ;;
    *)
        return 0 2>/dev/null || exit 0
        ;;
esac

if [ ! -t 1 ]; then
    return 0 2>/dev/null || exit 0
fi

if command -v dircolors >/dev/null 2>&1; then
    eval "$(dircolors -b 2>/dev/null)" || true
fi

if command -v ls >/dev/null 2>&1 && ls --color=auto -d . >/dev/null 2>&1; then
    alias ls='ls --color=auto'
    alias ll='ls -alF --color=auto'
    alias la='ls -A --color=auto'
    alias l='ls -CF --color=auto'
fi

if command -v grep >/dev/null 2>&1 && { grep --color=auto -q '' /dev/null >/dev/null 2>&1 || [ "$?" -eq 1 ]; }; then
    alias grep='grep --color=auto'
fi

if [ "${LESS+x}" != x ]; then
    export LESS='-R'
fi

if [ "$(id -u 2>/dev/null || printf '1')" = "0" ]; then
    prompt_user_color='31'
else
    prompt_user_color='32'
fi

if [ -n "${BASH_VERSION:-}" ]; then
    PS1='\[\033[1;'"${prompt_user_color}"'m\]\u\[\033[0m\]@\[\033[1;36m\]\h\[\033[0m\]:\[\033[1;34m\]\w\[\033[0m\] \[\033[1;33m\]\$ \[\033[0m\]'
else
    prompt_escape=$(printf '\033')
    prompt_user=$(id -un 2>/dev/null || printf 'user')
    prompt_host=$(hostname -s 2>/dev/null || hostname 2>/dev/null || printf 'host')
    PS1="${prompt_escape}[1;${prompt_user_color}m${prompt_user}${prompt_escape}[0m@${prompt_escape}[1;36m${prompt_host}${prompt_escape}[0m:${prompt_escape}[1;34m"'$PWD'"${prompt_escape}[0m ${prompt_escape}[1;33m\\$ ${prompt_escape}[0m"
fi
export PS1
EOF
    chmod 0644 /etc/profile.d/jeekremote-command-colors.sh
    info "Command color profile is installed at /etc/profile.d/jeekremote-command-colors.sh."
}

ssh_port=$(detect_ssh_port)
info "Detected SSH port: ${ssh_port}"

if is_enabled "$ENABLE_FIREWALL"; then
    configure_firewall "$ssh_port"
else
    info "Firewall setup skipped by ENABLE_FIREWALL=false."
fi

if is_enabled "$ENABLE_FAIL2BAN"; then
    ensure_fail2ban "$ssh_port"
else
    info "fail2ban setup skipped by ENABLE_FAIL2BAN=false."
fi

if is_enabled "$ENABLE_AUTO_UPDATES"; then
    enable_auto_updates
else
    info "Automatic updates setup skipped by ENABLE_AUTO_UPDATES=false."
fi

if is_enabled "$ENABLE_BBR"; then
    enable_bbr
else
    info "BBR setup skipped by ENABLE_BBR=false."
fi

if is_enabled "$ENABLE_APT_AUTOREMOVE"; then
    run_apt_autoremove
else
    info "apt autoremove skipped by ENABLE_APT_AUTOREMOVE=false."
fi

if is_enabled "$ENABLE_COMMAND_COLORS"; then
    enable_command_colors
else
    info "Command color setup skipped by ENABLE_COMMAND_COLORS=false."
fi

info "Basic server optimization setup completed."
