# Terraform IaC deploy to LocalStack — design

## Problem

The platform runs from `docker-compose.yml` and nothing else. There is no
infrastructure-as-code, and no deployment path that resembles the AWS shape
the docs keep promising — `production-considerations.md` talks about secrets
managers, load balancers and gated migration jobs, but none of it is
expressed anywhere executable.

The ask: Terraform that deploys this platform to a local LocalStack.

## What LocalStack can actually do here

The health endpoint reports `edition: pro` and lists `ecs`, `rds`, `elbv2`,
`ecr` as `"available"`. **That listing is not to be trusted.** A spike
(ALB + Fargate service + RDS instance) failed on every one of them with
`StatusCode: 501 — not included within your LocalStack license`. The licence
is properly activated (`is_license_activated: true`, token present in the
container); the tier simply does not cover those services.

Empirically probed, this LocalStack covers:

| Available | Not licensed |
| --- | --- |
| S3, Lambda, SQS, SNS, DynamoDB | **ECS, ECR, RDS, ELBv2** |
| Secrets Manager, SSM, IAM, KMS | EKS, Batch, EFS |
| CloudWatch Logs, EventBridge, Step Functions | CloudFront, API Gateway v2 |
| API Gateway v1, EC2, Route53 | AppConfig |

So there is **no container compute and no managed Postgres available**. Any
design that puts the API on ECS or the database on RDS is fiction on this
machine. The compute has to stay in Docker; what LocalStack can own is the
*edge* and the *config plane* — and those are genuinely in the request path,
not decoration.

## Architecture

```
Browser
   │
   ├──► S3 static website ─────────────► dashboard (React dist/)
   │    <bucket>.s3-website.localhost.localstack.cloud:4566
   │
   └──► API Gateway {proxy+} ──────────► host.docker.internal:5080
        <id>.execute-api.localhost.localstack.cloud:4566/local        │
                                                                      ▼
   Secrets Manager ──┐                                       ┌─────────────────┐
   SSM Parameters ───┼──► entrypoint reads at boot ─────────►│ payments-api    │
   CloudWatch Logs ◄─┘                                       │ (docker)        │
   SQS (settlement seam)                                     └────────┬────────┘
                                                                      ▼
                                                             ┌─────────────────┐
                                                             │ postgres:16     │
                                                             │ (docker)        │
                                                             └─────────────────┘
```

One `terraform apply` does all of it: the `aws` and `kreuzwerker/docker`
providers in a single root module.

### Verified by spike, not assumed

Every load-bearing claim above was tested against the live LocalStack before
this design was written:

- **S3 static website hosting** — bucket created, website configuration set,
  `index.html` served over HTTP at the `s3-website.localhost.localstack.cloud`
  host. Returns content.
- **API Gateway v1 `{proxy+}` with `HTTP_PROXY` integration** — created via
  Terraform, deployed to a stage, and proxied a request through to an nginx
  container on the host. Returned `200` with the origin's body.
- **LocalStack → host container networking** — `host.docker.internal` is
  reachable from inside the LocalStack container.
- **Secrets Manager, SSM Parameter Store, SQS, CloudWatch Logs** — all
  created successfully through Terraform.
- **`curl` against Secrets Manager and SSM from inside a
  `dotnet/aspnet:10.0` container** — returned the secret string and the
  parameter value using only a dummy SigV4 header, no AWS CLI installed.

The spikes were destroyed afterwards; nothing was left behind.

## Provider safety

The machine has real AWS credentials in `~/.aws`. The provider block
therefore pins `access_key = "test"`, `secret_key = "test"` and explicit
`endpoints` for every service, rather than relying on `tflocal` to inject
them. A plain `terraform apply` in this directory physically cannot reach a
real AWS account — the endpoints all point at `localhost:4566`. This is a
safety property, and it is why `tflocal` is not required.

## Components

New `infra/localstack/` directory. Nothing outside it changes except the
frontend's API base URL (below).

| File | Contents |
| --- | --- |
| `providers.tf` | `aws` pinned to LocalStack endpoints with dummy creds; `kreuzwerker/docker` |
| `config.tf` | Secrets Manager secret (DB connection string), SSM parameters (app config), CloudWatch log group, SQS settlement queue |
| `database.tf` | `postgres:16-alpine` container, named volume, healthcheck |
| `api.tf` | API image build, container with the config-fetching entrypoint, depends on the secret and on Postgres being healthy |
| `frontend.tf` | S3 bucket, website configuration, public-read policy, `dist/` upload with correct content types |
| `gateway.tf` | REST API, `{proxy+}` resource, `ANY` method, `HTTP_PROXY` integration, deployment, stage |
| `variables.tf` / `outputs.tf` | Knobs; the dashboard URL and API URL to open |

### Config plane

The API container gets a small shell entrypoint that, at boot, fetches the
connection string from Secrets Manager and the app settings from SSM, exports
them as the environment variables the app already reads, then `exec`s
`dotnet Payments.Api.dll`.

