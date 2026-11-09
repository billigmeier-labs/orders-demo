resource "aws_security_group" "alb" {
  name        = "${local.name}-alb"
  description = "Public HTTP/HTTPS to the ALB"
  vpc_id      = var.vpc_id
}

resource "aws_vpc_security_group_ingress_rule" "alb" {
  for_each          = toset(["80", "443"])
  security_group_id = aws_security_group.alb.id
  ip_protocol       = "tcp"
  from_port         = tonumber(each.key)
  to_port           = tonumber(each.key)
  cidr_ipv4         = "0.0.0.0/0"
}

resource "aws_vpc_security_group_egress_rule" "alb_to_tasks" {
  security_group_id            = aws_security_group.alb.id
  ip_protocol                  = "tcp"
  from_port                    = 8080
  to_port                      = 8080
  referenced_security_group_id = aws_security_group.tasks.id
}

resource "aws_vpc_security_group_egress_rule" "alb_to_previews" {
  security_group_id            = aws_security_group.alb.id
  ip_protocol                  = "tcp"
  from_port                    = 8080
  to_port                      = 8080
  referenced_security_group_id = aws_security_group.previews.id
}

# Code from main: prod, staging and console services, their migrations and the load generator.
# (The description still says previews: changing it would replace the group. Previews moved to
# orders-demo-previews below.)
resource "aws_security_group" "tasks" {
  name        = "${local.name}-tasks"
  description = "ECS tasks (services, previews, migrate, loadgen)"
  vpc_id      = var.vpc_id
}

resource "aws_vpc_security_group_ingress_rule" "tasks_from_alb" {
  security_group_id            = aws_security_group.tasks.id
  ip_protocol                  = "tcp"
  from_port                    = 8080
  to_port                      = 8080
  referenced_security_group_id = aws_security_group.alb.id
}

# Tasks pull images, call AWS APIs, GitHub and the ALB over the internet (no NAT).
resource "aws_vpc_security_group_egress_rule" "tasks_all" {
  security_group_id = aws_security_group.tasks.id
  ip_protocol       = "-1"
  cidr_ipv4         = "0.0.0.0/0"
}

# Production database: only tasks in the main-code group (orders-demo-tasks). The resource keeps its
# old name and description (changing the description would replace the group).
resource "aws_security_group" "db" {
  name        = "${local.name}-db"
  description = "PostgreSQL from ECS tasks only"
  vpc_id      = var.vpc_id
}

resource "aws_vpc_security_group_ingress_rule" "db_from_tasks" {
  security_group_id            = aws_security_group.db.id
  ip_protocol                  = "tcp"
  from_port                    = 5432
  to_port                      = 5432
  referenced_security_group_id = aws_security_group.tasks.id
}

# PR previews run code nobody has reviewed yet, so they get their own group: from the ALB, out to the
# internet (image pulls, AWS APIs; no NAT) and into the nonprod database only. The prod database's
# group never references it.
resource "aws_security_group" "previews" {
  name        = "${local.name}-previews"
  description = "PR preview tasks (pr-N services and their migrations)"
  vpc_id      = var.vpc_id
}

resource "aws_vpc_security_group_ingress_rule" "previews_from_alb" {
  security_group_id            = aws_security_group.previews.id
  ip_protocol                  = "tcp"
  from_port                    = 8080
  to_port                      = 8080
  referenced_security_group_id = aws_security_group.alb.id
}

resource "aws_vpc_security_group_egress_rule" "previews_all" {
  security_group_id = aws_security_group.previews.id
  ip_protocol       = "-1"
  cidr_ipv4         = "0.0.0.0/0"
}

# Nonprod database (staging and previews): main-code tasks (staging service and migration) and previews.
resource "aws_security_group" "db_nonprod" {
  name        = "${local.name}-db-nonprod"
  description = "PostgreSQL (nonprod) from staging and preview tasks"
  vpc_id      = var.vpc_id
}

resource "aws_vpc_security_group_ingress_rule" "db_nonprod" {
  for_each = {
    tasks    = aws_security_group.tasks.id
    previews = aws_security_group.previews.id
  }
  security_group_id            = aws_security_group.db_nonprod.id
  ip_protocol                  = "tcp"
  from_port                    = 5432
  to_port                      = 5432
  referenced_security_group_id = each.value
}
