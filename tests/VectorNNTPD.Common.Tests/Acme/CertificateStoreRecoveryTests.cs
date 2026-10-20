using VectorNNTPD.Common.Acme;
using VectorNNTPD.Common.Tests.TestDoubles;

namespace VectorNNTPD.Common.Tests.Acme
{
    /// <summary>
    /// Conservative recovery regressions for <see cref="CertificateStore"/> (issue #183).
    /// </summary>
    public sealed class CertificateStoreRecoveryTests
    {
        private const string Fqdn = "backfiller01.usenet.ninja";

        /// <summary>
        /// Asserts that an I/O failure while reading <c>current</c> leaves the pointer and generations untouched.
        /// </summary>
        [Fact]
        public void Recover_UnreadableCurrentPointer_DoesNotDeletePointerOrGenerations()
        {
            using var dir = new TempStateDir();
            IReadOnlyList<string> names = CertificateIdentities.ForFqdn(Fqdn, includeNewsHostname: false);
            var store = new CertificateStore(dir.Path, Fqdn, TestCertificateFactory.Password, names, TimeSpan.FromDays(30));
            store.Save(TestCertificateFactory.CreateMaterial(names, DateTimeOffset.UtcNow.AddDays(60)));
            string generationId = File.ReadAllText(AcmePaths.CurrentGenerationPointerPath(dir.Path, Fqdn)).Trim();
            string genDir = AcmePaths.GenerationDir(dir.Path, Fqdn, generationId);
            string pointerPath = AcmePaths.CurrentGenerationPointerPath(dir.Path, Fqdn);
            byte[] pfxBefore = File.ReadAllBytes(AcmePaths.GenerationCertificatePaths(dir.Path, Fqdn, generationId).PfxPath);

            store.FileAccessForTests = new FaultingFileAccess(
                RealCertificateStoreFileAccess.Instance,
                readAllText: path =>
                {
                    if (string.Equals(path, pointerPath, StringComparison.Ordinal))
                    {
                        throw new IOException("simulated sharing violation");
                    }

                    return File.ReadAllText(path);
                });

            var ex = Assert.Throws<AcmeStorageException>(() => store.Recover());
            Assert.Equal("current_pointer_unreadable", ex.Category);

            Assert.True(File.Exists(pointerPath));
            Assert.Equal(generationId, File.ReadAllText(pointerPath).Trim());
            Assert.True(Directory.Exists(genDir));
            Assert.True(pfxBefore.AsSpan().SequenceEqual(
                File.ReadAllBytes(AcmePaths.GenerationCertificatePaths(dir.Path, Fqdn, generationId).PfxPath)));
        }

        /// <summary>
        /// Asserts that a transient pointer-read failure does not GC a valid PFX generation.
        /// </summary>
        [Fact]
        public void Recover_TransientPointerReadFailure_PreservesValidPfx()
        {
            using var dir = new TempStateDir();
            IReadOnlyList<string> names = CertificateIdentities.ForFqdn(Fqdn, includeNewsHostname: false);
            var store = new CertificateStore(dir.Path, Fqdn, TestCertificateFactory.Password, names, TimeSpan.FromDays(30));
            store.Save(TestCertificateFactory.CreateMaterial(names, DateTimeOffset.UtcNow.AddDays(60)));
            string generationId = File.ReadAllText(AcmePaths.CurrentGenerationPointerPath(dir.Path, Fqdn)).Trim();
            string pointerPath = AcmePaths.CurrentGenerationPointerPath(dir.Path, Fqdn);
            int failuresRemaining = 1;

            store.FileAccessForTests = new FaultingFileAccess(
                RealCertificateStoreFileAccess.Instance,
                readAllText: path =>
                {
                    if (string.Equals(path, pointerPath, StringComparison.Ordinal)
                        && Interlocked.Exchange(ref failuresRemaining, 0) == 1)
                    {
                        throw new IOException("transient");
                    }

                    return File.ReadAllText(path);
                });

            Assert.Throws<AcmeStorageException>(() => store.Recover());
            Assert.True(Directory.Exists(AcmePaths.GenerationDir(dir.Path, Fqdn, generationId)));

            store.FileAccessForTests = null;
            store.Recover();
            Assert.NotNull(store.Load());
            Assert.Equal(generationId, store.CurrentGenerationId());
        }

