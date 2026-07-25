output "dashboard_url" {
  description = "The payments dashboard, served from S3 static website hosting."
  value       = local.dashboard_url
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
