# VectorNNTPD.Common architecture notes

These passages are the Common-owned parts of the VectorNNTP architecture document. Host startup order below is context for how applications use the library. It is not a second BackFiller lifecycle.

## Byte-oriented protocol data

Protocol data remains byte-oriented throughout the data plane. NNTP and VATP are byte-oriented wire protocols. A `string` is an application representation. Protocol data must not become a `string` by default, and must not make an unnecessary round trip such as `bytes → string → bytes`.

Incoming bytes are not converted to strings unless an explicit application, API, or logging boundary requires it. Static outgoing protocol text is pre-encoded once and reused. Dynamic outgoing protocol fields remain bytes where practical.

Strings are permitted at justified boundaries. They are not the default representation of protocol data.

## Cloudflare DNS and ACME

Startup order in the original hosts places `CloudflareDnsReconciliationApplicationService` among the application services. The adapter delegates to `CloudflareDnsReconciliationService`, which resolves eligible bind addresses (including intentional private IPs), reconciles A/AAAA for the generated FQDN via the Cloudflare DNS API, verifies the remote set, and fails startup on a hard error. Configuration validation does not call Cloudflare.

When TLS is enabled, `AcmeCertificateService` ensures the ACME account and a usable certificate (DNS-01 via Cloudflare), then publishes an immutable `SslStreamCertificateContext` for new handshakes. DNS-01 publication and cleanup use `CloudflareDnsClient`. Visibility checks use `AuthoritativeTxtResolver`, which discovers apex nameservers and queries TXT over UDP, then TCP, through `VectorNNTPD.Common.Dns`. That query path is not the Cloudflare API client and does not publish listener A/AAAA records. Certificate rotation replaces the published context without rebinding listeners. Each renewal attempt snapshots the shared-configuration PKCS#12 password once. The existing PFX is loaded with the password that last saved it. A new certificate is issued, stored, and published with that snapshot, and the store adopts the snapshot only after the new generation is current. A blank snapshot does not replace the password already in use. An invalid snapshot that cannot validate the new PFX leaves the previous certificate in place.

On shutdown, Cloudflare DNS cleanup removes every DNS record for the exact FQDN and verifies none remain. Parent, child, and other hostnames are not deleted. ACME does not contact the certificate authority during shutdown.

Cloudflare multi-record updates are not atomic. Each reconcile lists both families, creates missing A/AAAA records before deletes, updates kept records to managed attributes (`ttl=300`, `proxied=false`), deletes stale records, then verifies the set. Attempts share `CloudFlareOperationTimeout` (default 2 minutes) linked with the caller token. HTTP attempts are also capped by `min(30s, remaining budget)`. Same-instance reconcile and cleanup calls are serialized. Credentials and raw Cloudflare response bodies are not logged in exception messages.

Shared ACME, Cloudflare DNS, and bind-address implementations live under `VectorNNTPD.Common.Acme`, `VectorNNTPD.Common.Cloudflare`, `VectorNNTPD.Common.Networking`, and `VectorNNTPD.Common.Configuration`. Shared implementation does not imply one shared configuration section. Applications adapt their own bind addresses, ports, and identity into these types. There is one account key (`account/private_key.der`) and one journal file per certificate FQDN (`journal/{fqdn}.json`). Live certificate state lives under `live/{fqdn}/`. Production code does not use a global `live/gens` or `live/current` path.

## RabbitMQ connectivity

RabbitMQ connectivity (options, connection factory, `RabbitMqService`, channel adapters, reconnect and generation lifecycle) lives in `VectorNNTPD.Common.Messaging.RabbitMq`. Applications bind the top-level `RabbitMQ` section, call `AddRabbitMqInfrastructure()`, supply `IRabbitMqConnectionNameProvider`, and register `RabbitMqService` when the broker is a startup dependency.

Common does not declare exchanges, queues, or bindings. It does not own Management HTTP, Article Work, or OverviewDB publishing. The OverviewDB protobuf schema and codec (`OverviewArticleV1`, `OverviewArticleV1Codec`) are Common types. Publishing those bytes is application-owned.

`RabbitMqService` owns one process-wide AMQP connection and a monotonic connection generation. Client automatic recovery is disabled. Callers use `TryGetCurrent` for a non-owning snapshot (`RabbitMqConnectionHandle`). The handle does not pin the connection. Callers must not dispose it. After start, reconnect continues until a new generation is installed or shutdown cancels the loop. Shutdown disposes the current connection once. A manual-ack channel has one consumer. A second registration is rejected. A failed or cancelled registration detaches its handler before the error propagates. Disposal waits for in-flight registration, marks the channel closed, then closes the client channel. Expected close failures are written to the Serilog self-log. Any other close failure propagates after the client channel is disposed.

`ApplicationJsonConfiguration` owns the shared JSON source replacement. It removes Generic Host `appsettings` and `*.settings.json` sources and registers `{entry assembly}.json` and `{entry assembly}.{environment}.json`. The sample connectivity file shipped with the library is `src/VectorNNTPD.Common/VectorNNTPD.RabbitMQ.json`. Callers pass that filename explicitly. BackFiller passes `VectorNNTPD.RabbitMQ.json`.

## Vector Article Transfer Protocol

Common owns the multiplexed binary article data plane under `VectorNNTPD.Common.Transport.ArticleTransfer`. The contract is [vatp.md](vatp.md). The shared initial per-stream window is 1 MiB (`ArticleTransferLimits.Default`). HELLO does not negotiate it.

## Article records

The in-memory article envelope is [ArticleRecord.md](ArticleRecord.md). `ArticleRecord` is not itself a serialization protocol.