        /// <summary>
        /// Asserts that a malformed pointer deletes only the pointer and leaves complete generations on disk.
        /// </summary>
        [Fact]
        public void Recover_MalformedPointer_DoesNotDeleteGenerations()
        {
            using var dir = new TempStateDir();
            IReadOnlyList<string> names = CertificateIdentities.ForFqdn(Fqdn, includeNewsHostname: false);
            var store = new CertificateStore(dir.Path, Fqdn, TestCertificateFactory.Password, names, TimeSpan.FromDays(30));
            store.Save(TestCertificateFactory.CreateMaterial(names, DateTimeOffset.UtcNow.AddDays(60)));
            string generationId = File.ReadAllText(AcmePaths.CurrentGenerationPointerPath(dir.Path, Fqdn)).Trim();
            string genDir = AcmePaths.GenerationDir(dir.Path, Fqdn, generationId);
            string pointerPath = AcmePaths.CurrentGenerationPointerPath(dir.Path, Fqdn);

            File.WriteAllText(pointerPath, "not-a-generation-id\n");
            store.Recover();

            Assert.False(File.Exists(pointerPath));
            Assert.True(Directory.Exists(genDir));
            Assert.True(File.Exists(AcmePaths.GenerationCertificatePaths(dir.Path, Fqdn, generationId).PfxPath));
            Assert.Null(store.Load());
        }

        /// <summary>
        /// Asserts that a PFX length inspection failure does not classify the generation as incomplete or delete it.
        /// </summary>
        [Fact]
        public void Recover_GenerationLengthInspectionFailure_DoesNotDeleteGeneration()
        {
            using var dir = new TempStateDir();
            IReadOnlyList<string> names = CertificateIdentities.ForFqdn(Fqdn, includeNewsHostname: false);
            var store = new CertificateStore(dir.Path, Fqdn, TestCertificateFactory.Password, names, TimeSpan.FromDays(30));
            store.Save(TestCertificateFactory.CreateMaterial(names, DateTimeOffset.UtcNow.AddDays(60)));
            string generationId = File.ReadAllText(AcmePaths.CurrentGenerationPointerPath(dir.Path, Fqdn)).Trim();
            string pfxPath = AcmePaths.GenerationCertificatePaths(dir.Path, Fqdn, generationId).PfxPath;
            string genDir = AcmePaths.GenerationDir(dir.Path, Fqdn, generationId);
            string pointerPath = AcmePaths.CurrentGenerationPointerPath(dir.Path, Fqdn);

            store.FileAccessForTests = new FaultingFileAccess(
                RealCertificateStoreFileAccess.Instance,
                getFileLength: path =>
                {
                    if (string.Equals(path, pfxPath, StringComparison.Ordinal))
                    {
                        throw new IOException("length unavailable");
                    }

                    return new FileInfo(path).Length;
                });

            var ex = Assert.Throws<AcmeStorageException>(() => store.Recover());
            Assert.Equal("generation_inspection_failed", ex.Category);
            Assert.True(File.Exists(pointerPath));
            Assert.True(Directory.Exists(genDir));
        }

        /// <summary>
        /// Asserts that a missing pointer leaves a complete orphan generation intact.
        /// </summary>
        [Fact]
        public void Recover_MissingPointer_DoesNotDestroyCompleteGeneration()
        {
            using var dir = new TempStateDir();
            IReadOnlyList<string> names = CertificateIdentities.ForFqdn(Fqdn, includeNewsHostname: false);
            var store = new CertificateStore(dir.Path, Fqdn, TestCertificateFactory.Password, names, TimeSpan.FromDays(30));
            store.Save(TestCertificateFactory.CreateMaterial(names, DateTimeOffset.UtcNow.AddDays(60)));
            string generationId = File.ReadAllText(AcmePaths.CurrentGenerationPointerPath(dir.Path, Fqdn)).Trim();
            string genDir = AcmePaths.GenerationDir(dir.Path, Fqdn, generationId);
            AtomicFile.TryDelete(AcmePaths.CurrentGenerationPointerPath(dir.Path, Fqdn));

            store.Recover();

            Assert.False(File.Exists(AcmePaths.CurrentGenerationPointerPath(dir.Path, Fqdn)));
            Assert.True(Directory.Exists(genDir));
            Assert.True(File.Exists(AcmePaths.GenerationCompleteMarker(dir.Path, Fqdn, generationId)));
            Assert.Null(store.Load());
        }

