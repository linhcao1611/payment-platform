locals {
  backend_dir = abspath("${path.module}/${var.repo_root}/backend")

  # Tests, obj/ and bin/ are excluded deliberately. The generated files under obj/ change on
  # every build, so including them would make the hash unstable and rebuild the image on every
  # single apply.
  backend_source_files = [
    for f in setunion(
      fileset(local.backend_dir, "src/**/*.cs"),
      fileset(local.backend_dir, "src/**/*.csproj"),
      fileset(local.backend_dir, "src/**/*.json"),
      fileset(local.backend_dir, "Directory.Build.props"),
      fileset(local.backend_dir, "Dockerfile"),
    ) : f if !strcontains(f, "/obj/") && !strcontains(f, "/bin/")
  ]

  backend_hash = sha1(join("", [
    for f in local.backend_source_files : filesha1("${local.backend_dir}/${f}")
  ]))
}

# Built from the repo's own Dockerfile, unmodified. Rebuilt whenever a source file changes, so
# editing code and re-applying actually redeploys.
resource "docker_image" "api" {
  name = "${var.project}-api:tf"

  build {
    context    = local.backend_dir
    dockerfile = "Dockerfile"
  }

  triggers = {
    source = local.backend_hash
  }
}

# The entrypoint fetches configuration from LocalStack at boot, exports it as the environment
# variables the app already reads, then execs the app. This is the whole point of the config
# plane: the secret is genuinely consumed rather than decorative — and it costs zero C#
# changes, because ASP.NET Core's configuration binder already reads these variable names.
#
# curl rather than the AWS CLI: curl is already in the runtime image for the compose
# healthcheck, so backend/Dockerfile stays untouched. LocalStack accepts a dummy SigV4
# Authorization header, which is what makes this possible without a signing implementation.
#
# sed rather than jq: the runtime image has no jq and adding one would mean editing the
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

    # Fail loudly rather than falling through to appsettings' localhost connection string,
    # which would start fine and then be mystifying: the app would be up, pointed at nothing.
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
    # Swagger. production-considerations.md correctly calls for a pipeline-gated migration job
    # instead; this is a deliberate, documented deviation for a local demo.
    "ASPNETCORE_ENVIRONMENT=Development",
    "FakeGateway__AuthorizeDeclineRate=0",
    "Demo__Seed=true",
    "Demo__Days=7",
    "Demo__PaymentsPerDay=70",
    "DemoTraffic__Enabled=true",
    # OTEL_EXPORTER_OTLP_ENDPOINT is deliberately unset: Tempo is not part of this deployment,
    # and the app is built to no-op without it.
  ]

  networks_advanced {
    name = docker_network.payments.name
  }

  # Reaching LocalStack on the host. Docker Desktop provides this name already; the explicit
  # mapping is what makes the same configuration work on Linux.
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
