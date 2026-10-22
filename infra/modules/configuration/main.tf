variable "configuration_file" {
  type        = string
  description = "Explicit owner-authored nonsecret deployment configuration; never response-derived."
  validation {
    condition     = try(length(file(var.configuration_file)) <= 32768 && can(regex("^[0-9]{12}$", jsondecode(file(var.configuration_file)).awsAccountId)) && can(regex("^[a-z]{2}-[a-z]+-[0-9]$", jsondecode(file(var.configuration_file)).awsRegion)) && can(regex("^[a-z][a-z0-9-]{1,23}$", jsondecode(file(var.configuration_file)).project)) && jsondecode(file(var.configuration_file)).steamAppId > 0 && jsondecode(file(var.configuration_file)).steamAppId <= 2147483647 && floor(jsondecode(file(var.configuration_file)).steamAppId) == jsondecode(file(var.configuration_file)).steamAppId && can(regex("^/[A-Za-z0-9_/-]+/$", jsondecode(file(var.configuration_file)).rolePath)) && can(regex("^[^@ \r\n]+@[^@ \r\n]+$", jsondecode(file(var.configuration_file)).alertEmail)) && can(regex("^[a-z0-9]{6}:$", jsondecode(file(var.configuration_file)).identityPrefix)) && can(regex("^[a-z0-9-]{1,64}:$", jsondecode(file(var.configuration_file)).identityDomain)) && length("${jsondecode(file(var.configuration_file)).project}-tfstate-${jsondecode(file(var.configuration_file)).awsAccountId}-${jsondecode(file(var.configuration_file)).awsRegion}") <= 63 && length(setsubtract(toset(keys(jsondecode(file(var.configuration_file)))), toset(["awsAccountId", "awsRegion", "steamAppId", "project", "rolePath", "alertEmail", "identityPrefix", "identityDomain"]))) == 0, false)
    error_message = "Provide valid explicit account/region/app/project/rolePath/email/identity settings only; no credentials or secret values."
  }
}
locals {
  input = jsondecode(file(var.configuration_file))
  c = {
    account            = local.input.awsAccountId
    region             = local.input.awsRegion
    project            = local.input.project
    steam_app_id       = local.input.steamAppId
    identity_prefix    = local.input.identityPrefix
    identity_domain    = local.input.identityDomain
    state_bucket       = "${local.input.project}-tfstate-${local.input.awsAccountId}-${local.input.awsRegion}"
    recording_bucket   = "${local.input.project}-${local.input.awsAccountId}-${local.input.awsRegion}"
    table              = "${local.input.project}-sessions"
    function           = "${local.input.project}-issuer"
    api                = "${local.input.project}-api"
    schedule           = "${local.input.project}-daily-cleanup"
    topic              = "${local.input.project}-alerts"
    budget             = "${local.input.project}-monthly"
    secret             = "${local.input.project}/publisher-key"
    role_path          = local.input.rolePath
    roles              = ["issuer", "media", "deploy", "retrieval", "operations"]
    boundary_name      = "${local.input.project}-boundary"
    seed_role          = "${local.input.project}-bootstrap"
    alert_email        = local.input.alertEmail
    stream_name_prefix = "${local.input.project}-slot-"
  }
}
output "contract" { value = local.c }
