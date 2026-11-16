terraform {
  required_version = ">= 1.6"

  required_providers {
    mongodbatlas = {
      source  = "mongodb/mongodbatlas"
      version = "~> 2.21"
    }
    render = {
      source  = "render-oss/render"
      version = "~> 1.9"
    }
  }
}

# Credenciales desde variables de entorno (secretos de GitHub Actions):
# MONGODB_ATLAS_PUBLIC_KEY, MONGODB_ATLAS_PRIVATE_KEY, RENDER_API_KEY y RENDER_OWNER_ID.
provider "mongodbatlas" {}

provider "render" {
  wait_for_deploy_completion = true
}

# ---------------------------------------------------------------------------
# Variables
# ---------------------------------------------------------------------------
variable "atlas_org_id" {
  description = "Organización de MongoDB Atlas donde se crea el proyecto"
  type        = string
}

variable "db_name" {
  description = "Base de datos de la aplicación"
  type        = string
  default     = "biblioteca"
}

variable "app_password" {
  description = "Contraseña del usuario de la app (lectura/escritura)"
  type        = string
  sensitive   = true
}

variable "backup_password" {
  description = "Contraseña del usuario de respaldos (solo lectura)"
  type        = string
  sensitive   = true
}

variable "guardian_token" {
  description = "Token con el que GitHub Actions registra respaldos en la app"
  type        = string
  sensitive   = true
}

variable "image_url" {
  description = "Imagen de la app publicada en GitHub Packages (ghcr.io)"
  type        = string
}

variable "image_tag" {
  type    = string
  default = "latest"
}

variable "ghcr_username" {
  type = string
}

variable "ghcr_token" {
  type      = string
  sensitive = true
}

# ---------------------------------------------------------------------------
# MongoDB Atlas: proyecto, clúster M0 (réplica de 3 nodos), usuarios y red
# ---------------------------------------------------------------------------
resource "mongodbatlas_project" "guardian" {
  name   = "mongo-backup-guardian"
  org_id = var.atlas_org_id
}

resource "mongodbatlas_advanced_cluster" "guardian" {
  project_id   = mongodbatlas_project.guardian.id
  name         = "guardian"
  cluster_type = "REPLICASET"

  replication_specs = [{
    region_configs = [{
      provider_name         = "TENANT"
      backing_provider_name = "AWS"
      region_name           = "US_EAST_1"
      priority              = 7
      electable_specs = {
        instance_size = "M0"
      }
    }]
  }]
}

# Usuario de la aplicación: lectura/escritura solo en sus dos bases.
resource "mongodbatlas_database_user" "app" {
  project_id         = mongodbatlas_project.guardian.id
  auth_database_name = "admin"
  username           = "guardian-app"
  password           = var.app_password

  roles {
    role_name     = "readWrite"
    database_name = var.db_name
  }
  roles {
    role_name     = "readWrite"
    database_name = "${var.db_name}_guardian"
  }

  scopes {
    name = mongodbatlas_advanced_cluster.guardian.name
    type = "CLUSTER"
  }
}

# Usuario de respaldos: SOLO lectura. Si su contraseña se filtra, no puede borrar nada.
resource "mongodbatlas_database_user" "backup" {
  project_id         = mongodbatlas_project.guardian.id
  auth_database_name = "admin"
  username           = "guardian-backup"
  password           = var.backup_password

  roles {
    role_name     = "read"
    database_name = var.db_name
  }

  scopes {
    name = mongodbatlas_advanced_cluster.guardian.name
    type = "CLUSTER"
  }
}

# Render (plan free) y los runners de GitHub no tienen IP fija: se abre a Internet
# y la protección queda en TLS obligatorio + usuarios con contraseña larga y mínimo privilegio.
resource "mongodbatlas_project_ip_access_list" "internet" {
  project_id = mongodbatlas_project.guardian.id
  cidr_block = "0.0.0.0/0"
  comment    = "Render free y GitHub Actions (IPs dinamicas)"
}

locals {
  srv_host = trimprefix(mongodbatlas_advanced_cluster.guardian.connection_strings.standard_srv, "mongodb+srv://")
  app_uri  = "mongodb+srv://guardian-app:${var.app_password}@${local.srv_host}/?retryWrites=true&w=majority&appName=guardian"
}

# ---------------------------------------------------------------------------
# Hosting: Web Service de contenedores en Render
# ---------------------------------------------------------------------------
resource "render_registry_credential" "ghcr" {
  name       = "ghcr-mongo-backup-guardian"
  registry   = "GITHUB"
  username   = var.ghcr_username
  auth_token = var.ghcr_token
}

resource "render_web_service" "app" {
  name              = "mongo-backup-guardian"
  plan              = "free"
  region            = "virginia"
  health_check_path = "/health"

  runtime_source = {
    image = {
      image_url              = var.image_url
      tag                    = var.image_tag
      registry_credential_id = render_registry_credential.ghcr.id
    }
  }

  env_vars = {
    "PORT"           = { value = "8080" }
    "MONGODB_URI"    = { value = local.app_uri }
    "MONGODB_DB"     = { value = var.db_name }
    "GUARDIAN_TOKEN" = { value = var.guardian_token }
  }

  lifecycle {
    # La imagen la cambia el pipeline de CI/CD, no Terraform.
    ignore_changes = [runtime_source]
  }

  depends_on = [mongodbatlas_database_user.app, mongodbatlas_project_ip_access_list.internet]
}

# ---------------------------------------------------------------------------
# Salidas
# ---------------------------------------------------------------------------
output "app_url" {
  value = render_web_service.app.url
}

output "atlas_project_id" {
  value = mongodbatlas_project.guardian.id
}

output "mongodb_srv_host" {
  value = local.srv_host
}

output "mongodb_version" {
  value = mongodbatlas_advanced_cluster.guardian.mongo_db_version
}
