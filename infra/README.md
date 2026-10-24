# Playkitu AWS infrastructure

Source-only deployment workflow for one explicitly selected AWS account/region and
Steam app. No existing deployment, API, secret, verified email subscription or
live cost result is included. Preparation and tests do not deploy or activate AWS.

## Fixed architecture and limits

Three staged roots keep privileged owner bootstrap separate from routine deployment:

1. `foundation`: an empty named HTTP API; optionally its AWS-managed service-linked
   role only when the owner confirms it is absent. Do not import/manage an existing
   shared role.
2. `bootstrap`: a configured pool of retained KVS streams (four by default), private encrypted S3 state/diagnostics,
   DynamoDB, secret metadata, roles under a console-owned permissions boundary,
   closed API stage, alerts and disabled cleanup. Routine deploy cannot edit IAM,
   read publisher secrets or grant invocation permissions.
3. `runtime`: the reviewed Linux x64 Lambda ZIP and four API route integrations.
   A second owner bootstrap checkpoint grants narrowly bound invocation/cleanup
   only after OFF/OFF CONTROL and access logging are verified.

There are no always-on servers, NAT/VPC, runtime-created streams, automatic video
exports or permission-expanding deployment roles. Media credentials are write-only
for one assigned stream, short-lived, with the immutable four-hour deadline.
Diagnostic grants bind exact session/key, size/checksum and AES256 conditions.

Video/diagnostics retain 30 days. State is versioned/protected; diagnostic versions
are suspended with current/noncurrent expiry and incomplete multipart cleanup.
DynamoDB TTL is eventual cleanup, not lease authority. Resource deletion protection
and `prevent_destroy` remain enabled; removal is a separate owner-reviewed action.
Quotas are eight admissions/tester/day, eight globally/day and fifty/month, no
refunds; finite session/object/request limits still apply. Set `videoStreamCount`
to configure 1–8 streams; [capacity and safe resizing](../docs/capacity.md) explains
the separate quotas, quarantine and migration of an existing pool.

Monthly budget alerts use a USD100 budget with actual USD10/25/50 and forecast
USD75 notifications. Alerts and tag accounting can be delayed/incomplete; neither
alerts nor quotas guarantee a billing cap. Review current region-specific ingest,
retention, request, backend, viewing and egress rates and expected workloads before
any apply. `cost.ps1` accepts explicit reviewed rates and workload quantities; it
makes no network request and does not embed a stale price quote.

## Offline build and validation

```powershell
pwsh -NoProfile -File backend/issuer/prepare.ps1
pwsh -NoProfile -File backend/issuer/test.ps1
pwsh -NoProfile -File backend/issuer/package.ps1
pwsh -NoProfile -File infra/prepare.ps1
pwsh -NoProfile -File infra/test.ps1
```

Terraform 1.16.6 and AWS provider 6.69.0 are pinned. Initialization uses
`-backend=false`; tests use mocked providers, fake credentials and blocked outbound
HTTP(S). No live Terraform plan/apply is used by tests. `PLAYKITU_TERRAFORM` may
select an existing exact Terraform binary. Generated files stay in `infra/private`.

## Owner deployment sequence (not executed by these scripts)

Use a dedicated PowerShell process, MFA and an owner-confirmed non-root IAM
principal/federation role. Never use root CLI keys or AdministratorAccess. No user
or pipeline may let routine deploy expand its own permissions. Read
[readiness and controls](READINESS.md) before activation.

1. Copy `deployment.example.json` to ignored `private/deployment.json`; replace the
   fake account/app/region/email with the owner's selected values. Keep identities
   consistent with the client. Use an absolute path in all roots. The configuration
   accepts only its documented nonsecret fields; resource names are derived.
2. Render the exact time-limited owner policies locally (at most one hour). The
   confirmed principal is an input, never discovered from credentials. Before the
   API exists omit `-ApiId`; the generated foundation scope permits only its
   reviewed named creation. The owner installs/reviews those exact policies:

