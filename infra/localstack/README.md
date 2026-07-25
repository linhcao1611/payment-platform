# Deploying to LocalStack with Terraform

One `terraform apply` stands up the whole platform against a local LocalStack: the dashboard
on S3 static website hosting, the API behind API Gateway, and the configuration the API boots
with held in Secrets Manager and SSM Parameter Store.

```bash
cd infra/localstack
terraform init
terraform apply
```

The outputs tell you where everything landed:

| Output | What it is |
| --- | --- |
| `dashboard_url` | The dashboard, served from S3 |
| `api_url` | The API, through API Gateway |
| `swagger_url` | Swagger UI, through the same gateway |
| `api_direct_url` | The API container's own port, bypassing the gateway |
| `settlement_queue_url` | The SQS settlement seam (see below) |

## What this does and doesn't deploy

**It deploys:** the API, Postgres, and the dashboard.

**It does not deploy:** the observability stack. LocalStack has no meaningful
Grafana/Prometheus/Loki/Tempo analog, and running them as bare containers under Terraform
would add moving parts without adding fidelity. They stay in `docker-compose.yml` under the
`observability` profile, where they already work.

## Why compute is in Docker rather than ECS

The obvious design — ECS Fargate for the API and the dashboard, RDS for Postgres, an ALB in
front — is not buildable on this LocalStack tier.

`GET /_localstack/health` reports `"edition": "pro"` and lists `ecs`, `rds`, `elbv2` and `ecr`
as `"available"`. **That listing is not to be trusted.** A spike that tried to create an ALB, a
Fargate service and an RDS instance failed on every one of them:

```
StatusCode: 501, api error InternalFailure: Sorry, the ecs service is not
included within your LocalStack license, but is available in an upgraded license.
```

The licence is properly activated (`is_license_activated: true`); the tier simply does not
cover those services. Probed directly, this LocalStack covers:

| Available | Not licensed |
| --- | --- |
| S3, Lambda, SQS, SNS, DynamoDB | **ECS, ECR, RDS, ELBv2** |
| Secrets Manager, SSM, IAM, KMS | EKS, Batch, EFS |
| CloudWatch Logs, EventBridge, Step Functions | CloudFront, API Gateway v2 |
| API Gateway v1, EC2, Route53 | AppConfig |

So there is no container compute and no managed Postgres to deploy onto. What LocalStack *can*
own here is the edge and the config plane — and it genuinely does, rather than decoratively:
every request reaches the dashboard through S3 and the API through API Gateway, and the API
cannot start without reading its connection string out of Secrets Manager.

If ECS and RDS become available on a higher tier, the compute half of this is the part that
would be rewritten; the edge and config plane would carry over unchanged.

## Architecture

```
Browser
   │
   ├──► S3 static website ─────────────► dashboard (React dist/)
   │
   └──► API Gateway {proxy+} ──────────► host.docker.internal:5080
                                                    │
   Secrets Manager ──┐                              ▼
   SSM Parameters ───┼──► entrypoint ──────► payments-tf-api (docker)
   CloudWatch Logs   │                              │
   SQS (seam)        │                              ▼
                     └────────────────────► payments-tf-postgres (docker)
```

## How the API gets its configuration

The API container's entrypoint is overridden with a shell script that, at boot:

1. `curl`s Secrets Manager for the connection string,
2. `curl`s SSM for the demo traffic rate and the CORS origin,
3. exports them as `ConnectionStrings__Payments`, `DemoTraffic__PaymentsPerMinute` and
   `Cors__AllowedOrigins`,
4. `exec`s the app.

This costs **zero C# changes**, because ASP.NET Core's configuration binder already reads
those variable names. It's the seam `docs/production-considerations.md` describes — "real
deployment pulls them from a secrets manager into the environment" — demonstrated rather than
promised.

You can see it happen:

```bash
docker logs payments-tf-api | grep boot:
# boot: fetching configuration from LocalStack at http://host.docker.internal:4566
# boot: configuration loaded; CORS origin is http://payments-dashboard.s3-website...
```

