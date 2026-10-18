# Live Article Work RPC test

`tools/live_article_work_rpc.py` talks to a BackFiller process that is already running. It opens the TLS NNTP listener, publishes one Article Work v1 request, and then sends `ARTICLE <message-id>` on that same NNTP connection.

It does not start, stop, or reconfigure BackFiller. It does not declare, bind, purge, or delete `backfiller.{backbone}`. It does not post an article. The RabbitMQ publish is the Article Work request the running consumer already expects.

`--self-test` checks framing and JSON helpers in this process only. It does not open a socket and it does not stand in for the live run.

## What the running server must already be doing

BackFiller consumes Article Work, retrieves the article from the upstream provider, retains it, and only then publishes the RPC response. `ARTICLE` is served from that retention by the TLS listener. There is no cleartext NNTP listener.

After the greeting the listener is blocked on the next command. Each read uses `BackFiller:Listener:IoProgressTimeoutSeconds` (default 60). This script connects to RabbitMQ first, and it does not publish until the greeting is valid, so broker setup is not part of that idle window. The provider retrieval still has to finish, and `ARTICLE` still has to be written, before that read timer fires. The default `--rpc-timeout` is 30 seconds, matching `RabbitMQ:RpcTimeoutSeconds`. The listener accepts `ARTICLE`, `DATE`, and `QUIT`. Any other command is `500`. This script sends `ARTICLE` and `QUIT` only, so it does not use `DATE` to refresh that idle read.

A success response means the article is already retained. The script does not retry `ARTICLE` and it does not publish the RPC request again. ProviderFailure, Cancelled, UnexpectedFailure, and RetentionRejected are not given a response body; those cases surface as an RPC timeout. The request and the response both use an AMQP expiration of `1000` milliseconds. The exclusive reply queue is declared while connecting to RabbitMQ, before the NNTP handshake. `basic_consume` is registered after the greeting and immediately before the single publish, so the response TTL is not spent waiting for this process to subscribe.

## Contract

