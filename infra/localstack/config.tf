# The connection string the API boots with. production-considerations.md says "real deployment
# pulls them from a secrets manager into the environment" — this is that, demonstrated rather
# than promised. The API's entrypoint fetches this at boot and exports it as
# ConnectionStrings__Payments, the variable the app already reads.
#
# The host is the Postgres container's name on the shared Docker network, not localhost,
# because this string is consumed inside the API container.
resource "aws_secretsmanager_secret" "db_connection" {
  name                    = "${var.project}/db-connection"
  recovery_window_in_days = 0
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

# Non-secret runtime configuration, split from the secret on purpose: a connection string with
# a password belongs in Secrets Manager, a traffic rate belongs in Parameter Store, and
# conflating the two teaches the wrong habit.
resource "aws_ssm_parameter" "demo_rate" {
  name  = "/${var.project}/DemoTraffic/PaymentsPerMinute"
  type  = "String"
  value = tostring(var.demo_payments_per_minute)
}

# The dashboard's S3 website origin. The API turns CORS on only because this is set — see the
# gate in Program.cs.
resource "aws_ssm_parameter" "cors_origins" {
  name  = "/${var.project}/Cors/AllowedOrigins"
  type  = "String"
  value = local.dashboard_url
}

resource "aws_cloudwatch_log_group" "api" {
  name              = "/${var.project}/api"
  retention_in_days = 1
}

# A seam, not an integration. README.md and tradeoffs.md both describe the Postgres outbox as a
# deliberate step toward a real broker; this is where that broker would land. Nothing publishes
# to it today and nothing reads from it — it exists so the next step has a target, and this
# comment exists so nobody mistakes it for a working queue.
resource "aws_sqs_queue" "settlement" {
  name                       = "${var.project}-settlement"
  visibility_timeout_seconds = 60
}
