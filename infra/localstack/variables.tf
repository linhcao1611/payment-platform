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
    LocalStack edge endpoint as reached from inside a container. Docker Desktop resolves
    host.docker.internal already; the API container also gets an explicit host-gateway mapping
    so the same configuration works on Linux.
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
  description = "Postgres password. Local-only and never a real secret — the whole stack is a demo on localhost."
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
  description = "Host port for Postgres. Deliberately not compose's 5433, so both stacks can run at once."
  type        = number
  default     = 5434
}

variable "demo_payments_per_minute" {
  description = "Demo traffic generator rate. Stored in SSM and read by the API at boot."
  type        = number
  default     = 10
}