        /// <summary>
        /// Asserts that a positively incomplete pointed-to generation is removed without sweeping others.
        /// </summary>
        [Fact]
        public void Recover_KnownIncompleteCurrent_DeletesOnlyThatGeneration()
        {
            using var dir = new TempStateDir();
            IReadOnlyList<string> names = CertificateIdentities.ForFqdn(Fqdn, includeNewsHostname: false);
            var store = new CertificateStore(dir.Path, Fqdn, TestCertificateFactory.Password, names, TimeSpan.FromDays(30));
            store.Save(TestCertificateFactory.CreateMaterial(names, DateTimeOffset.UtcNow.AddDays(60)));
            string completeId = File.ReadAllText(AcmePaths.CurrentGenerationPointerPath(dir.Path, Fqdn)).Trim();
            string completeDir = AcmePaths.GenerationDir(dir.Path, Fqdn, completeId);

            // Create a second incomplete generation and point current at it.
            string incompleteId = Guid.NewGuid().ToString("N");
            string incompleteDir = AcmePaths.GenerationDir(dir.Path, Fqdn, incompleteId);
            Directory.CreateDirectory(incompleteDir);
            File.WriteAllBytes(AcmePaths.GenerationCertificatePaths(dir.Path, Fqdn, incompleteId).PfxPath, [0x01]);
            // No complete marker.
            File.WriteAllText(AcmePaths.CurrentGenerationPointerPath(dir.Path, Fqdn), incompleteId + "\n");

            store.Recover();

            Assert.False(File.Exists(AcmePaths.CurrentGenerationPointerPath(dir.Path, Fqdn)));
            Assert.False(Directory.Exists(incompleteDir));
            Assert.True(Directory.Exists(completeDir));
        }

        /// <summary>
        /// Asserts that a valid current generation is retained and obsolete generations are removed.
        /// </summary>
        [Fact]
        public void Recover_ValidCurrent_RetainsCurrentAndRemovesObsolete()
        {
            using var dir = new TempStateDir();
            IReadOnlyList<string> names = CertificateIdentities.ForFqdn(Fqdn, includeNewsHostname: false);
            var store = new CertificateStore(dir.Path, Fqdn, TestCertificateFactory.Password, names, TimeSpan.FromDays(30));
            store.Save(TestCertificateFactory.CreateMaterial(names, DateTimeOffset.UtcNow.AddDays(60)));
            string firstId = File.ReadAllText(AcmePaths.CurrentGenerationPointerPath(dir.Path, Fqdn)).Trim();
            store.Save(TestCertificateFactory.CreateMaterial(names, DateTimeOffset.UtcNow.AddDays(90)));
            string secondId = File.ReadAllText(AcmePaths.CurrentGenerationPointerPath(dir.Path, Fqdn)).Trim();

            store.Recover();

            Assert.Equal(secondId, store.CurrentGenerationId());
            Assert.False(Directory.Exists(AcmePaths.GenerationDir(dir.Path, Fqdn, firstId)));
            Assert.True(Directory.Exists(AcmePaths.GenerationDir(dir.Path, Fqdn, secondId)));
            Assert.NotNull(store.Load());
        }

        /// <summary>
        /// Asserts that recovery blocks on the issuance lock and cannot mutate state until the holder releases.
        /// </summary>
        /// <returns>A task that completes when the concurrency assertions finish.</returns>
        [Fact]
        public async Task Recover_WaitsForIssuanceLock_DoesNotRaceSave()
        {
            using var dir = new TempStateDir();
            IReadOnlyList<string> names = CertificateIdentities.ForFqdn(Fqdn, includeNewsHostname: false);
            var store = new CertificateStore(dir.Path, Fqdn, TestCertificateFactory.Password, names, TimeSpan.FromDays(30));
            store.Save(TestCertificateFactory.CreateMaterial(names, DateTimeOffset.UtcNow.AddDays(60)));
            string generationId = File.ReadAllText(AcmePaths.CurrentGenerationPointerPath(dir.Path, Fqdn)).Trim();

            var releaseLock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var holderAcquired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var recoverEnteredAcquire = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            Task holder = Task.Run(async () =>
            {
                await using AcmeIssuanceLock held = await AcmeIssuanceLock
                    .AcquireAsync(dir.Path, Fqdn, CancellationToken.None)
                    .ConfigureAwait(false);
                holderAcquired.TrySetResult();
                await releaseLock.Task.ConfigureAwait(false);
            });

            await holderAcquired.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Task recoverTask = Task.Run(() =>
            {
                recoverEnteredAcquire.TrySetResult();
                store.Recover();
            });

            await recoverEnteredAcquire.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // While the holder still owns the lock, recover must not have finished and the live gen remains.
            Assert.False(recoverTask.IsCompleted);
            Assert.True(Directory.Exists(AcmePaths.GenerationDir(dir.Path, Fqdn, generationId)));
            Assert.Equal(generationId, File.ReadAllText(AcmePaths.CurrentGenerationPointerPath(dir.Path, Fqdn)).Trim());

            releaseLock.TrySetResult();
            await recoverTask.WaitAsync(TimeSpan.FromSeconds(5));
            await holder.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.NotNull(store.Load());
            Assert.Equal(generationId, store.CurrentGenerationId());
        }

