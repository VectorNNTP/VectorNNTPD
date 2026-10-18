#!/bin/bash
set -e

echo "==> Disabling unattended-upgrades and auto updates..."
systemctl stop unattended-upgrades || true
systemctl disable unattended-upgrades || true
systemctl mask unattended-upgrades || true
apt-get purge -y unattended-upgrades || true

systemctl stop apt-daily.timer apt-daily-upgrade.timer || true
systemctl disable apt-daily.timer apt-daily-upgrade.timer || true
systemctl mask apt-daily.timer apt-daily-upgrade.timer || true
systemctl stop apt-daily.service apt-daily-upgrade.service || true
systemctl mask apt-daily.service apt-daily-upgrade.service || true

CONF="/etc/apt/apt.conf.d/50unattended-upgrades"
if [ -f "$CONF" ]; then
    sed -i 's@^//Unattended-Upgrade::Automatic-Reboot.*@Unattended-Upgrade::Automatic-Reboot "false";@' "$CONF"
fi
rm -f /var/run/reboot-required* || true

echo "==> Disabling service auto-restarts..."
DEBIAN_FRONTEND=noninteractive apt-get install -y needrestart || true
sed -i 's/^\$nrconf{restart}.*/$nrconf{restart} = "l";/' /etc/needrestart/needrestart.conf || true

cat << 'EOF' > /usr/sbin/policy-rc.d
#!/bin/sh
exit 101
EOF
chmod +x /usr/sbin/policy-rc.d

mkdir -p /etc/apt/apt.conf.d/
echo 'DPkg::Post-Invoke {"exit 0";};' > /etc/apt/apt.conf.d/99-no-service-restart


echo "==> Applying kernel performance tuning for FreeRADIUS + FRR..."
cat << 'EOF' > /etc/sysctl.d/99-performance.conf
# General performance
vm.swappiness = 1
vm.dirty_ratio = 10
vm.dirty_background_ratio = 5
vm.max_map_count = 262144

# File handles
fs.file-max = 2097152

# Networking performance (high throughput + BGP + RADIUS)
net.core.rmem_max = 134217728
net.core.wmem_max = 134217728
net.core.rmem_default = 262144
net.core.wmem_default = 262144
net.core.optmem_max = 25165824
net.core.netdev_max_backlog = 250000

# Increase socket listen queues
net.ipv4.tcp_max_syn_backlog = 262144
net.core.somaxconn = 65535

# Enable fast recycling of TIME_WAIT sockets
net.ipv4.tcp_tw_reuse = 1

# Disable slow start after idle
net.ipv4.tcp_slow_start_after_idle = 0

# BGP/FRR tuning (many sessions, high PPS)
net.ipv4.ip_local_port_range = 1024 65000
net.ipv4.tcp_fin_timeout = 10
net.ipv4.tcp_keepalive_time = 600
net.ipv4.tcp_keepalive_intvl = 15
net.ipv4.tcp_keepalive_probes = 5

# Jumbo frames support (NIC must be configured separately)
net.core.netdev_max_backlog = 250000

# Increase conntrack table size (if using conntrack/NAT — optional)
net.netfilter.nf_conntrack_max = 2097152
EOF

sysctl --system


echo "==> Increasing systemd and process limits..."

# PAM / security limits
cat << 'EOF' > /etc/security/limits.d/99-performance.conf
* soft nofile 1048576
* hard nofile 1048576
* soft nproc  1048576
* hard nproc  1048576
EOF

# Ensure systemd override directories exist
mkdir -p /etc/systemd/system.conf.d
mkdir -p /etc/systemd/user.conf.d

cat << 'EOF' > /etc/systemd/system.conf.d/99-performance.conf
[Manager]
DefaultLimitNOFILE=1048576
DefaultLimitNPROC=1048576
DefaultTasksMax=1000000
EOF

cat << 'EOF' > /etc/systemd/user.conf.d/99-performance.conf
[Manager]
DefaultLimitNOFILE=1048576
DefaultLimitNPROC=1048576
DefaultTasksMax=1000000
EOF

systemctl daemon-reexec


echo "==> Applying NIC optimizations and making them persistent..."

# Install ethtool if missing
apt-get install -y ethtool || true

# Create a script that will be executed at boot
NIC_SCRIPT="/usr/local/sbin/nic-tune.sh"
cat << 'EOF' > $NIC_SCRIPT
#!/bin/bash
for IFACE in $(ls /sys/class/net | grep -E 'eth|ens|eno'); do
    echo "Tuning $IFACE"
    ethtool -G $IFACE rx 4096 tx 4096 || true
    ethtool -K $IFACE gro off lro off tso off gso off || true
done
EOF
chmod +x $NIC_SCRIPT

# Create a systemd service
SERVICE_FILE="/etc/systemd/system/nic-tune.service"
cat << EOF > $SERVICE_FILE
[Unit]
Description=NIC tuning for high throughput
After=network.target

[Service]
Type=oneshot
ExecStart=$NIC_SCRIPT
RemainAfterExit=yes

[Install]
WantedBy=multi-user.target
EOF

# Enable and start the service
systemctl daemon-reload
systemctl enable nic-tune.service
systemctl start nic-tune.service


echo "==> Disabling CPU frequency scaling (set to performance mode)..."
apt-get install -y cpufrequtils || true
cat << 'EOF' > /etc/default/cpufrequtils
GOVERNOR="performance"
EOF
systemctl disable ondemand || true
systemctl stop ondemand || true
systemctl enable cpufrequtils || true
systemctl start cpufrequtils || true


echo "==> Server setup complete. All tuning is persistent across reboots."
