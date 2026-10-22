namespace VectorNNTPD.StorageService.Storage.SegmentPools
{

    /// <summary>
    /// Production <see cref="ILinuxSysfs"/> that reads <c>/sys/dev/block</c>.
    /// </summary>
    internal sealed class LinuxSysfsReader : ILinuxSysfs
    {
        private readonly string _root;

        /// <summary>Reads <c>/sys</c>.</summary>
        public LinuxSysfsReader()
            : this("/sys")
        {
        }

        /// <summary>Reads a sysfs root. Tests pass a fixture tree; production uses <c>/sys</c>.</summary>
        /// <param name="sysfsRoot">Directory that contains <c>dev/block</c>.</param>
        internal LinuxSysfsReader(string sysfsRoot)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sysfsRoot);
            _root = sysfsRoot;
        }

        /// <inheritdoc />
        public bool Exists(string majorMinor) => Directory.Exists(Node(majorMinor));

        /// <inheritdoc />
        public bool HasPartition(string majorMinor) =>
            File.Exists(StorageRelativePath.CombineUnder(Node(majorMinor), "partition"));

        /// <inheritdoc />
        public string? ParentMajorMinor(string majorMinor)
        {
            var node = new DirectoryInfo(Node(majorMinor));
            if (!node.Exists)
            {
                return null;
            }

            var final = node.ResolveLinkTarget(returnFinalTarget: true) as DirectoryInfo ?? node;
            var parent = final.Parent;
            if (parent is null)
            {
                return null;
            }

            var dev = StorageRelativePath.CombineUnder(parent.FullName, "dev");
            if (!File.Exists(dev))
            {
                return null;
            }

            var text = File.ReadAllText(dev).Trim();
            return text.Length == 0 ? null : text;
        }

        /// <inheritdoc />
        public IReadOnlyList<string> SlaveMajorMinors(string majorMinor)
        {
            var slaves = StorageRelativePath.CombineUnder(Node(majorMinor), "slaves");
            if (!Directory.Exists(slaves))
            {
                return [];
            }

            var results = new List<string>();
            foreach (var slave in Directory.GetDirectories(slaves))
            {
                var dev = StorageRelativePath.CombineUnder(slave, "dev");
                if (!File.Exists(dev))
                {
                    continue;
                }

                var text = File.ReadAllText(dev).Trim();
                if (text.Length > 0)
                {
                    results.Add(text);
                }
            }

            return results;
        }

        /// <inheritdoc />
        public string DeviceName(string majorMinor)
        {
            var node = new DirectoryInfo(Node(majorMinor));
            if (!node.Exists)
            {
                return string.Empty;
            }

            var final = node.ResolveLinkTarget(returnFinalTarget: true) as DirectoryInfo ?? node;
            return final.Name;
        }

        /// <summary>
        /// Resolves <c>{sysfs}/dev/block/{major}:{minor}</c> after validating the device id.
        /// </summary>
        /// <param name="majorMinor">Kernel device id such as <c>8:0</c>.</param>
        /// <returns>Absolute node path under the configured sysfs root.</returns>
        private string Node(string majorMinor)
        {
            RequireMajorMinor(majorMinor);
            // major:minor is a single relative segment; ':' is illegal as a Win32 file name, so use the
            // checked-segment join after RequireMajorMinor has constrained the alphabet.
            var blockRoot = StorageRelativePath.CombineUnder(_root, "dev", "block");
            return StorageRelativePath.CombineUnderCheckedSegment(blockRoot, majorMinor.Trim());
        }

        /// <summary>
        /// Accepts only <c>major:minor</c> where both sides are non-empty decimal digit runs.
        /// Rejects separators and traversal so the id cannot escape <c>/sys/dev/block</c>.
        /// </summary>
        private static void RequireMajorMinor(string majorMinor)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(majorMinor);
            var trimmed = majorMinor.Trim();
            var colon = trimmed.IndexOf(':');
            if (colon <= 0 || colon != trimmed.LastIndexOf(':') || colon == trimmed.Length - 1)
            {
                throw new ArgumentException(
                    "Linux block device id must be 'major:minor' with decimal digits.",
                    nameof(majorMinor));
            }

            for (var index = 0; index < trimmed.Length; index++)
            {
                if (index == colon)
                {
                    continue;
                }

                if (!char.IsAsciiDigit(trimmed[index]))
                {
                    throw new ArgumentException(
                        "Linux block device id must be 'major:minor' with decimal digits.",
                        nameof(majorMinor));
                }
            }
        }
    }
}