        /// <summary>
        /// Asserts that when the incomplete-current pointer cannot be deleted, the generation directory is left intact.
        /// </summary>
        [Fact]
        public void Recover_IncompleteCurrent_PointerDeleteFailure_DoesNotDeleteGeneration()
        {
            using var dir = new TempStateDir();
            IReadOnlyList<string> names = CertificateIdentities.ForFqdn(Fqdn, includeNewsHostname: false);
            var store = new CertificateStore(dir.Path, Fqdn, TestCertificateFactory.Password, names, TimeSpan.FromDays(30));

            string incompleteId = Guid.NewGuid().ToString("N");
            string incompleteDir = AcmePaths.GenerationDir(dir.Path, Fqdn, incompleteId);
            Directory.CreateDirectory(incompleteDir);
            File.WriteAllBytes(AcmePaths.GenerationCertificatePaths(dir.Path, Fqdn, incompleteId).PfxPath, [0x01]);
            string pointerPath = AcmePaths.CurrentGenerationPointerPath(dir.Path, Fqdn);
            File.WriteAllText(pointerPath, incompleteId + "\n");
            File.SetAttributes(pointerPath, FileAttributes.ReadOnly);

            try
            {
                var ex = Assert.Throws<AcmeStorageException>(() => store.Recover());
                Assert.Equal("current_pointer_delete_failed", ex.Category);
                Assert.Contains(incompleteId, ex.Message, StringComparison.Ordinal);
                Assert.True(File.Exists(pointerPath));
                Assert.True(Directory.Exists(incompleteDir));
            }
            finally
            {
                File.SetAttributes(pointerPath, FileAttributes.Normal);
            }
        }

        /// <summary>
        /// Asserts that a post-delete pointer existence-check failure is not treated as absence, so the generation is kept.
        /// </summary>
        [Fact]
        public void Recover_IncompleteCurrent_PointerExistsInspectionFailure_DoesNotDeleteGeneration()
        {
            using var dir = new TempStateDir();
            IReadOnlyList<string> names = CertificateIdentities.ForFqdn(Fqdn, includeNewsHostname: false);
            var store = new CertificateStore(dir.Path, Fqdn, TestCertificateFactory.Password, names, TimeSpan.FromDays(30));

            string incompleteId = Guid.NewGuid().ToString("N");
            string incompleteDir = AcmePaths.GenerationDir(dir.Path, Fqdn, incompleteId);
            Directory.CreateDirectory(incompleteDir);
            File.WriteAllBytes(AcmePaths.GenerationCertificatePaths(dir.Path, Fqdn, incompleteId).PfxPath, [0x01]);
            string pointerPath = AcmePaths.CurrentGenerationPointerPath(dir.Path, Fqdn);
            File.WriteAllText(pointerPath, incompleteId + "\n");

            int pointerExistsChecks = 0;
            store.FileAccessForTests = new FaultingFileAccess(
                RealCertificateStoreFileAccess.Instance,
                fileExists: path =>
                {
                    if (string.Equals(path, pointerPath, StringComparison.Ordinal))
                    {
                        // First check: ReadCurrentPointerState. Later check: TryDeleteCurrentPointer verification.
                        if (Interlocked.Increment(ref pointerExistsChecks) >= 2)
                        {
                            throw new IOException("post-delete exists inspection failed");
                        }
                    }

                    return File.Exists(path);
                });

            var ex = Assert.Throws<AcmeStorageException>(() => store.Recover());
            Assert.Equal("current_pointer_delete_failed", ex.Category);
            Assert.True(Directory.Exists(incompleteDir));
        }

