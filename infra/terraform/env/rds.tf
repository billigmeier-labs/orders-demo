resource "aws_db_subnet_group" "this" {
  name       = local.name
  subnet_ids = local.subnet_ids
}

resource "aws_db_instance" "this" {
  for_each = toset(["prod", "nonprod"])

  identifier     = "orders-${each.key}"
  engine         = "postgres"
  engine_version = "17"
  instance_class = "db.t4g.micro"

  allocated_storage = 20
  storage_type      = "gp3"
  storage_encrypted = true

  db_name                     = "orders"
  username                    = "orders"
  manage_master_user_password = true

  db_subnet_group_name   = aws_db_subnet_group.this.name
  vpc_security_group_ids = [each.key == "prod" ? aws_security_group.db.id : aws_security_group.db_nonprod.id]
  publicly_accessible    = false
  multi_az               = false

  backup_retention_period      = 0
  skip_final_snapshot          = true
  deletion_protection          = false
  apply_immediately            = true
  auto_minor_version_upgrade   = true
  performance_insights_enabled = false

  # nonprod moves to db_nonprod: its ingress rules must exist first so staging never loses access.
  depends_on = [aws_vpc_security_group_ingress_rule.db_nonprod]
}

locals {
  # Npgsql reads the password from PGPASSWORD (injected from the RDS-managed
  # secret below), so the connection string itself carries no secret.
  connection_strings = {
    for k, db in aws_db_instance.this :
    k => "Host=${db.address};Port=${db.port};Database=${db.db_name};Username=${db.username};SSL Mode=Require;Maximum Pool Size=20"
  }
  db_password_secret = {
    for k, db in aws_db_instance.this : k => "${db.master_user_secret[0].secret_arn}:password::"
  }
}
