# Copy to an ignored private backend file; replace every placeholder explicitly.
bucket = "OWNER_STATE_BUCKET"
key = "runtime/terraform.tfstate"
region = "OWNER_REGION"
encrypt = true
use_lockfile = true
allowed_account_ids = ["OWNER_ACCOUNT_ID"]
profile = "OWNER_PROCESS_PROFILE"
workspace_key_prefix = "runtime/workspaces-disabled"
