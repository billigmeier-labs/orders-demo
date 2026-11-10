# The certificate for demo.joshb.app and *.demo.joshb.app (every host: prod, staging., console., pr-<N>.).
# Per demo run: `just env down` deletes it with its validation record; `up` requests a new one, and DNS
# validation finishes while RDS is still being created.
resource "aws_acm_certificate" "demo" {
  domain_name               = local.domain
  subject_alternative_names = ["*.${local.domain}"]
  validation_method         = "DNS"

  lifecycle {
    create_before_destroy = true
  }
}

# The apex and its wildcard share one validation CNAME, so create it once
# (keyed by the apex) rather than twice with allow_overwrite.
resource "aws_route53_record" "cert_validation" {
  for_each = {
    for dvo in aws_acm_certificate.demo.domain_validation_options : dvo.domain_name => dvo
    if dvo.domain_name == local.domain
  }

  zone_id         = local.base.zone_id
  name            = each.value.resource_record_name
  type            = each.value.resource_record_type
  records         = [each.value.resource_record_value]
  ttl             = 60
  allow_overwrite = true
}

resource "aws_acm_certificate_validation" "demo" {
  certificate_arn         = aws_acm_certificate.demo.arn
  validation_record_fqdns = [for r in aws_route53_record.cert_validation : r.fqdn]
}
