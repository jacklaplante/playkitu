resource "aws_s3_bucket" "state" {
  bucket        = local.c.state_bucket
  force_destroy = false
  lifecycle { prevent_destroy = true }
}
resource "aws_s3_bucket_versioning" "state" {
  bucket = aws_s3_bucket.state.id
  versioning_configuration { status = "Enabled" }
}
resource "aws_s3_bucket_server_side_encryption_configuration" "state" {
  bucket = aws_s3_bucket.state.id
  rule {
    apply_server_side_encryption_by_default { sse_algorithm = "AES256" }
  }
}
resource "aws_s3_bucket_public_access_block" "state" {
  bucket                  = aws_s3_bucket.state.id
  block_public_acls       = true
  block_public_policy     = true
  ignore_public_acls      = true
  restrict_public_buckets = true
}
resource "aws_s3_bucket_ownership_controls" "state" {
  bucket = aws_s3_bucket.state.id
  rule { object_ownership = "BucketOwnerEnforced" }
}
resource "aws_s3_bucket_policy" "state" {
  bucket = aws_s3_bucket.state.id
  policy = jsonencode({ Version = "2012-10-17", Statement = [{ Sid = "TLSOnly", Effect = "Deny", Principal = "*", Action = "s3:*", Resource = [aws_s3_bucket.state.arn, "${aws_s3_bucket.state.arn}/*"], Condition = { Bool = { "aws:SecureTransport" = "false" } } }] })
}

resource "aws_s3_bucket" "recording" {
  bucket        = local.c.recording_bucket
  force_destroy = false
  lifecycle { prevent_destroy = true }
}
resource "aws_s3_bucket_versioning" "recording" {
  bucket = aws_s3_bucket.recording.id
  # No old versions may silently survive 30-day current-object expiry.
  versioning_configuration { status = "Suspended" }
}
resource "aws_s3_bucket_server_side_encryption_configuration" "recording" {
  bucket = aws_s3_bucket.recording.id
  rule {
    apply_server_side_encryption_by_default { sse_algorithm = "AES256" }
  }
}
resource "aws_s3_bucket_public_access_block" "recording" {
  bucket                  = aws_s3_bucket.recording.id
  block_public_acls       = true
  block_public_policy     = true
  ignore_public_acls      = true
  restrict_public_buckets = true
}
resource "aws_s3_bucket_ownership_controls" "recording" {
  bucket = aws_s3_bucket.recording.id
  rule { object_ownership = "BucketOwnerEnforced" }
}
resource "aws_s3_bucket_policy" "recording" {
  bucket = aws_s3_bucket.recording.id
  policy = jsonencode({ Version = "2012-10-17", Statement = [
    { Sid = "TLSOnly", Effect = "Deny", Principal = "*", Action = "s3:*", Resource = [aws_s3_bucket.recording.arn, "${aws_s3_bucket.recording.arn}/*"], Condition = { Bool = { "aws:SecureTransport" = "false" } } },
    { Sid = "ExplicitSSEForUploads", Effect = "Deny", Principal = "*", Action = "s3:PutObject", Resource = "${aws_s3_bucket.recording.arn}/sessions/*", Condition = { StringNotEquals = { "s3:x-amz-server-side-encryption" = "AES256" } } }
  ] })
}
resource "aws_s3_bucket_lifecycle_configuration" "recording" {
  bucket = aws_s3_bucket.recording.id
  rule {
    id     = "session-expiry"
    status = "Enabled"
    filter { prefix = "sessions/" }
    expiration { days = 30 }
    noncurrent_version_expiration { noncurrent_days = 30 }
    abort_incomplete_multipart_upload { days_after_initiation = 1 }
  }
}

resource "aws_kinesis_video_stream" "slot" {
  for_each                = toset([for i in range(local.c.video_stream_count) : format("%02d", i + 1)])
  name                    = "${local.c.project}-slot-${each.key}"
  data_retention_in_hours = 720
  media_type              = "video/h264"
  # Omitted kms_key_id uses aws/kinesisvideo. No export, warm tier or CMK.
  lifecycle { prevent_destroy = true }
}
resource "aws_dynamodb_table" "sessions" {
  name                        = local.c.table
  billing_mode                = "PAY_PER_REQUEST"
  hash_key                    = "pk"
  range_key                   = "sk"
  deletion_protection_enabled = true
  attribute {
    name = "pk"
    type = "S"
  }
  attribute {
    name = "sk"
    type = "S"
  }
  attribute {
    name = "sessionDay"
    type = "S"
  }
  attribute {
    name = "sessionOrder"
    type = "S"
  }
  dynamic "attribute" {
    for_each = local.c.stream_mode == "onDemand" ? { workShard = "S", workDue = "N" } : {}
    content {
      name = attribute.key
      type = attribute.value
    }
  }
  ttl {
    attribute_name = "purgeAt"
    enabled        = true
  }
  global_secondary_index {
    name            = "sessions-by-day"
    projection_type = "KEYS_ONLY"
    key_schema {
      attribute_name = "sessionDay"
      key_type       = "HASH"
    }
    key_schema {
      attribute_name = "sessionOrder"
      key_type       = "RANGE"
    }
  }
  dynamic "global_secondary_index" {
    for_each = local.c.stream_mode == "onDemand" ? [1] : []
    content {
      name            = "stream-work-by-due"
      projection_type = "ALL"
      key_schema {
        attribute_name = "workShard"
        key_type       = "HASH"
      }
      key_schema {
        attribute_name = "workDue"
        key_type       = "RANGE"
      }
    }
  }
  # No snapshots/PITR copies of short-lived private session metadata.
  lifecycle { prevent_destroy = true }
}
resource "aws_secretsmanager_secret" "publisher" {
  name                    = local.c.secret
  description             = "New restricted Valve publisher key; value provisioned privately by owner after approval."
  recovery_window_in_days = 30
  lifecycle { prevent_destroy = true }
}
