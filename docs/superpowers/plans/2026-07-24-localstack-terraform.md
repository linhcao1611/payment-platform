# LocalStack Terraform Deploy Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A single `terraform apply` that deploys the payment platform against a local LocalStack — dashboard on S3 static website hosting, API behind API Gateway, config in Secrets Manager and SSM, with Postgres and the API itself running as Terraform-managed Docker containers.

**Architecture:** LocalStack owns the edge (S3 website, API Gateway) and the config plane (Secrets Manager, SSM, CloudWatch Logs, SQS). Compute stays in Docker because this LocalStack tier does not license ECS or RDS. The `aws` and `kreuzwerker/docker` providers live in one root module so a single apply does everything. The API's container command is overridden with a shell entrypoint that `curl`s its configuration out of LocalStack at boot and exports it as the environment variables the app already reads — so no C# configuration code changes.

**Tech Stack:** Terraform ~1.x, `hashicorp/aws` ~> 5.0, `kreuzwerker/docker` ~> 3.0, Docker, AWS CLI v2 (host-side, for `s3 sync`), .NET 10, Vite/React.

## Global Constraints

- **Never touch real AWS.** The `aws` provider MUST pin `access_key = "test"`, `secret_key = "test"` and explicit `endpoints` for every service used, all pointing at `http://localhost:4566`. The machine has real credentials in `~/.aws`; this is the only thing preventing a plan from reaching them. Do not rely on `tflocal`.
- **Do not modify `docker-compose.yml`, `frontend/nginx.conf`, or `backend/Dockerfile`.** This work adds a deployment path; it does not replace or alter the existing one.
- **Services NOT licensed on this LocalStack — never use them:** ECS, ECR, RDS, ELBv2/ALB, EKS, Batch, EFS, CloudFront, API Gateway v2 (`apigatewayv2`), AppConfig. They return `501` at apply time. Use API Gateway **v1** (`aws_api_gateway_*`) only.
- **Config values must not contain double quotes.** The entrypoint parses JSON with `sed`, not `jq`.
- **Region is `us-east-1`** everywhere. Account id is `000000000000`.
- All Terraform lives in `infra/localstack/`. Only two files outside it change: `frontend/src/api/client.ts` and `backend/src/Payments.Api/Program.cs`.
- Default `VITE_API_BASE` to `''` so the compose and `npm run dev` paths keep working unchanged.
- CORS must be **off unless configured**, so the compose path (same origin) is unaffected.

---

### Task 1: Frontend honours a configurable API base URL

**Files:**
- Modify: `frontend/src/api/client.ts:12`
- Test: `frontend/src/api/client.test.ts` (create)

**Interfaces:**
- Produces: `BASE_URL` resolved as `` `${import.meta.env.VITE_API_BASE ?? ''}/api` ``. Task 8 sets `VITE_API_BASE` at build time to the API Gateway stage URL.

- [ ] **Step 1: Write the failing test**

Create `frontend/src/api/client.test.ts`:

```ts
import { afterEach, describe, expect, it, vi } from 'vitest'
import { listPayments } from './client'

function mockFetchOk() {
  const fetchMock = vi.fn().mockResolvedValue({
    ok: true,
    json: async () => ({ items: [], page: 1, pageSize: 20, total: 0 }),
  })
  vi.stubGlobal('fetch', fetchMock)
  return fetchMock
}

describe('api base url', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
    vi.unstubAllEnvs()
    vi.resetModules()
  })

  it('calls a relative /api path when VITE_API_BASE is unset', async () => {
    vi.stubEnv('VITE_API_BASE', '')
    const fetchMock = mockFetchOk()
    const { listPayments: freshListPayments } = await import('./client')
    await freshListPayments({})
    expect(fetchMock.mock.calls[0][0]).toBe('/api/payments')
  })

  it('prefixes requests with VITE_API_BASE when it is set', async () => {
    vi.stubEnv('VITE_API_BASE', 'http://gateway.example/local')
    const fetchMock = mockFetchOk()
    vi.resetModules()
    const { listPayments: freshListPayments } = await import('./client')
    await freshListPayments({})
    expect(fetchMock.mock.calls[0][0]).toBe('http://gateway.example/local/api/payments')
  })

  it('still sends the merchant header', async () => {
    const fetchMock = mockFetchOk()
    await listPayments({})
    expect(fetchMock.mock.calls[0][1].headers['X-Merchant-Id']).toBe('acme')
  })
})
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `cd frontend && npx vitest run src/api/client.test.ts`
Expected: FAIL — the second test gets `/api/payments` instead of the prefixed URL.

- [ ] **Step 3: Make the base URL configurable**

In `frontend/src/api/client.ts`, replace line 12:

```ts
const BASE_URL = '/api'
```

with:

```ts
// Empty by default, which keeps the relative `/api` path the compose (nginx) and
// `npm run dev` (Vite proxy) paths both rely on. The LocalStack deploy serves the
// dashboard from S3, where there is no proxy in front of it, so Terraform builds
// with VITE_API_BASE set to the API Gateway stage URL and the same code reaches
// across origins instead.
const BASE_URL = `${import.meta.env.VITE_API_BASE ?? ''}/api`
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `cd frontend && npx vitest run src/api/client.test.ts`
Expected: PASS (3 tests)

- [ ] **Step 5: Verify the rest of the frontend is unaffected**

Run: `cd frontend && npx vitest run && npm run lint && npm run build`
Expected: all tests pass, no lint errors, build succeeds.

- [ ] **Step 6: Commit**