```powershell
pwsh -NoProfile -File infra/access/prepare-owner.ps1 -ConfigurationFile ABSOLUTE_OWNER_CONFIG -OperatorPrincipalArn EXACT_NONROOT_OPERATOR_ARN -WindowStartUtc APPROVED_UTC_START -WindowEndUtc APPROVED_UTC_END -OutputDirectory NEW_ABSOLUTE_POLICY_DIRECTORY
terraform -chdir=infra/foundation init
terraform -chdir=infra/foundation plan -var=configuration_file=ABSOLUTE_OWNER_CONFIG -var=create_api_gateway_service_linked_role=false -out=../private/foundation.plan
# After owner review/approval only:
terraform -chdir=infra/foundation apply ../private/foundation.plan
terraform -chdir=infra/foundation output -raw api_id
```

Set the prerequisite flag true only after checking that the account role is absent.
The foundation/bootstrap initial state is private local state; protect it with OS
access/encryption. Do not commit state, plans or backend overrides.

3. Re-render `prepare-owner.ps1` in a new directory with the actual `-ApiId`.
   The owner installs the exact boundary and bootstrap policies, then initializes,
   reviews and applies bootstrap with `-var=configuration_file=ABSOLUTE_OWNER_CONFIG`
   and `-var-file=ABSOLUTE_GENERATED_bootstrap.tfvars.json`. Its defaults keep
   invocation/cleanup disabled. `terraform -chdir=infra/bootstrap output -json
   runtime_inputs` gives nonsecret references for ignored runtime tfvars.
4. The owner privately provisions the restricted Steam publisher key value into
   the new Secrets Manager secret using the console. Terraform manages its ARN
   metadata only: no secret version/value resource, sensitive variable or state
   workaround. Verify Steam app/key ownership separately before live admission.
5. Prepare the canonical conditional OFF seed and flag-only requests:

```powershell
pwsh -NoProfile -File infra/access/prepare-control.ps1 -ConfigurationFile ABSOLUTE_OWNER_CONFIG -OutputDirectory NEW_ABSOLUTE_CONTROL_DIRECTORY
# Only after role identity verification; this condition refuses existing CONTROL:
aws dynamodb put-item --profile OWNER_OPERATIONS_PROCESS_PROFILE --region OWNER_REGION --cli-input-json file://ABSOLUTE_CONTROL_SEED_JSON
```

The owner verifies actual boolean OFF/OFF flags with the generated strong-read
request. Never overwrite CONTROL, reclaim by direct table editing, or reset quotas.
6. Review the newly built `backend/issuer/private/evidence/artifact.json` hash.
   Runtime tfvars must supply the exact API ID/origin, issuer/media roles,
   publisher-secret ARN and ordered streams from bootstrap, plus
   `expected_issuer_sha256`. Run runtime init/plan/apply under the dedicated deploy
   process profile, with explicit configuration and runtime tfvars. The default ZIP
   is the newly built `backend/issuer/private/issuer.zip`; a wrong hash fails.
7. Owner establishes/reconciles the exact API stage access-log destination and its
   account-level delivery prerequisites. Log format contains only request ID,
   route key, status and latency, never headers/bodies/tickets. Then review a second
   bootstrap plan with `runtime_ready=true` and `owner_logging_ready=true`. This
   enables bound invocation/once-daily cleanup, while CONTROL remains OFF/OFF.
8. Save/review `terraform -chdir=infra/runtime output -json download_configuration`
   as an ignored owner download JSON, and configure the client from the same trust
   values. Confirm email subscription/alerts and cost-allocation tag coverage.
   Perform a separately authorized bounded consent/capture/retrieval test before
   treating deployment or media quality as demonstrated.

For remote state, copy `access/backend.example.hcl` to ignored private files with
actual bucket/profile/account/region and distinct foundation/bootstrap/runtime
keys. After protection/backup review, supply a private `backend_override.tf` and
use `terraform init -migrate-state -backend-config=...` separately for each root.
Default workspace only; routine deploy can access only runtime state/lock paths.
No remote-state data source gives routine deploy bootstrap-state access.

No cloud apply, MFA flow, key entry, alert send or live capture is implied by
building/testing this repository. These remain owner operations with fresh review.
