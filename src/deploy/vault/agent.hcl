vault {
  ca_cert = "/vault/tls/ca.pem"

  retry {
    num_retries = 12
  }
}

auto_auth {
  method "approle" {
    mount_path  = "auth/approle"
    exit_on_err = true

    config = {
      role_id_file_path                   = "/vault/auth/role_id"
      secret_id_file_path                 = "/vault/auth/secret_id"
      remove_secret_id_file_after_reading = false
    }
  }
}

template_config {
  exit_on_retry_failure = true
}

template {
  source               = "/vault/templates/foundation.json.ctmpl"
  destination          = "/vault/rendered/foundation.json"
  create_dest_dirs     = false
  error_on_missing_key = true
  backup               = false
  perms                = "0600"
}

template {
  source               = "/vault/templates/runtime-s3.json.ctmpl"
  destination          = "/vault/rendered/runtime-s3.json"
  create_dest_dirs     = false
  error_on_missing_key = true
  backup               = false
  perms                = "0600"
}

template {
  source               = "/vault/templates/api.json.ctmpl"
  destination          = "/vault/rendered/api.json"
  create_dest_dirs     = false
  error_on_missing_key = true
  backup               = false
  perms                = "0600"
}

template {
  source               = "/vault/templates/control.json.ctmpl"
  destination          = "/vault/rendered/control.json"
  create_dest_dirs     = false
  error_on_missing_key = true
  backup               = false
  perms                = "0600"
}

template {
  source               = "/vault/templates/backup-s3.json.ctmpl"
  destination          = "/vault/rendered/backup-s3.json"
  create_dest_dirs     = false
  error_on_missing_key = true
  backup               = false
  perms                = "0600"
}

template {
  source               = "/vault/templates/media-security.json.ctmpl"
  destination          = "/vault/rendered/media-security.json"
  create_dest_dirs     = false
  error_on_missing_key = true
  backup               = false
  perms                = "0600"
}

template {
  source               = "/vault/templates/backup-encryption.json.ctmpl"
  destination          = "/vault/rendered/backup-encryption.json"
  create_dest_dirs     = false
  error_on_missing_key = true
  backup               = false
  perms                = "0600"
}
