locals {
  frontend_dir = abspath("${path.module}/${var.repo_root}/frontend")

  # dist/ and node_modules/ are excluded: they are build outputs and inputs-of-inputs, and
  # including either would make the hash change on every build.
  frontend_source_files = [
    for f in setunion(
      fileset(local.frontend_dir, "src/**"),
      fileset(local.frontend_dir, "public/**"),
      fileset(local.frontend_dir, "index.html"),
      fileset(local.frontend_dir, "package.json"),
      fileset(local.frontend_dir, "package-lock.json"),
      fileset(local.frontend_dir, "vite.config.ts"),
      fileset(local.frontend_dir, "tsconfig*.json"),
    ) : f if !strcontains(f, "node_modules/")
  ]

  frontend_hash = sha1(join("", [
    for f in local.frontend_source_files : filesha1("${local.frontend_dir}/${f}")
  ]))

  # Defined here rather than in outputs.tf because config.tf needs it for the CORS parameter,
  # and duplicating the URL construction in two places is how they drift apart.
  dashboard_url = "http://${aws_s3_bucket.dashboard.bucket}.s3-website.localhost.localstack.cloud:4566"
}

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
# The reason is ordering. The bundle has to be built with VITE_API_BASE set to the API Gateway
# URL, and that URL only exists once apply is under way. aws_s3_object with fileset() evaluates
# at plan time, when dist/ either doesn't exist or holds a bundle built against the wrong URL.
# Running the build and then `aws s3 sync` sidesteps that, and sync infers content types itself.
#
# Requires the AWS CLI and Node on the host.
resource "terraform_data" "dashboard_bundle" {
  triggers_replace = {
    api_base_url = local.api_base_url
    bucket       = aws_s3_bucket.dashboard.bucket
    source       = local.frontend_hash
  }

  provisioner "local-exec" {
    working_dir = local.frontend_dir

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