```bash
git add frontend/src/api/client.ts frontend/src/api/client.test.ts
git commit -m "Allow the dashboard's API base URL to be set at build time"
```

---

### Task 2: API serves CORS when, and only when, origins are configured

**Files:**
- Modify: `backend/src/Payments.Api/Program.cs` (add near line 96, and near line 153)
- Test: `backend/tests/Payments.Api.Tests/CorsTests.cs` (create)

**Interfaces:**
- Consumes: `PaymentsApiFixture.CreateHost(string connectionString, bool workerEnabled, IReadOnlyDictionary<string, string?>? extraConfig)` and `PaymentsApiFixture.CreateIsolatedDatabaseAsync()`, both existing.
- Produces: config key `Cors:AllowedOrigins` — a comma-separated origin list. Environment-variable form `Cors__AllowedOrigins`. Task 6 sets it via SSM.

- [ ] **Step 1: Write the failing test**

Create `backend/tests/Payments.Api.Tests/CorsTests.cs`:

```csharp
using System.Collections.Generic;
using System.Net.Http;

namespace Payments.Api.Tests;

/// <summary>
/// The compose path serves the dashboard and the API from one origin, so CORS is off by
/// default and must stay off — turning it on unconditionally would loosen the compose
/// deployment for no reason. The LocalStack deploy serves the dashboard from S3, a genuinely
/// different origin, so it configures origins explicitly.
/// </summary>
[Collection(nameof(PaymentsApiCollection))]
public sealed class CorsTests(PaymentsApiFixture fixture)
{
    private const string Origin = "http://dashboard.example";

    [Fact]
    public async Task Allowed_origin_gets_cors_headers()
    {
        var connectionString = await fixture.CreateIsolatedDatabaseAsync();
        using var host = fixture.CreateHost(connectionString, workerEnabled: false,
            new Dictionary<string, string?> { ["Cors:AllowedOrigins"] = Origin });
        using var client = host.CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/payments");
        request.Headers.Add("Origin", Origin);
        request.Headers.Add("X-Merchant-Id", "acme");

        var response = await client.SendAsync(request);

        Assert.Equal(
            Origin,
            Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
    }

    [Fact]
    public async Task Unconfigured_host_sends_no_cors_headers()
    {
        var connectionString = await fixture.CreateIsolatedDatabaseAsync();
        using var host = fixture.CreateHost(connectionString, workerEnabled: false);
        using var client = host.CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/payments");
        request.Headers.Add("Origin", Origin);
        request.Headers.Add("X-Merchant-Id", "acme");

        var response = await client.SendAsync(request);

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `cd backend && dotnet test tests/Payments.Api.Tests --filter FullyQualifiedName~CorsTests`
Expected: `Allowed_origin_gets_cors_headers` FAILS (no `Access-Control-Allow-Origin` header). The second test passes already — that is correct, it is the regression guard.

- [ ] **Step 3: Register the CORS policy, gated on configuration**

In `backend/src/Payments.Api/Program.cs`, immediately **before** the line `builder.Services.AddEndpointsApiExplorer();` (currently line 95), insert:

```csharp
// Off unless configured, and deliberately so. The compose deployment serves the dashboard
// and the API from one origin through nginx, so it needs no CORS at all and shouldn't get a
// relaxed policy it never asked for. The LocalStack deploy puts the dashboard on an S3
// website and the API behind API Gateway — genuinely different origins — and sets this.
var corsOrigins = builder.Configuration["Cors:AllowedOrigins"]
    ?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    ?? [];

