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
        public bool HasPartition(string majorMinor) => File.Exists(Path.Combine(Node(majorMinor), "partition"));

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

            var dev = Path.Combine(parent.FullName, "dev");
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
            var slaves = Path.Combine(Node(majorMinor), "slaves");
            if (!Directory.Exists(slaves))
            {
                return [];
            }

            var results = new List<string>();
            foreach (var slave in Directory.GetDirectories(slaves))
            {
                var dev = Path.Combine(slave, "dev");
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

        private string Node(string majorMinor) => Path.Combine(_root, "dev", "block", majorMinor);
    }
}
