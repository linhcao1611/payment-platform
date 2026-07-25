# A dedicated network so the API reaches Postgres by container name, exactly as it does under
# compose. Named distinctly from the compose network so both stacks can run side by side.
resource "docker_network" "payments" {
  name = "${var.project}-tf-net"
}

resource "docker_image" "postgres" {
  name         = "postgres:16-alpine"
  keep_locally = true
}

# Container names carry a -tf- infix throughout, and ports differ from compose's, so this
# deployment coexists with `docker compose up` rather than colliding with it.
resource "docker_container" "postgres" {
  name     = "${var.project}-tf-postgres"
  image    = docker_image.postgres.image_id
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

# Note this volume's lifecycle is independent of LocalStack's: restarting LocalStack wipes the
# AWS-side resources while this data survives, so the two can drift apart. The README says so.
resource "docker_volume" "pgdata" {
  name = "${var.project}-tf-pgdata"
}
