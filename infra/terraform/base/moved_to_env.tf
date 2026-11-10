# These used to live here and now belong to the env layer, so `just env down` wipes them (spec: the
# wildcard certificate and its DNS record, AppConfig flags, and the console secret go with each demo).
# `destroy = false` only drops them from this layer's state; the env layer adopts the live resources
# with its import blocks (env/import_from_base.tf) on the first `just env up` after this change.
removed {
  from = aws_acm_certificate.demo
  lifecycle {
    destroy = false
  }
}

removed {
  from = aws_acm_certificate_validation.demo
  lifecycle {
    destroy = false
  }
}

removed {
  from = aws_route53_record.cert_validation
  lifecycle {
    destroy = false
  }
}

removed {
  from = aws_appconfig_application.orders
  lifecycle {
    destroy = false
  }
}

removed {
  from = aws_appconfig_environment.this
  lifecycle {
    destroy = false
  }
}

removed {
  from = aws_appconfig_configuration_profile.flags
  lifecycle {
    destroy = false
  }
}

removed {
  from = aws_appconfig_hosted_configuration_version.flags
  lifecycle {
    destroy = false
  }
}

removed {
  from = aws_appconfig_deployment.initial
  lifecycle {
    destroy = false
  }
}

removed {
  from = aws_appconfig_deployment_strategy.instant
  lifecycle {
    destroy = false
  }
}

removed {
  from = aws_secretsmanager_secret.console
  lifecycle {
    destroy = false
  }
}
