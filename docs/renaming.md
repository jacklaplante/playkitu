# Migrating to playkitu

The repository is now https://github.com/jacklaplante/playkitu. GitHub preserves
old repository links and commits; v0.1.0 stays available for existing installations.
Version 0.2.0 uses com.playkitu.recording, Playkitu.* managed namespaces/assemblies
and PLAYKITU_* tool settings. Update package paths, assembly references and your
adapter imports together; preserve Unity .meta GUIDs when moving adapter assets.

New deployments use pkrec2: and playkitu-recording: for their identity defaults.
Existing deployments must retain their explicit identity prefix/domain, resource
names, roles and ARNs until a separate coordinated migration. Renaming this source
does not rename or deploy any AWS resources.

Compatibility identifiers intentionally retain their original spelling:

- PLAYREEL_AWS_REGION is the accepted native runtime's trusted-region ABI key.
- playreel.decoded-media-probe and the playreel.owner-* / recording-health kinds
  identify existing version-1 data formats. Their writers/readers remain paired.
- Native export names, binary filenames, byte-pinned media fixtures and upstream
  patch provenance remain unchanged. They are compatibility contracts, not the
  product's display name.

This avoids rebuilding native dependencies or rewriting saved evidence solely
for branding. The new managed package interoperates with the accepted native
runtime. Current README, package identity, samples, public APIs and CLI names use
playkitu. The game can update its dependency independently of an older test build.