This means LocalStack genuinely holds the configuration and the app genuinely
consumes it — **with zero C# changes**, because the app already reads
`ConnectionStrings__Payments`, `DemoTraffic__*` and `FakeGateway__*` from the
environment. The seam that `production-considerations.md` describes
("real deployment pulls them from a secrets manager into the environment") is
now demonstrated rather than promised.

The entrypoint would normally need an AWS CLI in the image. Rather than bloat
the runtime image, the container overrides its command with a shell that
`curl`s the LocalStack endpoint directly — `curl` is already installed in the
runtime image for the compose healthcheck, so `backend/Dockerfile` is not
touched.

This was verified: LocalStack accepts a dummy SigV4 `Authorization` header, so
a plain `curl -X POST` with the right `X-Amz-Target` returns the secret and
the parameter from inside a `dotnet/aspnet:10.0` container. Two constraints
follow:

- The container needs `host.docker.internal` mapped to the host gateway. The
  Docker provider's container resource gets an explicit `host` block for this;
  it is not automatic on Linux.
- Responses are parsed with `sed`, not `jq`, since the runtime image has no
  `jq` and adding one would mean editing the Dockerfile. This means **config
  values must not contain double quotes**. Connection strings and the numeric
  demo settings don't, and the README notes the constraint.

### SQS settlement queue

Provisioned but not consumed. `README.md` and `tradeoffs.md` both describe the
Postgres outbox as a deliberate step toward a real broker; the queue exists so
that the next step has somewhere to land. This is explicitly a seam, not a
working integration, and the README will say so rather than implying the app
publishes to it.

### Frontend

Served from S3, there is no nginx, so the dashboard's relative `/api` calls
have nothing to proxy them. The frontend needs the API Gateway URL at build
time.

- A `VITE_API_BASE` environment variable, defaulting to `''` so the existing
  compose and `npm run dev` paths keep working unchanged (relative `/api`,
  proxied by nginx or Vite respectively).
- The frontend's fetch layer prefixes requests with it.
- Terraform builds the dashboard with `VITE_API_BASE` set to the API Gateway
  stage URL, then uploads `dist/` to the bucket.

Because the dashboard is then on a different origin from the API, the API
needs CORS for the S3 website origin. This is added as an ASP.NET Core CORS
policy enabled only when an allowed-origins config value is present, so the
compose path — same origin, no CORS needed — is unaffected.

### Migrations

`ASPNETCORE_ENVIRONMENT=Development`, exactly as compose does it, so the API
applies migrations and seeds demo data on startup. `production-considerations.md`
correctly says migrations should be a pipeline-gated deploy step; a separate
one-shot migration container would honour that, but Terraform cannot cleanly
sequence it without a `null_resource`, and the value here is a working local
deploy rather than a faithful release pipeline. This is a deliberate deviation
and the README records it.

Demo seed and the traffic generator stay enabled so the dashboard has data.
OpenTelemetry stays dormant — `OTEL_EXPORTER_OTLP_ENDPOINT` unset — because
Tempo is not part of this deployment and the app is built to no-op without it.

## Scope boundaries

**In scope:** API, Postgres, dashboard, and the LocalStack config/edge plane.

**Out of scope:** the observability stack. LocalStack has no meaningful
Grafana/Prometheus/Loki/Tempo analog, and running them as bare containers
under Terraform would add moving parts without adding fidelity. They stay in
`docker-compose.yml` under the `observability` profile, where they already
work.

**Unchanged:** `docker-compose.yml` and `frontend/nginx.conf` are not touched.
This design adds a deployment path; it does not replace the existing one. The
inner dev loop and the demo profile keep working exactly as they do today.

## Known limitations

- **Persistence is disabled** on this LocalStack, so a restart wipes every
  AWS-side resource while Terraform state still claims they exist. Recovery is
  `terraform state rm` or a full re-apply; the README documents this.
- **Postgres data survives** in a Docker named volume independently of
  LocalStack's lifecycle, so the two can drift out of sync after a LocalStack
  restart.
- **API Gateway adds a hop** that compose does not have, so latency figures
  from this path are not comparable to the compose numbers.
- **The deployment is single-instance.** There is no load balancer available,
  so the multi-replica behaviour the outbox design tolerates is not exercised
  here.

## Testing

- `terraform validate` and `terraform plan` are clean.
- `terraform apply` from a cold LocalStack reaches completion.
- The dashboard URL serves the app, and the app loads payment data through
  the API Gateway URL.
- The API's `/healthz` and `/readyz` both answer through API Gateway.
- Creating a payment through the gateway URL appears in the dashboard.
- Secrets Manager holds the connection string and the API is confirmed to
  have consumed it (the container's environment matches the secret's value).
- `terraform destroy` removes everything, including the Docker containers.
