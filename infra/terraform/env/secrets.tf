# Console secrets as one JSON document with keys GITHUB_TOKEN,
# CONSOLE_VERIFICATION_TOKEN, GOOGLE_CLIENT_ID and GOOGLE_CLIENT_SECRET.
# Terraform creates only the container; `just env secrets` (run by `just env up`) writes the
# value from 1Password, so it never lands in Terraform state. Per demo run: no recovery window, so
# `down` deletes it at once and the next `up` can create the same name again.
resource "aws_secretsmanager_secret" "console" {
  name                    = "orders-demo/console"
  description             = "Demo console secrets (JSON); value set by `just env secrets`"
  recovery_window_in_days = 0
}
