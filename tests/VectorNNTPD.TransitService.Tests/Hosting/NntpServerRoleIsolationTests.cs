using Microsoft.Extensions.DependencyInjection;
using VectorNNTPD.TransitService.Hosting;
using VectorNNTPD.TransitService.PostFilter;
using VectorNNTPD.TransitService.Session;
using VectorNNTPD.TransitService.Session.Authentication;
using VectorNNTPD.TransitService.SessionState;
using VectorNNTPD.TransitService.Transit;

namespace VectorNNTPD.TransitService.Tests.Hosting
{
    public sealed class NntpServerRoleIsolationTests
    {
        [Fact]
        public void Reader_hosting_on_transit_executable_throws()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            var ex = Assert.Throws<NotSupportedException>(() => services.AddNntpReaderHosting());
            Assert.Contains("Reader", ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Transit_host_registers_transit_markers_without_reader_post_filter()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddNntpTransitHosting();

            Assert.Contains(
                services,
                static d => d.ServiceType == typeof(NntpServerRole)
                    && Equals(d.ImplementationInstance, NntpServerRole.Transit));
            Assert.Contains(services, static d => d.ImplementationType == typeof(TransitCatalogueService));
            Assert.Contains(services, static d => d.ImplementationType == typeof(TransitPeerAuthorization));
            Assert.DoesNotContain(services, static d => d.ImplementationType == typeof(PostFilterPolicyService));
            Assert.DoesNotContain(services, static d => d.ImplementationType == typeof(SessionStateService));

            Assert.Contains(
                services,
                static d => d.ServiceType == typeof(INntpAuthenticationProvider)
                    && d.ImplementationFactory is not null);
        }
    }
}
