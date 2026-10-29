using Microsoft.Extensions.DependencyInjection;
using VectorNNTPD.ReaderService.Hosting;
using VectorNNTPD.ReaderService.PostFilter;
using VectorNNTPD.ReaderService.Session;
using VectorNNTPD.ReaderService.SessionState;
using VectorNNTPD.ReaderService.Transit;

namespace VectorNNTPD.ReaderService.Tests.Hosting
{
    public sealed class NntpServerRoleIsolationTests
    {
        [Fact]
        public void Reader_host_registers_reader_markers_without_transit_catalogue()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddNntpReaderHosting();

            Assert.Contains(
                services,
                static d => d.ServiceType == typeof(NntpServerRole)
                    && Equals(d.ImplementationInstance, NntpServerRole.Reader));
            Assert.Contains(services, static d => d.ImplementationType == typeof(SessionStateService));
            Assert.Contains(services, static d => d.ImplementationType == typeof(PostFilterPolicyService));
            Assert.DoesNotContain(services, static d => d.ImplementationType == typeof(TransitCatalogueService));
            Assert.DoesNotContain(services, static d => d.ImplementationType == typeof(TransitPeerAuthorization));
        }

        [Fact]
        public void Transit_hosting_on_reader_executable_throws()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            var ex = Assert.Throws<NotSupportedException>(() => services.AddNntpTransitHosting());
            Assert.Contains("Transit", ex.Message, StringComparison.Ordinal);
        }
    }
}