        /// <summary>
        /// Asserts that a later Save after a missing pointer GCs orphan generations once a new current is established.
        /// </summary>
        [Fact]
        public void Save_AfterMissingPointer_ReconcilesOrphanGenerations()
        {
            using var dir = new TempStateDir();
            IReadOnlyList<string> names = CertificateIdentities.ForFqdn(Fqdn, includeNewsHostname: false);
            var store = new CertificateStore(dir.Path, Fqdn, TestCertificateFactory.Password, names, TimeSpan.FromDays(30));
            store.Save(TestCertificateFactory.CreateMaterial(names, DateTimeOffset.UtcNow.AddDays(60)));
            string orphanId = File.ReadAllText(AcmePaths.CurrentGenerationPointerPath(dir.Path, Fqdn)).Trim();
            string orphanDir = AcmePaths.GenerationDir(dir.Path, Fqdn, orphanId);
            AtomicFile.TryDelete(AcmePaths.CurrentGenerationPointerPath(dir.Path, Fqdn));

            store.Recover();
            Assert.True(Directory.Exists(orphanDir));

            store.Save(TestCertificateFactory.CreateMaterial(names, DateTimeOffset.UtcNow.AddDays(90)));
            string newId = File.ReadAllText(AcmePaths.CurrentGenerationPointerPath(dir.Path, Fqdn)).Trim();

            // New current is established first; only then is the orphan GC'd.
            Assert.NotEqual(orphanId, newId);
            Assert.False(Directory.Exists(orphanDir));
            Assert.True(Directory.Exists(AcmePaths.GenerationDir(dir.Path, Fqdn, newId)));
            Assert.NotNull(store.Load());
        }

        /// <summary>
        /// Asserts that a failed Save after a missing pointer does not delete the orphan complete generation.
        /// </summary>
        [Fact]
        public void Save_FailureAfterMissingPointer_PreservesOrphanGeneration()
        {
            using var dir = new TempStateDir();
            IReadOnlyList<string> names = CertificateIdentities.ForFqdn(Fqdn, includeNewsHostname: false);
            var store = new CertificateStore(dir.Path, Fqdn, TestCertificateFactory.Password, names, TimeSpan.FromDays(30));
            var orphanMaterial = TestCertificateFactory.CreateMaterial(names, DateTimeOffset.UtcNow.AddDays(60));
            store.Save(orphanMaterial);
            string orphanId = File.ReadAllText(AcmePaths.CurrentGenerationPointerPath(dir.Path, Fqdn)).Trim();
            string orphanDir = AcmePaths.GenerationDir(dir.Path, Fqdn, orphanId);
            byte[] orphanPfx = File.ReadAllBytes(AcmePaths.GenerationCertificatePaths(dir.Path, Fqdn, orphanId).PfxPath);
            AtomicFile.TryDelete(AcmePaths.CurrentGenerationPointerPath(dir.Path, Fqdn));

            var invalid = orphanMaterial with { PfxBytes = [0x00, 0x01] };
            Assert.ThrowsAny<Exception>(() => store.Save(invalid));

            Assert.False(File.Exists(AcmePaths.CurrentGenerationPointerPath(dir.Path, Fqdn)));
            Assert.True(Directory.Exists(orphanDir));
            Assert.True(orphanPfx.AsSpan().SequenceEqual(
                File.ReadAllBytes(AcmePaths.GenerationCertificatePaths(dir.Path, Fqdn, orphanId).PfxPath)));
        }

        /// <summary>
        /// Asserts that after a transient inspection failure clears, recovery and load succeed without data loss.
        /// </summary>
        [Fact]
        public void Recover_AfterTransientFailureClears_SucceedsWithoutDataLoss()
        {
            using var dir = new TempStateDir();
            IReadOnlyList<string> names = CertificateIdentities.ForFqdn(Fqdn, includeNewsHostname: false);
            var store = new CertificateStore(dir.Path, Fqdn, TestCertificateFactory.Password, names, TimeSpan.FromDays(30));
            var material = TestCertificateFactory.CreateMaterial(names, DateTimeOffset.UtcNow.AddDays(60));
            store.Save(material);
            string generationId = File.ReadAllText(AcmePaths.CurrentGenerationPointerPath(dir.Path, Fqdn)).Trim();
            string pfxPath = AcmePaths.GenerationCertificatePaths(dir.Path, Fqdn, generationId).PfxPath;

            store.FileAccessForTests = new FaultingFileAccess(
                RealCertificateStoreFileAccess.Instance,
                getFileLength: _ => throw new IOException("busy"));
            Assert.Throws<AcmeStorageException>(() => store.Recover());

            store.FileAccessForTests = null;
            store.Recover();
            CertificateMaterial? loaded = store.Load();
            Assert.NotNull(loaded);
            Assert.True(File.Exists(pfxPath));
            Assert.Equal(generationId, store.CurrentGenerationId());
        }

