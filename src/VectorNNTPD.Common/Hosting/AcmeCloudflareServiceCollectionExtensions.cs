using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using VectorNNTPD.Common.Acme;
using VectorNNTPD.Common.Cloudflare;
using VectorNNTPD.Common.Networking;
using VectorNNTPD.Common.Networking.Certificates;
using VectorNNTPD.Common.Configuration;

namespace VectorNNTPD.Common.Hosting
{
    /// <summary>
    /// Registers shared ACME, Cloudflare, and bind-address infrastructure.
    /// </summary>
    internal static class AcmeCloudflareServiceCollectionExtensions
    {
        /// <summary>
        /// Adds bind-address resolution, Cloudflare DNS, and ACME component factories.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <returns>The same <paramref name="services"/> instance.</returns>
        /// <remarks>
        /// Requires <see cref="IOptions{TOptions}"/> of <see cref="AcmeCloudflareOptions"/> to be registered
        /// by the application. Does not register application lifecycle/hosted wrappers.
        /// </remarks>
        internal static IServiceCollection AddAcmeCloudflareInfrastructure(this IServiceCollection services)
        {
            ArgumentNullException.ThrowIfNull(services);

            services.TryAddSingleton<ILocalIpAddressAssignee, NetworkInterfaceLocalIpAddressAssignee>();
            services.TryAddSingleton<IBindAddressResolver, BindAddressResolver>();
            services.TryAddSingleton<IAcmeCertificateReadiness, AcmeCertificateReadiness>();
            services.TryAddSingleton<ICloudflareDnsReconciler, CloudflareDnsReconciler>();

            services.AddHttpClient(CloudflareDnsClient.HttpClientName, static client =>
            {
                client.BaseAddress = new Uri("https://api.cloudflare.com/client/v4/");
                client.Timeout = Timeout.InfiniteTimeSpan;
                client.DefaultRequestHeaders.ExpectContinue = false;
            });

            services.AddHttpClient(AcmeIssuer.HttpClientName, static client =>
            {
                client.Timeout = Timeout.InfiniteTimeSpan;
                client.DefaultRequestHeaders.ExpectContinue = false;
            });

            services.TryAddSingleton<ICloudflareDnsClient>(static sp =>
            {
                var httpClient = sp.GetRequiredService<IHttpClientFactory>()
                    .CreateClient(CloudflareDnsClient.HttpClientName);
                return new CloudflareDnsClient(
                    httpClient,
                    sp.GetRequiredService<IOptions<AcmeCloudflareOptions>>(),
                    sp.GetRequiredService<ILogger<CloudflareDnsClient>>());
            });

            services.TryAddSingleton<TlsCertificateContextProvider>();
            services.TryAddSingleton<ITlsCertificateContextProvider>(static sp =>
                sp.GetRequiredService<TlsCertificateContextProvider>());
            services.TryAddSingleton<IAcmeCertificatePublisher>(static sp =>
                sp.GetRequiredService<TlsCertificateContextProvider>());
            services.TryAddSingleton<AcmeComponentFactory>(static sp =>
                new AcmeComponentFactory(
                    sp.GetRequiredService<IOptions<AcmeCloudflareOptions>>(),
                    sp.GetRequiredService<ICloudflareDnsClient>(),
                    sp.GetRequiredService<IHttpClientFactory>(),
                    sp.GetRequiredService<ILoggerFactory>()));
            services.TryAddSingleton<IServerCertificateProvider>(static sp =>
                sp.GetRequiredService<AcmeComponentFactory>().GetCertificateProvider());
            services.TryAddSingleton(static provider =>
                new CloudflareDnsReconciliationService(
                    provider.GetRequiredService<IOptions<AcmeCloudflareOptions>>(),
                    provider.GetRequiredService<IBindAddressResolver>(),
                    provider.GetRequiredService<ICloudflareDnsReconciler>(),
                    provider.GetRequiredService<ILogger<CloudflareDnsReconciliationService>>(),
                    provider.GetService<IDnsPublicationLease>()));
            services.TryAddSingleton<IListenerDnsPublication>(static provider =>
                provider.GetRequiredService<CloudflareDnsReconciliationService>());
            services.TryAddSingleton<AcmeCertificateService>(static sp =>
                new AcmeCertificateService(
                    sp.GetRequiredService<IOptions<AcmeCloudflareOptions>>(),
                    sp.GetRequiredService<AcmeComponentFactory>(),
                    sp.GetRequiredService<IAcmeCertificatePublisher>(),
                    sp.GetRequiredService<IAcmeCertificateReadiness>(),
                    sp.GetRequiredService<ILogger<AcmeCertificateService>>(),
                    sp.GetService<TimeProvider>() ?? TimeProvider.System,
                    sp.GetService<IAcmeCertificateObservation>()));

            return services;
        }
    }
}