| Item | Value |
|---|---|
| NNTP transport | TLS 1.2 or 1.3. No client certificate. |
| Greeting | `201 NNTP Service Ready, posting prohibited` |
| Article command | `ARTICLE <message-id>` terminated by CRLF. Message-ID includes the angle brackets. |
| Success status | `220 0 <message-id> article follows` |
| Article body | Multiline block ended by a line that is exactly `.` CRLF. One leading dot is removed from every other dot-leading line. |
| Other listener results | `430` not retained, `403` not available, `412` number form, `501` syntax, `500` unknown command. |
| Exchange, queue, routing key | `backfiller.{backbone}` after trim and invariant lower-case. The exchange is fanout. Example: backbone `Giganews` uses `backfiller.giganews`. |
| Request JSON | `version` (1), `requestId` (UUID), `messageId`, `backbone`. Unknown fields are ignored by the server. Names are case-sensitive. |
| Request AMQP | `ContentType=application/json`, `CorrelationId`, `ReplyTo` (this script's exclusive reply queue), header `RequestId` equal to JSON `requestId`, expiration `1000`. |
| Response JSON | `version`, `requestId`, `messageId`, `backbone`, `outcome`. Success adds `fqdn`, `listenerPort`, and `articleId` (64 lowercase hex digits, BLAKE3 of the Message-ID octets including the brackets). Failure outcomes `ArticleNotFound`, `InvalidArticle`, and `InvalidRequest` carry `error` and omit the success fields. |
| Response AMQP | Default exchange, routing key equal to the request `ReplyTo`, same `CorrelationId`, `ContentType=application/json`, expiration `1000`. |
| Publish shape | One mandatory publish. The in-repository RPC channel publishes `mandatory: false`; this script uses `mandatory: true` so a missing binding fails instead of waiting out the timeout. The JSON body is the same. |

Known backbone labels are `Abavia`, `Altopia`, `BaseIP`, `Eweka`, `Elbracht`, `Giganews`, `GTT`, `Highwinds`, `ItsHosted`, `Novia`, `UExpress`, and `UsenetNode1`. This script does not require the label to be one of those names. It accepts any 1–64 character token from `A–Z`, `a–z`, `0–9`, `.`, `_`, and `-`. The JSON `backbone` is compared to the consuming session with ordinal ignore-case. The response echoes the spelling sent in the request. The exchange name is always lower-case.

No provider username, path, or article body is part of the request. `articleId` is not a checksum of the article body.

## Dependency

Python 3.11 or newer, plus the AMQP client:

```text
python -m pip install pika
```

Optional, only to recompute `articleId`:

```text
python -m pip install blake3
```

Without `blake3`, the script still checks that `articleId` is 64 lowercase hex digits and says the digest was not recomputed.

## Configuration

Flags override environment variables. An omitted flag inherits the environment variable, then the default. Boolean flags accept an explicit opposite: `--no-insecure-skip-tls-verify` forces certificate verification, and `--no-rabbitmq-tls` forces plaintext AMQP. Passwords are read from the environment when `--rabbitmq-password` is omitted. The script does not print the password, the username, or an AMQP URL.

`--amqp-url` or `BACKFILLER_AMQP_URL` is the broker identity. In that mode the script does not read or validate `RABBITMQ_HOST`, `RABBITMQ_PORT`, `RABBITMQ_VHOST`, `RABBITMQ_CA_FILE`, `RABBITMQ_USE_TLS`, or the discrete username and password. An `amqps` URL selects TLS by itself. Passing both `--amqp-url` and `--rabbitmq-host` is an error. `--rabbitmq-host` overrides an environment AMQP URL and uses the discrete settings.

| Setting | Flag | Environment variable |
|---|---|---|
| NNTP host | `--nntp-host` | `NNTP_HOST` |
| NNTP TLS port (`BackFiller:BindPortTls`) | `--nntp-port` | `NNTP_PORT` |
| Certificate name / SNI | `--tls-server-name` | `NNTP_TLS_SERVER_NAME` |
| Listener CA bundle | `--ca-file` | `NNTP_CA_FILE` |
| Skip listener certificate checks (default false) | `--insecure-skip-tls-verify` / `--no-insecure-skip-tls-verify` | `NNTP_INSECURE_SKIP_TLS_VERIFY=true` |
| Message-ID, including brackets | `--message-id` | `BACKFILLER_MESSAGE_ID` |
| Backbone label | `--backbone` | `BACKFILLER_BACKBONE` |
| AMQP URL (overrides the discrete broker settings) | `--amqp-url` | `BACKFILLER_AMQP_URL` |
| Broker host | `--rabbitmq-host` | `RABBITMQ_HOST` |
| Broker port (default 5672) | `--rabbitmq-port` | `RABBITMQ_PORT` |
| Virtual host (default `/`) | `--rabbitmq-vhost` | `RABBITMQ_VHOST` |
| Broker username | `--rabbitmq-username` | `VECTOR__RABBITMQ__USERNAME` |
| Broker password | `--rabbitmq-password` | `VECTOR__RABBITMQ__PASSWORD` |
| Broker TLS for discrete settings (default false) | `--rabbitmq-tls` / `--no-rabbitmq-tls` | `RABBITMQ_USE_TLS=true` |
| Broker CA bundle | `--rabbitmq-ca-file` | `RABBITMQ_CA_FILE` |
| TCP/TLS connect timeout seconds (default 30) | `--connect-timeout` | `BACKFILLER_CONNECT_TIMEOUT_SECONDS` |
| RPC wait seconds (default 30) | `--rpc-timeout` | `BACKFILLER_RPC_TIMEOUT_SECONDS` |
| NNTP read/write timeout seconds (default 60) | `--nntp-timeout` | `BACKFILLER_NNTP_TIMEOUT_SECONDS` |
| Multiline wire-byte cap, including the terminator (default 67108864) | `--max-article-bytes` | `BACKFILLER_MAX_ARTICLE_BYTES` |

`VectorNNTPD.RabbitMQ.json` in this repository sets `EnableSsl` to false and `Port` to 5672. Turn on `--rabbitmq-tls` only when the broker you are using actually requires TLS. An `amqps` URL already selects TLS.

Use the listener FQDN as `--nntp-host` when you want certificate verification and the FQDN check. `BindPortTls` is that listener's port. Connecting by IP or `localhost` skips the FQDN equality check and still requires `listenerPort` to equal `--nntp-port`. Pass `--tls-server-name` when SNI must be the certificate name.

## Sample invocation

Placeholders only:

```powershell
$env:VECTOR__RABBITMQ__USERNAME = "<username>"
$env:VECTOR__RABBITMQ__PASSWORD = "<password>"
python tools/live_article_work_rpc.py `
  --nntp-host backfiller01.example.invalid `
  --nntp-port 563 `
  --backbone Giganews `
  --message-id "<12345@example.invalid>" `
  --rabbitmq-host rabbit.example.invalid `
  --rabbitmq-port 5672 `
  --rabbitmq-vhost /
```

```sh
VECTOR__RABBITMQ__USERNAME='<username>' \
VECTOR__RABBITMQ__PASSWORD='<password>' \
python tools/live_article_work_rpc.py \
  --nntp-host backfiller01.example.invalid \
  --nntp-port 563 \
  --backbone Giganews \
  --message-id '<12345@example.invalid>' \
  --rabbitmq-host rabbit.example.invalid
```

Exit 0 means the greeting, the success RPC response, the `220` article, the terminator, the Message-ID, and the listener metadata all matched. Exit 1 is a failed live assertion. Exit 2 is configuration or a missing `pika` install.

The failure line distinguishes:

- NNTP connection/greeting failure
- RabbitMQ RPC failure or timeout
- RPC success followed by NNTP ARTICLE failure
- Incomplete or malformed article framing
- Message-ID mismatch
- RPC listener metadata does not match the NNTP connection
