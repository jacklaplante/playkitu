# Changelog

## Unreleased

- Add a local browser library to list individual playtests, download session evidence and watch video/audio as MP4 parts.
- Preserve original MKVs, show partial-download notes, support cancellation and reopen saved downloads.
- Configure the shared video pool with `videoStreamCount` (default four, supported one through eight); retain independent admission quotas and document safe existing-pool migration.

## 0.2.0 — playkitu

- Rename the repository, Unity package, managed APIs and tools to playkitu.
- Use the supplied logo and retain the free/open-source contribution guide.
- Preserve legacy native/data contracts and old Git revisions for existing users.
- New deployments use pkrec2: identities; existing deployments keep their explicit identity settings.

## 0.1.0 — source release preparation

- Independent Unity package with host-owned consent, authentication and lifecycle.
- Windows native source/tooling with pinned dependencies and explicit region binding.
- Configurable AWS authorization service, Terraform, controls and download tool.
- Minimal Unity sample, offline validation and opt-in Windows IL2CPP self-check.
- MIT terms for original code and retained upstream notices.

No prebuilt native binaries or newly deployed live end-to-end qualification are included.
