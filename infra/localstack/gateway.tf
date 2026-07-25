# API Gateway v1, not v2: apigatewayv2 is not licensed on this LocalStack tier and fails with a
# 501 at apply time. Neither is ELBv2, which is why this is the edge rather than an ALB.
resource "aws_api_gateway_rest_api" "api" {
  name        = "${var.project}-api"
  description = "Fronts the payments API container."
}

# A single greedy proxy resource: this gateway is a transparent front door, not a second place
# to declare routes. The API already owns its routing, and duplicating it here would mean every
# new endpoint needed a Terraform change too.
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

# The root path too, so the Swagger redirect at / works through the gateway.
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
  # deployment forever, and integration edits appear to do nothing.
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
  # The virtual-host form LocalStack serves API Gateway on. The stage's own invoke_url attribute
  # points at execute-api.amazonaws.com, which is not reachable from here.
  api_base_url = "http://${aws_api_gateway_rest_api.api.id}.execute-api.localhost.localstack.cloud:4566/${aws_api_gateway_stage.local.stage_name}"
}
