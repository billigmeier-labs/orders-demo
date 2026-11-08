resource "aws_appconfig_application" "orders" {
  name        = "orders-demo"
  description = "Feature flags for the orders demo"
}

resource "aws_appconfig_environment" "this" {
  for_each       = toset(["prod", "staging", "preview"])
  name           = each.key
  application_id = aws_appconfig_application.orders.id
}

resource "aws_appconfig_configuration_profile" "flags" {
  application_id = aws_appconfig_application.orders.id
  name           = "feature-flags"
  location_uri   = "hosted"
  type           = "AWS.AppConfig.FeatureFlags"
}

locals {
  percent_attribute = {
    percent = {
      constraints = { type = "number", minimum = 0, maximum = 100 }
    }
  }
}

resource "aws_appconfig_hosted_configuration_version" "flags" {
  application_id           = aws_appconfig_application.orders.id
  configuration_profile_id = aws_appconfig_configuration_profile.flags.configuration_profile_id
  content_type             = "application/json"
  description              = "Initial flags (all off)"

  content = jsonencode({
    version = "1"
    flags = {
      searchV2        = { name = "searchV2", description = "New search implementation" }
      faultSlowSearch = { name = "faultSlowSearch", description = "Fault: slow down a percentage of searches", attributes = local.percent_attribute }
      faultFailOrders = { name = "faultFailOrders", description = "Fault: fail a percentage of order creates with 500", attributes = local.percent_attribute }
    }
    values = {
      searchV2        = { enabled = false }
      faultSlowSearch = { enabled = false, percent = 20 }
      faultFailOrders = { enabled = false, percent = 20 }
    }
  })
}

# Deploy the initial version to every environment so GetLatestConfiguration
# returns the flags instead of an empty document. Later flag flips are new
# hosted versions + deployments made outside Terraform.
resource "aws_appconfig_deployment" "initial" {
  for_each = aws_appconfig_environment.this

  application_id           = aws_appconfig_application.orders.id
  environment_id           = each.value.environment_id
  configuration_profile_id = aws_appconfig_configuration_profile.flags.configuration_profile_id
  configuration_version    = aws_appconfig_hosted_configuration_version.flags.version_number
  deployment_strategy_id   = "AppConfig.AllAtOnce"
  description              = "Initial flags"
}

# Flag toggles from the console deploy a new version each time, and AppConfig
# allows one deployment per environment at a time (bake time included). The
# predefined AllAtOnce bakes for 10 minutes; this one finishes immediately.
resource "aws_appconfig_deployment_strategy" "instant" {
  name                           = "orders-demo-instant"
  description                    = "100% immediately, no bake"
  deployment_duration_in_minutes = 0
  final_bake_time_in_minutes     = 0
  growth_factor                  = 100
  growth_type                    = "LINEAR"
  replicate_to                   = "NONE"
}
