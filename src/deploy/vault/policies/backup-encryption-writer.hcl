path "hook2stream-kv/data/production/backup-encryption/keys/+" {
  capabilities = ["create"]
}

path "hook2stream-kv/data/production/backup-encryption/current" {
  capabilities = ["create", "read", "update"]
}

path "hook2stream-kv/metadata/production/backup-encryption/current" {
  capabilities = ["read"]
}