        /// <summary>
        /// Asserts that constructor recovery throws on unreadable pointer without deleting on-disk state.
        /// </summary>
        [Fact]
        public void Constructor_UnreadablePointer_ThrowsWithoutDeleting()
        {
            using var dir = new TempStateDir();
            IReadOnlyList<string> names = CertificateIdentities.ForFqdn(Fqdn, includeNewsHostname: false);
            var seed = new CertificateStore(dir.Path, Fqdn, TestCertificateFactory.Password, names, TimeSpan.FromDays(30));
            seed.Save(TestCertificateFactory.CreateMaterial(names, DateTimeOffset.UtcNow.AddDays(60)));
            string generationId = File.ReadAllText(AcmePaths.CurrentGenerationPointerPath(dir.Path, Fqdn)).Trim();
            string pointerPath = AcmePaths.CurrentGenerationPointerPath(dir.Path, Fqdn);
            string genDir = AcmePaths.GenerationDir(dir.Path, Fqdn, generationId);

            // Lock the pointer file for reading exclusively so the new store's Recover cannot read it.
            using (var locked = new FileStream(
                       pointerPath,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.None))
            {
                var ex = Assert.Throws<AcmeStorageException>(() =>
                    new CertificateStore(dir.Path, Fqdn, TestCertificateFactory.Password, names, TimeSpan.FromDays(30)));
                Assert.Equal("current_pointer_unreadable", ex.Category);
                Assert.True(File.Exists(pointerPath));
                Assert.True(Directory.Exists(genDir));
                _ = locked;
            }

            var recovered = new CertificateStore(dir.Path, Fqdn, TestCertificateFactory.Password, names, TimeSpan.FromDays(30));
            Assert.NotNull(recovered.Load());
        }

        /// <summary>
        /// Asserts that <see cref="CertificateStore.Paths"/> surfaces unreadable-pointer failures without deletion.
        /// </summary>
        [Fact]
        public void Paths_UnreadablePointer_ThrowsWithoutDeleting()
        {
            using var dir = new TempStateDir();
            IReadOnlyList<string> names = CertificateIdentities.ForFqdn(Fqdn, includeNewsHostname: false);
            var store = new CertificateStore(dir.Path, Fqdn, TestCertificateFactory.Password, names, TimeSpan.FromDays(30));
            store.Save(TestCertificateFactory.CreateMaterial(names, DateTimeOffset.UtcNow.AddDays(60)));
            string generationId = File.ReadAllText(AcmePaths.CurrentGenerationPointerPath(dir.Path, Fqdn)).Trim();
            string pointerPath = AcmePaths.CurrentGenerationPointerPath(dir.Path, Fqdn);

            store.FileAccessForTests = new FaultingFileAccess(
                RealCertificateStoreFileAccess.Instance,
                readAllText: path =>
                {
                    if (string.Equals(path, pointerPath, StringComparison.Ordinal))
                    {
                        throw new IOException("denied");
                    }

                    return File.ReadAllText(path);
                });

            var ex = Assert.Throws<AcmeStorageException>(() => store.Paths());
            Assert.Equal("current_pointer_unreadable", ex.Category);
            Assert.True(Directory.Exists(AcmePaths.GenerationDir(dir.Path, Fqdn, generationId)));
        }

        /// <summary>Delegates to an inner access implementation with optional overrides.</summary>
        private sealed class FaultingFileAccess : ICertificateStoreFileAccess
        {
            private readonly ICertificateStoreFileAccess _inner;
            private readonly Func<string, bool>? _fileExists;
            private readonly Func<string, string>? _readAllText;
            private readonly Func<string, long>? _getFileLength;

            public FaultingFileAccess(
                ICertificateStoreFileAccess inner,
                Func<string, bool>? fileExists = null,
                Func<string, string>? readAllText = null,
                Func<string, long>? getFileLength = null)
            {
                _inner = inner;
                _fileExists = fileExists;
                _readAllText = readAllText;
                _getFileLength = getFileLength;
            }

            public bool FileExists(string path) => _fileExists?.Invoke(path) ?? _inner.FileExists(path);

            public string ReadAllText(string path) => _readAllText?.Invoke(path) ?? _inner.ReadAllText(path);

            public long GetFileLength(string path) => _getFileLength?.Invoke(path) ?? _inner.GetFileLength(path);
        }
    }
}
