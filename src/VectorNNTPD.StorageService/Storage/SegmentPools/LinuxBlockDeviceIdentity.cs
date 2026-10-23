namespace VectorNNTPD.StorageService.Storage.SegmentPools
{

    /// <summary>
    /// Walks Linux device-mapper and partition relationships to physical disk names.
    /// </summary>
    internal static class LinuxBlockDeviceIdentity
    {
        /// <summary>Resolved physical devices, or a failure when identity cannot be established.</summary>
        /// <param name="DeviceIds">Canonical ids such as <c>block:sda</c>.</param>
        /// <param name="Failure">Actionable diagnostic, or <see langword="null"/> on success.</param>
        internal readonly record struct PhysicalIdentity(string[] DeviceIds, string? Failure);

        /// <summary>
        /// Resolves <paramref name="majorMinor"/> to one or more physical disks.
        /// Partitions collapse to the parent disk. Device-mapper and md nodes collapse to their slaves.
        /// Virtual devices without resolvable slaves fail closed.
        /// </summary>
        /// <param name="majorMinor">Kernel device number from mountinfo.</param>
        /// <param name="sysfs">Sysfs reader.</param>
        /// <returns>Physical device ids, or a failure.</returns>
        internal static PhysicalIdentity Resolve(string majorMinor, ILinuxSysfs sysfs)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(majorMinor);
            ArgumentNullException.ThrowIfNull(sysfs);
            var devices = new SortedSet<string>(StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var failure = Walk(majorMinor, sysfs, seen, devices, depth: 0);
            if (failure is not null)
            {
                return new PhysicalIdentity([], failure);
            }

            if (devices.Count == 0)
            {
                return new PhysicalIdentity([], "physical device identity could not be established.");
            }

            return new PhysicalIdentity(devices.ToArray(), null);
        }

        private static string? Walk(
            string majorMinor,
            ILinuxSysfs sysfs,
            HashSet<string> seen,
            SortedSet<string> devices,
            int depth)
        {
            if (depth > 8)
            {
                return "physical device identity could not be established: device stack exceeded 8 levels.";
            }

            if (!seen.Add(majorMinor))
            {
                return "physical device identity could not be established: device stack contains a cycle.";
            }

            if (!sysfs.Exists(majorMinor))
            {
                return $"physical device identity could not be established: sysfs has no block device {majorMinor}.";
            }

            var slaves = sysfs.SlaveMajorMinors(majorMinor);
            if (slaves.Count > 0)
            {
                // Lazy Select + FirstOrDefault preserves Walk order and stops at the first failure.
                return slaves
                    .Select(slave => Walk(slave, sysfs, seen, devices, depth + 1))
                    .FirstOrDefault(static failure => failure is not null);
            }

            if (sysfs.HasPartition(majorMinor))
            {
                var parent = sysfs.ParentMajorMinor(majorMinor);
                if (string.IsNullOrWhiteSpace(parent))
                {
                    return $"physical device identity could not be established: partition {majorMinor} has no parent disk.";
                }

                return Walk(parent, sysfs, seen, devices, depth + 1);
            }

            var name = sysfs.DeviceName(majorMinor);
            if (name.Length == 0)
            {
                return $"physical device identity could not be established: block device {majorMinor} has no name.";
            }

            if (!IsAcceptedPhysicalName(name))
            {
                return "physical device identity could not be established: '"
                    + name
                    + "' is layered, virtual, or not a recognized physical disk. "
                    + "RAID, multipath, loop, device-mapper, and virtio disks are accepted only when their underlying physical disks can be resolved.";
            }

            devices.Add("block:" + PhysicalIdentityName(name));
            return null;
        }

        /// <summary>
        /// Collapses an NVMe namespace to its controller. <c>nvme0n1</c> and <c>nvme0n2</c> are one physical device.
        /// </summary>
        /// <param name="name">Kernel device name that has already passed <see cref="IsAcceptedPhysicalName"/>.</param>
        /// <returns>Controller name for NVMe, otherwise <paramref name="name"/>.</returns>
        internal static string PhysicalIdentityName(string name)
        {
            if (TryNvmeController(name, out var controller))
            {
                return controller;
            }

            return name;
        }

        /// <summary>Reads the <c>nvmeN</c> controller from an <c>nvmeNnM</c> namespace name.</summary>
        /// <param name="name">Kernel device name.</param>
        /// <param name="controller">Controller name when the method returns <see langword="true"/>.</param>
        /// <returns><see langword="true"/> for a whole NVMe namespace name.</returns>
        internal static bool TryNvmeController(string name, out string controller)
        {
            controller = string.Empty;
            if (!IsNvmeDisk(name))
            {
                return false;
            }

            var separator = name.IndexOf('n', 4);
            controller = name[..separator];
            return true;
        }

        /// <summary>
        /// SCSI, NVMe, ATA, and MMC disk names are treated as physical when they have no slaves.
        /// Partition suffixes are rejected here because partitions must be collapsed first.
        /// </summary>
        /// <param name="name">Kernel device name.</param>
        /// <returns><see langword="true"/> for a whole-disk name this host can treat as physical.</returns>
        internal static bool IsAcceptedPhysicalName(string name)
        {
            if (name.StartsWith("loop", StringComparison.Ordinal)
                || name.StartsWith("dm-", StringComparison.Ordinal)
                || name.StartsWith("md", StringComparison.Ordinal)
                || name.StartsWith("nbd", StringComparison.Ordinal)
                || name.StartsWith("rbd", StringComparison.Ordinal)
                || name.StartsWith("zram", StringComparison.Ordinal)
                || name.StartsWith("vd", StringComparison.Ordinal)
                || name.StartsWith("xvd", StringComparison.Ordinal))
            {
                return false;
            }

            if (IsScsiDisk(name) || IsNvmeDisk(name) || IsAtaDisk(name) || IsMmcDisk(name))
            {
                return true;
            }

            return false;
        }

        private static bool IsScsiDisk(string name)
        {
            if (name.Length < 3 || !name.StartsWith("sd", StringComparison.Ordinal))
            {
                return false;
            }

            for (var i = 2; i < name.Length; i++)
            {
                if (!char.IsAsciiLetter(name[i]))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsNvmeDisk(string name)
        {
            // nvme<controller>n<namespace>, without a partition suffix.
            if (!name.StartsWith("nvme", StringComparison.Ordinal))
            {
                return false;
            }

            var n = name.IndexOf('n', 4);
            if (n <= 4 || n == name.Length - 1)
            {
                return false;
            }

            for (var i = 4; i < n; i++)
            {
                if (!char.IsAsciiDigit(name[i]))
                {
                    return false;
                }
            }

            for (var i = n + 1; i < name.Length; i++)
            {
                if (!char.IsAsciiDigit(name[i]))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsAtaDisk(string name)
        {
            if (name.Length < 3 || !name.StartsWith("hd", StringComparison.Ordinal))
            {
                return false;
            }

            for (var i = 2; i < name.Length; i++)
            {
                if (!char.IsAsciiLetter(name[i]))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsMmcDisk(string name)
        {
            // mmcblk<N> without a partition suffix p<N>.
            if (!name.StartsWith("mmcblk", StringComparison.Ordinal) || name.Contains('p', StringComparison.Ordinal))
            {
                return false;
            }

            for (var i = 6; i < name.Length; i++)
            {
                if (!char.IsAsciiDigit(name[i]))
                {
                    return false;
                }
            }

            return name.Length > 6;
        }
    }
}