if (corsOrigins.Length > 0)
{
    builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy
        .WithOrigins(corsOrigins)
        // The dashboard sends X-Merchant-Id and Idempotency-Key, and uses POST for capture
        // and refund. Credentials are not allowed: this API authenticates by header, not
        // cookie, so there is nothing for the browser to attach.
        .AllowAnyHeader()
        .AllowAnyMethod()));
}
```

- [ ] **Step 4: Add the middleware, also gated**

In the same file, immediately **before** `app.MapControllers();` (currently line 154), insert:

```csharp
// Gated on the same condition as the registration above: UseCors throws if no policy was
// ever added, so the two must agree.
if (corsOrigins.Length > 0)
{
    app.UseCors();
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `cd backend && dotnet test tests/Payments.Api.Tests --filter FullyQualifiedName~CorsTests`
Expected: PASS (2 tests)

- [ ] **Step 6: Run the full backend suite to confirm nothing regressed**

Run: `cd backend && dotnet test`
Expected: all tests pass. Pay attention to `PaymentTracingTests` and `DemoTrafficPauseTests`, which build their own hosts.

- [ ] **Step 7: Commit**

```bash
git add backend/src/Payments.Api/Program.cs backend/tests/Payments.Api.Tests/CorsTests.cs
git commit -m "Add an opt-in CORS policy for cross-origin dashboard deployments"
```

---

### Task 3: Terraform scaffolding and provider safety

**Files:**
- Create: `infra/localstack/providers.tf`, `infra/localstack/variables.tf`, `infra/localstack/README.md`
- Modify: `.gitignore`

**Interfaces:**
- Produces: variables `aws_region` (default `us-east-1`), `localstack_endpoint` (default `http://localhost:4566`), `project` (default `payments`), `repo_root` (default `../..`), `db_user`, `db_password`, `db_name`, `api_host_port` (default `5080`). Every later task consumes these.

- [ ] **Step 1: Create the provider configuration**

Create `infra/localstack/providers.tf`:

```hcl
terraform {
  required_version = ">= 1.5"

  required_providers {
    aws = {
      source  = "hashicorp/aws"
      version = "~> 5.0"
    }
    docker = {
      source  = "kreuzwerker/docker"
      version = "~> 3.0"
    }
  }
}

# Every endpoint is pinned at LocalStack and the credentials are dummies. This machine has
# real AWS credentials in ~/.aws, and without these overrides a stray apply would reach a
# real account. This is why `tflocal` is not required: the safety lives in the config, where
# it can be reviewed, rather than in how someone happened to invoke Terraform.
provider "aws" {
  region                      = var.aws_region
  access_key                  = "test"
  secret_key                  = "test"
  s3_use_path_style           = true
  skip_credentials_validation = true
  skip_metadata_api_check     = true
  skip_requesting_account_id  = true

  default_tags {
    tags = {
      Project   = var.project
      ManagedBy = "terraform"
    }
  }

  endpoints {
    apigateway     = var.localstack_endpoint
    cloudwatch     = var.localstack_endpoint
    ec2            = var.localstack_endpoint
    iam            = var.localstack_endpoint
    logs           = var.localstack_endpoint
    s3             = var.localstack_endpoint
    secretsmanager = var.localstack_endpoint
    sqs            = var.localstack_endpoint
    ssm            = var.localstack_endpoint
    sts            = var.localstack_endpoint
  }
}

provider "docker" {}
```

- [ ] **Step 2: Create the variables**

Create `infra/localstack/variables.tf`:

```hcl
variable "aws_region" {
  description = "Region used for every LocalStack resource."
  type        = string
  default     = "us-east-1"
}

variable "localstack_endpoint" {
  description = "LocalStack edge endpoint, as reached from the Terraform host."
  type        = string
  default     = "http://localhost:4566"
}

variable "localstack_endpoint_from_container" {
  description = <<-EOT
    LocalStack edge endpoint as reached from inside a container. Docker Desktop maps the host
    gateway to host.docker.internal; the API container gets an explicit extra_hosts entry so
    this resolves on Linux too.
  EOT
  type        = string
  default     = "http://host.docker.internal:4566"
}

variable "project" {
  description = "Name prefix for every resource."
  type        = string
  default     = "payments"
}

variable "repo_root" {
  description = "Path to the repository root, relative to this module."
  type        = string
  default     = "../.."
}

variable "db_name" {
  description = "Postgres database name."
  type        = string
  default     = "payments"
}

variable "db_user" {
  description = "Postgres user."
  type        = string
  default     = "payments"
}

variable "db_password" {
  description = "Postgres password. Local-only, and never a real secret — the whole stack is a demo on localhost."
  type        = string
  default     = "payments_dev"
  sensitive   = true
}

variable "api_host_port" {
  description = "Host port the API container publishes on. API Gateway proxies to this."
  type        = number
  default     = 5080
}

variable "postgres_host_port" {
  description = "Host port for Postgres. Defaults off the compose 5433 so both can run at once."
  type        = number
  default     = 5434
}

variable "demo_payments_per_minute" {
  description = "Demo traffic generator rate, stored in SSM and read by the API at boot."
  type        = number
  default     = 10
}
```

- [ ] **Step 3: Ignore Terraform state**

Append to `.gitignore`:

```gitignore
# Terraform
.terraform/
*.tfstate
*.tfstate.*
*.tfplan
crash.log
```

Note: `.terraform.lock.hcl` is deliberately NOT ignored — it pins provider versions and belongs in version control.

- [ ] **Step 4: Verify init and validate succeed**

Run: `cd infra/localstack && terraform init && terraform validate`
Expected: `Success! The configuration is valid.`

- [ ] **Step 5: Confirm the safety property**

Run: `cd infra/localstack && terraform providers`
Expected: lists `hashicorp/aws` and `kreuzwerker/docker`. Then confirm by inspection that `providers.tf` contains no reference to any host other than `var.localstack_endpoint`.

- [ ] **Step 6: Commit**

```bash
git add infra/localstack/providers.tf infra/localstack/variables.tf .gitignore
git commit -m "Add Terraform scaffolding for the LocalStack deploy"
```

---

### Task 4: Config plane — Secrets Manager, SSM, CloudWatch Logs, SQS

**Files:**
- Create: `infra/localstack/config.tf`

**Interfaces:**
- Consumes: `var.project`, `var.db_*`, `var.demo_payments_per_minute`, `docker_network.payments` (Task 5 — declared there, referenced here).
- Produces: `aws_secretsmanager_secret.db_connection` (name `payments/db-connection`), `aws_ssm_parameter.demo_rate` (`/payments/DemoTraffic/PaymentsPerMinute`), `aws_ssm_parameter.cors_origins` (`/payments/Cors/AllowedOrigins`), `aws_cloudwatch_log_group.api`, `aws_sqs_queue.settlement`. Task 6 reads the secret name and both parameter names.

- [ ] **Step 1: Create the config plane**

Create `infra/localstack/config.tf`:

```hcl
# The connection string the API boots with. production-considerations.md says "real deployment
# pulls them from a secrets manager into the environment" — this is that, demonstrated rather
# than promised. The API's entrypoint fetches this at boot and exports it as
# ConnectionStrings__Payments, which is the variable the app already reads.
#
# The host is the Postgres container's name on the shared Docker network, not localhost:
# this string is consumed inside the API container.
resource "aws_secretsmanager_secret" "db_connection" {
  name                           = "${var.project}/db-connection"
  recovery_window_in_days        = 0
  force_overwrite_replica_secret = true
}

resource "aws_secretsmanager_secret_version" "db_connection" {
  secret_id = aws_secretsmanager_secret.db_connection.id
  secret_string = join(";", [
    "Host=${docker_container.postgres.name}",
    "Port=5432",
    "Database=${var.db_name}",
    "Username=${var.db_user}",
    "Password=${var.db_password}",
  ])
}

# Non-secret runtime configuration. Split from the secret on purpose: a connection string with
# a password belongs in Secrets Manager, a traffic rate belongs in Parameter Store, and
# conflating them teaches the wrong habit.
resource "aws_ssm_parameter" "demo_rate" {
  name  = "/${var.project}/DemoTraffic/PaymentsPerMinute"
  type  = "String"
  value = tostring(var.demo_payments_per_minute)
}

# The dashboard's S3 website origin. The API turns CORS on only because this is set.
resource "aws_ssm_parameter" "cors_origins" {
  name  = "/${var.project}/Cors/AllowedOrigins"
  type  = "String"
  value = "http://${aws_s3_bucket.dashboard.bucket}.s3-website.localhost.localstack.cloud:4566"
}

resource "aws_cloudwatch_log_group" "api" {
  name              = "/${var.project}/api"
  retention_in_days = 1
}

# A seam, not an integration. README.md and tradeoffs.md both describe the Postgres outbox as
# a deliberate step toward a real broker; this is where that broker would land. Nothing
# publishes to it today and nothing reads from it — it exists so the next step has a target,
# and this comment exists so no one mistakes it for a working queue.
resource "aws_sqs_queue" "settlement" {
  name                       = "${var.project}-settlement"
  visibility_timeout_seconds = 60
}
```

- [ ] **Step 2: Verify it validates**

Run: `cd infra/localstack && terraform validate`
Expected: FAIL — `docker_container.postgres` and `aws_s3_bucket.dashboard` are not declared yet. This is expected; they arrive in Tasks 5 and 8. Confirm the errors name exactly those two resources and nothing else.

- [ ] **Step 3: Commit**

```bash
git add infra/localstack/config.tf
git commit -m "Add the LocalStack config plane: secrets, parameters, logs, settlement queue"
```

---

### Task 5: Postgres container

**Files:**
- Create: `infra/localstack/database.tf`

**Interfaces:**
- Produces: `docker_network.payments` (name `${var.project}-net`) and `docker_container.postgres` (name `${var.project}-tf-postgres`). Tasks 4 and 6 both reference these.

- [ ] **Step 1: Create the database**

Create `infra/localstack/database.tf`:

```hcl
# A dedicated network so the API can reach Postgres by container name, exactly the way it does
# under compose. Named distinctly from the compose network so both stacks can run at once.
resource "docker_network" "payments" {
  name = "${var.project}-tf-net"
}

resource "docker_image" "postgres" {
  name         = "postgres:16-alpine"
  keep_locally = true
}

# Container names carry a -tf- infix throughout so this deployment can coexist with
# `docker compose up` rather than colliding with it on names and ports.
resource "docker_container" "postgres" {
  name  = "${var.project}-tf-postgres"
  image = docker_image.postgres.image_id
  must_run = true

  env = [
    "POSTGRES_USER=${var.db_user}",
    "POSTGRES_PASSWORD=${var.db_password}",
    "POSTGRES_DB=${var.db_name}",
  ]

  networks_advanced {
    name = docker_network.payments.name
  }

  ports {
    internal = 5432
    external = var.postgres_host_port
  }

  volumes {
    volume_name    = docker_volume.pgdata.name
    container_path = "/var/lib/postgresql/data"
  }

  healthcheck {
    test     = ["CMD-SHELL", "pg_isready -U ${var.db_user} -d ${var.db_name}"]
    interval = "5s"
    timeout  = "3s"
    retries  = 10
  }

  wait = true
}

# Survives `terraform destroy` of the containers but not of the volume itself. Note this is
# independent of LocalStack's own lifecycle: restarting LocalStack wipes the AWS-side
# resources while this data stays, so the two can drift.
resource "docker_volume" "pgdata" {
  name = "${var.project}-tf-pgdata"
}
```

- [ ] **Step 2: Verify it validates**

Run: `cd infra/localstack && terraform validate`
Expected: FAIL only on `aws_s3_bucket.dashboard` (Task 8). The `docker_container.postgres` error from Task 4 is now resolved.

- [ ] **Step 3: Commit**

```bash
git add infra/localstack/database.tf
git commit -m "Add the Terraform-managed Postgres container"
```

---

### Task 6: API container with a config-fetching entrypoint

**Files:**
- Create: `infra/localstack/api.tf`

**Interfaces:**
- Consumes: `aws_secretsmanager_secret.db_connection.name`, `aws_ssm_parameter.demo_rate.name`, `aws_ssm_parameter.cors_origins.name`, `docker_network.payments`, `docker_container.postgres`.
- Produces: `docker_container.api` publishing on `var.api_host_port`. Task 7 proxies to it.

- [ ] **Step 1: Create the API deployment**

Create `infra/localstack/api.tf`:

```hcl
# Built from the repo's own Dockerfile, unmodified. The image is rebuilt whenever any source
# file changes, so a code edit followed by `terraform apply` actually redeploys.
resource "docker_image" "api" {
  name = "${var.project}-api:tf"

  build {
    context    = abspath("${path.module}/${var.repo_root}/backend")
    dockerfile = "Dockerfile"
  }

  triggers = {
    src = sha1(join("", [
      for f in fileset("${path.module}/${var.repo_root}/backend", "**/*.{cs,csproj,props}")
      : filesha1("${path.module}/${var.repo_root}/backend/${f}")
      if !startswith(f, "tests/") && !strcontains(f, "/obj/") && !strcontains(f, "/bin/")
    ]))
  }
}

# The entrypoint fetches configuration from LocalStack at boot and exports it as the
# environment variables the app already reads, then execs the app. This is the whole point of
# the config plane: the secret is genuinely consumed, not decoration — and it costs zero C#
# changes, because ASP.NET Core's configuration binder already reads these variable names.
#
# curl rather than the AWS CLI: curl is already in the runtime image for the compose
# healthcheck, so backend/Dockerfile stays untouched. LocalStack accepts a dummy SigV4
# header, which is what makes this possible at all.
#
# sed rather than jq: the runtime image has no jq, and adding one would mean editing the
# Dockerfile. The consequence is that config values must not contain double quotes.
locals {
  api_entrypoint = <<-EOT
    set -e

    LS="${var.localstack_endpoint_from_container}"
    AUTH="AWS4-HMAC-SHA256 Credential=test/20260101/${var.aws_region}/x/aws4_request, SignedHeaders=host, Signature=dummy"

    aws_post() {
      curl -s --max-time 10 -X POST "$LS/" \
        -H 'Content-Type: application/x-amz-json-1.1' \
        -H "X-Amz-Target: $1" \
        -H "Authorization: $AUTH" \
        -d "$2"
    }

    fetch_secret() {
      aws_post secretsmanager.GetSecretValue "{\"SecretId\":\"$1\"}" \
        | sed -n 's/.*"SecretString": *"\([^"]*\)".*/\1/p'
    }

    fetch_param() {
      aws_post AmazonSSM.GetParameter "{\"Name\":\"$1\"}" \
        | sed -n 's/.*"Value": *"\([^"]*\)".*/\1/p'
    }

    echo "boot: fetching configuration from LocalStack at $LS"

    ConnectionStrings__Payments="$(fetch_secret '${aws_secretsmanager_secret.db_connection.name}')"
    DemoTraffic__PaymentsPerMinute="$(fetch_param '${aws_ssm_parameter.demo_rate.name}')"
    Cors__AllowedOrigins="$(fetch_param '${aws_ssm_parameter.cors_origins.name}')"

    # Fail loudly rather than falling back to appsettings' localhost connection string, which
    # would start fine and then be mystifying: the app would be up, pointed at nothing.
    if [ -z "$ConnectionStrings__Payments" ]; then
      echo "boot: FATAL - could not read the connection string from Secrets Manager" >&2
      exit 1
    fi

    export ConnectionStrings__Payments DemoTraffic__PaymentsPerMinute Cors__AllowedOrigins

    echo "boot: configuration loaded; CORS origin is $Cors__AllowedOrigins"
    exec dotnet Payments.Api.dll
  EOT
}

resource "docker_container" "api" {
  name     = "${var.project}-tf-api"
  image    = docker_image.api.image_id
  must_run = true

  # Overrides the image's ENTRYPOINT so the config fetch happens before the app starts.
  entrypoint = ["/bin/sh", "-c", local.api_entrypoint]

  env = [
    "ASPNETCORE_URLS=http://+:8080",
    # Development on purpose, matching compose: it applies migrations on startup and serves
    # Swagger. production-considerations.md correctly calls for a pipeline-gated migration
    # job instead; this is a deliberate, documented deviation for a local demo.
    "ASPNETCORE_ENVIRONMENT=Development",
    "FakeGateway__AuthorizeDeclineRate=0",
    "Demo__Seed=true",
    "Demo__Days=7",
    "Demo__PaymentsPerDay=70",
    "DemoTraffic__Enabled=true",
    # OTEL_EXPORTER_OTLP_ENDPOINT is deliberately unset: Tempo is not part of this deployment
    # and the app is built to no-op without it.
  ]

  networks_advanced {
    name = docker_network.payments.name
  }

  # Reaching LocalStack on the host. Docker Desktop provides this name already; the explicit
  # mapping is what makes the same config work on Linux.
  host {
    host = "host.docker.internal"
    ip   = "host-gateway"
  }

  ports {
    internal = 8080
    external = var.api_host_port
  }

  healthcheck {
    test         = ["CMD-SHELL", "curl -fsS http://localhost:8080/healthz || exit 1"]
    interval     = "5s"
    timeout      = "3s"
    retries      = 12
    start_period = "15s"
  }

  wait = true

  depends_on = [
    docker_container.postgres,
    aws_secretsmanager_secret_version.db_connection,
    aws_ssm_parameter.demo_rate,
    aws_ssm_parameter.cors_origins,
  ]
}
```

- [ ] **Step 2: Verify it validates**

Run: `cd infra/localstack && terraform validate`
Expected: FAIL only on `aws_s3_bucket.dashboard` (Task 8).

- [ ] **Step 3: Commit**

```bash
git add infra/localstack/api.tf
git commit -m "Add the API container, booting from LocalStack-held configuration"
```

---

### Task 7: API Gateway in front of the API

**Files:**
- Create: `infra/localstack/gateway.tf`

**Interfaces:**
- Consumes: `var.api_host_port`, `docker_container.api`.
- Produces: `aws_api_gateway_stage.local.invoke_url` and `local.api_base_url` — the URL the dashboard is built against in Task 8. Format: `http://<rest-api-id>.execute-api.localhost.localstack.cloud:4566/local`.

- [ ] **Step 1: Create the gateway**

Create `infra/localstack/gateway.tf`:

```hcl
# API Gateway v1, not v2: apigatewayv2 is not licensed on this LocalStack tier and fails with
# a 501 at apply time.
resource "aws_api_gateway_rest_api" "api" {
  name        = "${var.project}-api"
  description = "Fronts the payments API container. There is no ALB on this LocalStack tier, so this is the edge."
}

# A single greedy proxy resource: this gateway is a transparent front door, not a place to
# re-declare the API's routes. The API already owns its routing, and duplicating it here would
# mean every new endpoint needed a Terraform change too.
resource "aws_api_gateway_resource" "proxy" {
  rest_api_id = aws_api_gateway_rest_api.api.id
  parent_id   = aws_api_gateway_rest_api.api.root_resource_id
  path_part   = "{proxy+}"
}

resource "aws_api_gateway_method" "proxy" {
  rest_api_id   = aws_api_gateway_rest_api.api.id
  resource_id   = aws_api_gateway_resource.proxy.id
  http_method   = "ANY"
  authorization = "NONE"

  request_parameters = {
    "method.request.path.proxy" = true
  }
}

resource "aws_api_gateway_integration" "proxy" {
  rest_api_id             = aws_api_gateway_rest_api.api.id
  resource_id             = aws_api_gateway_resource.proxy.id
  http_method             = aws_api_gateway_method.proxy.http_method
  type                    = "HTTP_PROXY"
  integration_http_method = "ANY"
  uri                     = "http://host.docker.internal:${var.api_host_port}/{proxy}"

  request_parameters = {
    "integration.request.path.proxy" = "method.request.path.proxy"
  }
}

# The root path too, so /healthz-style bare paths and the Swagger redirect both work.
resource "aws_api_gateway_method" "root" {
  rest_api_id   = aws_api_gateway_rest_api.api.id
  resource_id   = aws_api_gateway_rest_api.api.root_resource_id
  http_method   = "ANY"
  authorization = "NONE"
}

resource "aws_api_gateway_integration" "root" {
  rest_api_id             = aws_api_gateway_rest_api.api.id
  resource_id             = aws_api_gateway_rest_api.api.root_resource_id
  http_method             = aws_api_gateway_method.root.http_method
  type                    = "HTTP_PROXY"
  integration_http_method = "ANY"
  uri                     = "http://host.docker.internal:${var.api_host_port}/"
}

resource "aws_api_gateway_deployment" "api" {
  rest_api_id = aws_api_gateway_rest_api.api.id

  # Redeploy whenever the routing changes. Without this the stage keeps serving the first
  # deployment forever and integration edits appear to do nothing.
  triggers = {
    redeploy = sha1(jsonencode([
      aws_api_gateway_resource.proxy.id,
      aws_api_gateway_method.proxy.id,
      aws_api_gateway_integration.proxy.id,
      aws_api_gateway_method.root.id,
      aws_api_gateway_integration.root.id,
    ]))
  }

  lifecycle {
    create_before_destroy = true
  }

  depends_on = [
    aws_api_gateway_integration.proxy,
    aws_api_gateway_integration.root,
  ]
}

resource "aws_api_gateway_stage" "local" {
  rest_api_id   = aws_api_gateway_rest_api.api.id
  deployment_id = aws_api_gateway_deployment.api.id
  stage_name    = "local"
}

locals {
  # The virtual-host form LocalStack serves API Gateway on. The stage's own invoke_url points
  # at execute-api.amazonaws.com, which is not reachable here.
  api_base_url = "http://${aws_api_gateway_rest_api.api.id}.execute-api.localhost.localstack.cloud:4566/${aws_api_gateway_stage.local.stage_name}"
}
```

- [ ] **Step 2: Verify it validates**

Run: `cd infra/localstack && terraform validate`
Expected: FAIL only on `aws_s3_bucket.dashboard` (Task 8).

- [ ] **Step 3: Commit**

```bash
git add infra/localstack/gateway.tf
git commit -m "Add API Gateway as the edge in front of the API container"
```

---

### Task 8: Dashboard on S3 static website hosting

**Files:**
- Create: `infra/localstack/frontend.tf`, `infra/localstack/outputs.tf`

**Interfaces:**
- Consumes: `local.api_base_url` (Task 7), `var.repo_root`.
- Produces: `aws_s3_bucket.dashboard` — referenced by `aws_ssm_parameter.cors_origins` in Task 4. Outputs `dashboard_url`, `api_url`, `swagger_url`.

- [ ] **Step 1: Create the S3 website**

Create `infra/localstack/frontend.tf`:

```hcl
resource "aws_s3_bucket" "dashboard" {
  bucket        = "${var.project}-dashboard"
  force_destroy = true
}

resource "aws_s3_bucket_website_configuration" "dashboard" {
  bucket = aws_s3_bucket.dashboard.id

  index_document {
    suffix = "index.html"
  }

  # The dashboard routes client-side (/payments/{id}), so an unknown path must return the app
  # rather than a 404 — the same rule frontend/nginx.conf's try_files enforces under compose.
  error_document {
    key = "index.html"
  }
}

resource "aws_s3_bucket_public_access_block" "dashboard" {
  bucket = aws_s3_bucket.dashboard.id

  block_public_acls       = false
  block_public_policy     = false
  ignore_public_acls      = false
  restrict_public_buckets = false
}

resource "aws_s3_bucket_policy" "dashboard" {
  bucket = aws_s3_bucket.dashboard.id

  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [{
      Sid       = "PublicReadForWebsite"
      Effect    = "Allow"
      Principal = "*"
      Action    = "s3:GetObject"
      Resource  = "${aws_s3_bucket.dashboard.arn}/*"
    }]
  })

  depends_on = [aws_s3_bucket_public_access_block.dashboard]
}

# Build and upload in one step, driven by local-exec rather than aws_s3_object resources.
#
# The reason is ordering: the bundle has to be built with VITE_API_BASE set to the API
# Gateway URL, and that URL only exists after apply starts. aws_s3_object with fileset()
# evaluates at plan time, when dist/ either doesn't exist or holds a bundle built against the
# wrong URL. `aws s3 sync` after the build sidesteps that, and infers content types itself.
#
# Requires the AWS CLI on the host.
resource "terraform_data" "dashboard_bundle" {
  triggers_replace = {
    api_base_url = local.api_base_url
    bucket       = aws_s3_bucket.dashboard.bucket
    src = sha1(join("", [
      for f in fileset("${path.module}/${var.repo_root}/frontend", "{src/**,index.html,package.json,package-lock.json,vite.config.ts,tsconfig*.json}")
      : filesha1("${path.module}/${var.repo_root}/frontend/${f}")
    ]))
  }

  provisioner "local-exec" {
    working_dir = abspath("${path.module}/${var.repo_root}/frontend")

    environment = {
      VITE_API_BASE         = local.api_base_url
      AWS_ACCESS_KEY_ID     = "test"
      AWS_SECRET_ACCESS_KEY = "test"
      AWS_DEFAULT_REGION    = var.aws_region
      AWS_PAGER             = ""
    }

    command = <<-EOT
      set -e
      npm ci
      npm run build
      aws --endpoint-url=${var.localstack_endpoint} s3 sync dist/ s3://${aws_s3_bucket.dashboard.bucket}/ --delete
    EOT
  }

  depends_on = [
    aws_s3_bucket_policy.dashboard,
    aws_s3_bucket_website_configuration.dashboard,
  ]
}
```

- [ ] **Step 2: Create the outputs**

Create `infra/localstack/outputs.tf`:

```hcl
output "dashboard_url" {
  description = "The payments dashboard, served from S3."
  value       = "http://${aws_s3_bucket.dashboard.bucket}.s3-website.localhost.localstack.cloud:4566"
}

output "api_url" {
  description = "The API, through API Gateway."
  value       = local.api_base_url
}

output "swagger_url" {
  description = "Swagger UI, through the same gateway."
  value       = "${local.api_base_url}/swagger"
}

output "api_direct_url" {
  description = "The API container's published port, bypassing the gateway. Useful for isolating whether a failure is the gateway's."
  value       = "http://localhost:${var.api_host_port}"
}

output "settlement_queue_url" {
  description = "The SQS settlement seam. Provisioned as a target for future work; nothing publishes to it today."
  value       = aws_sqs_queue.settlement.url
}
```

- [ ] **Step 3: Verify the whole configuration validates**

Run: `cd infra/localstack && terraform init && terraform validate`
Expected: `Success! The configuration is valid.` — all cross-task references now resolve.

- [ ] **Step 4: Commit**

```bash
git add infra/localstack/frontend.tf infra/localstack/outputs.tf
git commit -m "Serve the dashboard from S3 static website hosting"
```

---

### Task 9: End-to-end verification and documentation

**Files:**
- Create: `infra/localstack/README.md`

**Interfaces:**
- Consumes: everything.

- [ ] **Step 1: Confirm LocalStack is running**

Run: `curl -s http://localhost:4566/_localstack/health | head -c 200`
Expected: JSON with `"edition": "pro"`. If this fails, start it with `localstack start -d` before continuing.

- [ ] **Step 2: Apply**

Run: `cd infra/localstack && terraform apply -auto-approve`
Expected: apply completes and prints `dashboard_url`, `api_url`, `swagger_url`, `api_direct_url`, `settlement_queue_url`.

If the API container fails its healthcheck, read its boot output first — the entrypoint logs what it fetched:
`docker logs payments-tf-api | head -30`

- [ ] **Step 3: Verify the API answers directly**

```bash
curl -s -o /dev/null -w '%{http_code}\n' http://localhost:5080/healthz
curl -s -o /dev/null -w '%{http_code}\n' http://localhost:5080/readyz
```
Expected: `200` for both. `/readyz` proves the container reached Postgres using the connection string it pulled from Secrets Manager.

- [ ] **Step 4: Verify the config really came from LocalStack**

```bash
docker logs payments-tf-api 2>&1 | grep 'boot:'
```
Expected: `boot: configuration loaded; CORS origin is http://payments-dashboard.s3-website.localhost.localstack.cloud:4566`.

This is the proof the config plane is genuinely consumed rather than decorative. If the CORS origin is empty, the SSM fetch silently failed.

- [ ] **Step 5: Verify the API answers through API Gateway**

```bash
API_URL=$(terraform output -raw api_url)
curl -s -o /dev/null -w '%{http_code}\n' "$API_URL/healthz"
curl -s "$API_URL/api/payments?pageSize=1" -H 'X-Merchant-Id: acme' | head -c 300
```
Expected: `200`, then a JSON page of payments. A non-empty `items` array also confirms the demo seeder ran.

- [ ] **Step 6: Verify the dashboard is served and points at the gateway**

```bash
DASH_URL=$(terraform output -raw dashboard_url)
curl -s -o /dev/null -w '%{http_code}\n' "$DASH_URL"
curl -s "$DASH_URL" | grep -o 'assets/[^"]*\.js' | head -1
```
Expected: `200` and an asset path. Then confirm the bundle carries the gateway URL:

```bash
ASSET=$(curl -s "$DASH_URL" | grep -o 'assets/[^"]*\.js' | head -1)
curl -s "$DASH_URL/$ASSET" | grep -c 'execute-api.localhost.localstack.cloud'
```
Expected: a count of `1` or more. Zero means `VITE_API_BASE` did not reach the build.

- [ ] **Step 7: Verify CORS is actually working**

```bash
API_URL=$(terraform output -raw api_url)
DASH_URL=$(terraform output -raw dashboard_url)
curl -s -D - -o /dev/null "$API_URL/api/payments?pageSize=1" \
  -H "Origin: $DASH_URL" -H 'X-Merchant-Id: acme' | grep -i 'access-control-allow-origin'
```
Expected: `access-control-allow-origin: http://payments-dashboard.s3-website.localhost.localstack.cloud:4566`

- [ ] **Step 8: Open the dashboard and confirm it loads data**

Open the `dashboard_url` in a browser. Expected: the payments table renders with seeded rows, and clicking a payment opens its detail page. If the table is empty but the API returns data, check the browser console for a CORS error.

- [ ] **Step 9: Verify idempotency of apply**

Run: `cd infra/localstack && terraform apply -auto-approve`
Expected: `No changes. Your infrastructure matches the configuration.` A second apply that wants to rebuild the image or re-run the bundle means a trigger hash is unstable — fix it before continuing.

- [ ] **Step 10: Write the README**

Create `infra/localstack/README.md` documenting:
- **What this deploys and what it doesn't** — the ECS/RDS/ALB tier limitation, discovered by spike, and why compute is in Docker. Include the licensed/unlicensed table from the design doc.
- **Prerequisites** — LocalStack running, Docker, AWS CLI v2, Node, and that the .NET SDK is *not* needed (the image builds it).
- **Usage** — `terraform init && terraform apply`, then the output URLs.
- **The safety property** — dummy credentials and pinned endpoints mean this cannot reach real AWS, and why `tflocal` is therefore unnecessary.
- **Deliberate deviations** — `ASPNETCORE_ENVIRONMENT=Development` so migrations self-apply, contrary to `production-considerations.md`; the SQS queue is a seam nothing publishes to; single-instance with no load balancer.
- **Known limitations** — LocalStack persistence is disabled, so a LocalStack restart wipes the AWS-side resources while Terraform state and the Postgres volume survive. Recovery: `terraform destroy` then re-apply, or `terraform state rm` the orphaned AWS resources.
- **The double-quote constraint** on config values, and why (sed, not jq).
- **Coexistence with compose** — different container names, different ports (Postgres on 5434 vs compose's 5433), so both can run simultaneously.

- [ ] **Step 11: Verify destroy is clean**

Run: `cd infra/localstack && terraform destroy -auto-approve`
Expected: all resources destroyed. Then confirm nothing is left behind:

```bash
docker ps -a --filter name=payments-tf --format '{{.Names}}'
aws --endpoint-url=http://localhost:4566 s3 ls
```
Expected: no `payments-tf-*` containers, no `payments-dashboard` bucket.

- [ ] **Step 12: Re-apply to confirm the whole thing works from cold**

Run: `cd infra/localstack && terraform apply -auto-approve`
Expected: completes, and Steps 3–7 all pass again.

- [ ] **Step 13: Commit**

```bash
git add infra/localstack/README.md
git commit -m "Document the LocalStack Terraform deploy"
```

---

## Self-Review

**Spec coverage:**

| Spec requirement | Task |
| --- | --- |
| Provider safety (dummy creds, pinned endpoints, no tflocal) | 3 |
| Secrets Manager holds connection string | 4 |
| SSM holds app config | 4 |
| CloudWatch log group | 4 |
| SQS settlement seam, documented as unconsumed | 4, 9 |
| Postgres container | 5 |
| API container, entrypoint fetches config via curl+sed | 6 |
| `host.docker.internal` extra-host mapping | 6 |
| `backend/Dockerfile` untouched | 6 (entrypoint override) |
| Migrations via `ASPNETCORE_ENVIRONMENT=Development` | 6, 9 |
| OTel dormant | 6 |
| API Gateway `{proxy+}` HTTP_PROXY | 7 |
| S3 static website, SPA fallback | 8 |
| `VITE_API_BASE` build-time injection | 1, 8 |
| CORS gated on config | 2, 4, 6 |
| Outputs for dashboard/API URLs | 8 |
| Observability stack out of scope | 9 (README) |
| Compose and nginx.conf unchanged | Global constraints |
| Known limitations documented | 9 |

No gaps.

**Placeholder scan:** No TBD/TODO. Every code step carries actual content. Step 10 of Task 9 specifies README contents as an explicit list rather than prose code, which is appropriate for documentation.

**Type consistency:**
- `Cors:AllowedOrigins` (Task 2, C# config key) ↔ `Cors__AllowedOrigins` (Task 6, env var) ↔ `/payments/Cors/AllowedOrigins` (Task 4, SSM name). Correct — ASP.NET maps `__` to `:`, and the SSM path is independent since the entrypoint does the mapping explicitly.
- `VITE_API_BASE` consistent across Tasks 1 and 8.
- `docker_container.postgres.name` referenced in Task 4's connection string is defined in Task 5. Task 4's validate step explicitly expects that forward reference to fail, and Task 5 resolves it.
- `aws_s3_bucket.dashboard.bucket` referenced in Task 4 is defined in Task 8, same pattern, and Task 8 Step 3 is where full validation finally succeeds.
- `local.api_base_url` produced in Task 7, consumed in Tasks 8's `terraform_data` and `outputs.tf`.