`curl` rather than the AWS CLI because `curl` is already in the runtime image for the compose
healthcheck, so `backend/Dockerfile` stays untouched. LocalStack accepts a dummy SigV4
`Authorization` header, which is what makes that possible without implementing signing.

### The double-quote constraint

Responses are parsed with `sed`, not `jq` — the runtime image has no `jq`, and adding one
would mean editing the Dockerfile. **Config values must therefore not contain double quotes.**
Connection strings and the numeric demo settings don't. If you add a parameter whose value
might, install `jq` in the image and parse properly rather than escaping around it.

## This cannot reach real AWS

`providers.tf` pins `access_key = "test"`, `secret_key = "test"` and an explicit `endpoints`
block sending every service to `http://localhost:4566`.

That is deliberate, and it is why `tflocal` is not needed. This machine has real AWS
credentials in `~/.aws`; without those overrides a stray `terraform apply` could reach a real
account. Putting the constraint in the configuration means it is reviewable and
version-controlled rather than depending on how someone happened to invoke Terraform.

## Deliberate deviations from production practice

**Migrations run on app startup.** `ASPNETCORE_ENVIRONMENT=Development` is set, exactly as
compose does it, so the API applies migrations and seeds demo data when it boots.
`docs/production-considerations.md` correctly says migrations should be a pipeline-gated
deploy step so a rolling deploy can't have two versions racing to migrate. A separate one-shot
migration container would honour that; it is not done here because Terraform cannot cleanly
sequence it, and the goal is a working local deploy rather than a faithful release pipeline.

**The SQS queue is a seam, not an integration.** Nothing publishes to it and nothing reads
from it. `README.md` and `docs/tradeoffs.md` both describe the Postgres outbox as a deliberate
step toward a real broker; the queue exists so that step has somewhere to land. Don't mistake
its presence for a working queue.

**Single instance, no load balancer.** ELBv2 isn't licensed, so there's one API container. The
multi-replica behaviour the outbox design tolerates isn't exercised here.

**CORS is on.** The compose deployment serves dashboard and API from one origin and needs no
CORS; this one spans two origins and configures it explicitly. The policy is off unless
`Cors:AllowedOrigins` is set, so the compose path is unaffected.

## Known limitations

**LocalStack persistence is disabled.** Restarting LocalStack wipes every AWS-side resource
while Terraform state still claims they exist, and the Postgres volume survives independently
— so the two drift apart. After a LocalStack restart:

```bash
terraform destroy   # may error on already-gone resources; that's expected
terraform apply
```

If destroy can't reconcile, `terraform state rm` the orphaned AWS resources and re-apply.

**API Gateway adds a hop** that compose doesn't have, so latency measured through `api_url`
isn't comparable to compose numbers. Use `api_direct_url` to measure the API itself.

**The API Gateway id changes** whenever the REST API is recreated, which changes `api_url` and
therefore rebuilds the dashboard bundle. That's correct behaviour, not a bug — the bundle has
the gateway URL compiled into it.

## Coexisting with docker compose

Both stacks can run at once. This deployment uses a `-tf-` infix on every container name and
different host ports:

| | compose | terraform |
| --- | --- | --- |
| Postgres | `payments-postgres`, port 5433 | `payments-tf-postgres`, port 5434 |
| API | `payments-api`, port 5080 | `payments-tf-api`, port 5080 |
| Dashboard | `payments-dashboard`, port 5173 | S3 website on 4566 |

The API port is the one genuine collision: both publish on 5080. Run one or the other, or set
`-var api_host_port=5081` — the gateway integration follows the variable.

## Prerequisites

- LocalStack running (`localstack start -d`)
- Docker
- AWS CLI v2 — used by the dashboard build step for `s3 sync`
- Node — the dashboard is built on the host
- The .NET SDK is **not** needed; the API image builds it in a container

## Tearing down

```bash
terraform destroy
```

Removes the containers, the network, the Postgres volume and every AWS-side resource.
