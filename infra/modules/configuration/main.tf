variable "configuration_file" {
  type        = string
  description = "Explicit owner-authored nonsecret deployment configuration; never response-derived."
  validation {
    condition     = try(length(file(var.configuration_file)) <= 32768 && can(regex("^[0-9]{12}$", jsondecode(file(var.configuration_file)).awsAccountId)) && can(regex("^[a-z]{2}-[a-z]+-[0-9]$", jsondecode(file(var.configuration_file)).awsRegion)) && can(regex("^[a-z][a-z0-9-]{1,23}$", jsondecode(file(var.configuration_file)).project)) && jsondecode(file(var.configuration_file)).steamAppId > 0 && jsondecode(file(var.configuration_file)).steamAppId <= 2147483647 && floor(jsondecode(file(var.configuration_file)).steamAppId) == jsondecode(file(var.configuration_file)).steamAppId && can(regex("^/[A-Za-z0-9_/-]+/$", jsondecode(file(var.configuration_file)).rolePath)) && can(regex("^[^@ \r\n]+@[^@ \r\n]+$", jsondecode(file(var.configuration_file)).alertEmail)) && can(regex("^[a-z0-9]{6}:$", jsondecode(file(var.configuration_file)).identityPrefix)) && can(regex("^[a-z0-9-]{1,64}:$", jsondecode(file(var.configuration_file)).identityDomain)) && length("${jsondecode(file(var.configuration_file)).project}-tfstate-${jsondecode(file(var.configuration_file)).awsAccountId}-${jsondecode(file(var.configuration_file)).awsRegion}") <= 63 && try(jsondecode(file(var.configuration_file)).videoStreamCount >= (try(jsondecode(file(var.configuration_file)).streamMode, "pooled") == "onDemand" ? 0 : 1) && jsondecode(file(var.configuration_file)).videoStreamCount <= 8 && floor(jsondecode(file(var.configuration_file)).videoStreamCount) == jsondecode(file(var.configuration_file)).videoStreamCount, true) && length(setsubtract(toset(keys(jsondecode(file(var.configuration_file)))), toset(["awsAccountId", "awsRegion", "steamAppId", "project", "rolePath", "alertEmail", "identityPrefix", "identityDomain", "videoStreamCount", "streamMode", "recordingLimits", "lambdaConcurrency", "apiRequestsPerSecond", "apiBurst"]))) == 0, false)
    error_message = "Provide valid explicit account/region/app/project/rolePath/email/identity settings only; no credentials or secret values."
  }
  validation {
    condition = try(contains(["pooled", "onDemand"], try(jsondecode(file(var.configuration_file)).streamMode, "pooled")) &&
      (!contains(keys(jsondecode(file(var.configuration_file))), "recordingLimits") || try(jsondecode(file(var.configuration_file)).streamMode, "pooled") == "onDemand") &&
      length(setsubtract(toset(keys(try(jsondecode(file(var.configuration_file)).recordingLimits, {}))), toset(["concurrentSessions", "admissionsPerDay", "admissionsPerMonth", "testerAdmissionsPerDay", "admissionAttemptsPerMinute", "sessionRequestsPerMinute"]))) == 0 &&
      alltrue([for key in ["concurrentSessions", "admissionsPerDay", "admissionsPerMonth", "testerAdmissionsPerDay"] :
      try(jsondecode(file(var.configuration_file)).recordingLimits[key], null) == null ? true : try(jsondecode(file(var.configuration_file)).recordingLimits[key] >= 1 && jsondecode(file(var.configuration_file)).recordingLimits[key] <= 100000000 && floor(jsondecode(file(var.configuration_file)).recordingLimits[key]) == jsondecode(file(var.configuration_file)).recordingLimits[key], false)]) &&
      alltrue([for pair in [{ key = "admissionAttemptsPerMinute", fallback = 120, maximum = 3000 }, { key = "sessionRequestsPerMinute", fallback = 30, maximum = 120 }] :
      try(jsondecode(file(var.configuration_file)).recordingLimits[pair.key], pair.fallback) >= 1 && try(jsondecode(file(var.configuration_file)).recordingLimits[pair.key], pair.fallback) <= pair.maximum && floor(try(jsondecode(file(var.configuration_file)).recordingLimits[pair.key], pair.fallback)) == try(jsondecode(file(var.configuration_file)).recordingLimits[pair.key], pair.fallback)])
    , false)
    error_message = "Use pooled/onDemand, and only documented integer recording limits (null disables count caps)."
  }
  validation {
    condition = try((try(jsondecode(file(var.configuration_file)).lambdaConcurrency, null) == null ? true :
      (jsondecode(file(var.configuration_file)).lambdaConcurrency >= 1 && jsondecode(file(var.configuration_file)).lambdaConcurrency <= 100000 && floor(jsondecode(file(var.configuration_file)).lambdaConcurrency) == jsondecode(file(var.configuration_file)).lambdaConcurrency)) &&
      alltrue([for key in ["apiRequestsPerSecond", "apiBurst"] : try(jsondecode(file(var.configuration_file))[key], 1) >= 1 && try(jsondecode(file(var.configuration_file))[key], 1) <= 10000 && floor(try(jsondecode(file(var.configuration_file))[key], 1)) == try(jsondecode(file(var.configuration_file))[key], 1)])
    , false)
    error_message = "Lambda concurrency must be null or a positive integer; API rate/burst must be positive integers."
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
    stream_mode        = try(local.input.streamMode, "pooled")
    video_stream_count = try(local.input.videoStreamCount, try(local.input.streamMode, "pooled") == "onDemand" ? 0 : 4)
    recording_limits = merge({ concurrentSessions = null, admissionsPerDay = null, admissionsPerMonth = null,
    testerAdmissionsPerDay = null, admissionAttemptsPerMinute = 120, sessionRequestsPerMinute = 30 }, try(local.input.recordingLimits, {}))
    lambda_concurrency = try(local.input.lambdaConcurrency, try(local.input.streamMode, "pooled") == "onDemand" ? null : 2)
    api_rate           = try(local.input.apiRequestsPerSecond, try(local.input.streamMode, "pooled") == "onDemand" ? 100 : 3)
    api_burst          = try(local.input.apiBurst, try(local.input.streamMode, "pooled") == "onDemand" ? 200 : 6)
    stream_name_prefix = "${local.input.project}-slot-"
  }
}
output "contract" { value = local.c }
