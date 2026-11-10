# One-time adoption of what used to be in the base layer (base/moved_to_env.tf drops it from base state
# without destroying it). `just env up` / `just env plan` set migrate_from_base=true only while the
# environment is up and this state does not have the AppConfig application yet, so a fresh `up` after a
# teardown never tries to import these (they no longer exist then). The IDs are the live ones from
# demo run 1; once `just env up` has run on this commit this file can be deleted.
locals {
  adopt = var.migrate_from_base ? toset(["adopt"]) : toset([])

  adopt_appconfig = {
    app          = "rpii8p7"
    profile      = "1frhxge"
    strategy     = "16sb2gl"
    environments = { prod = "o91moyo", staging = "sh4a1f7", preview = "fywbawk" }
    hosted       = 3
  }
}

import {
  for_each = local.adopt
  to       = aws_acm_certificate.demo
  id       = "arn:aws:acm:us-east-1:983889169993:certificate/b13f5c7d-3a49-441c-9535-00425bf6f1f3"
}

import {
  for_each = local.adopt
  to       = aws_route53_record.cert_validation["demo.joshb.app"]
  id       = "Z04795242Y2F7953D3FBP__be6a16b3ac8e93623c95321089efdd21.demo.joshb.app._CNAME"
}

import {
  for_each = local.adopt
  to       = aws_secretsmanager_secret.console
  id       = "arn:aws:secretsmanager:us-east-1:983889169993:secret:orders-demo/console-OLQftc"
}

import {
  for_each = local.adopt
  to       = aws_appconfig_application.orders
  id       = local.adopt_appconfig.app
}

import {
  for_each = local.adopt
  to       = aws_appconfig_configuration_profile.flags
  id       = "${local.adopt_appconfig.profile}:${local.adopt_appconfig.app}"
}

import {
  for_each = local.adopt
  to       = aws_appconfig_deployment_strategy.instant
  id       = local.adopt_appconfig.strategy
}

import {
  for_each = local.adopt
  to       = aws_appconfig_hosted_configuration_version.flags
  id       = "${local.adopt_appconfig.app}/${local.adopt_appconfig.profile}/${local.adopt_appconfig.hosted}"
}

import {
  for_each = var.migrate_from_base ? local.adopt_appconfig.environments : {}
  to       = aws_appconfig_environment.this[each.key]
  id       = "${each.value}:${local.adopt_appconfig.app}"
}

import {
  for_each = var.migrate_from_base ? local.adopt_appconfig.environments : {}
  to       = aws_appconfig_deployment.initial[each.key]
  id       = "${local.adopt_appconfig.app}/${each.value}/3"
}
