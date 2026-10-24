# Maintaining playkitu

playkitu is a reusable, source-first Unity playtest recorder. Keep game adapters,
real deployment identities and private operational files outside this repository.

## Start here

- Read [development](docs/development.md) for the smallest test loop and local
  package editing, and [current limits](docs/limitations.md) before proposing scale
  or release readiness. Do not equate configurable source with deployed capacity.
- [Unity integration](docs/unity-integration.md) owns consent, host APIs, Steam
  ownership and removal. [Capacity](docs/capacity.md) distinguishes video leases,
  admission quotas and quarantine.
- Component guides: [native](native/README.md), [issuer](backend/issuer/README.md),
  [AWS](infra/README.md), [downloads](tools/download/README.md).
- [CONTRIBUTING.md](CONTRIBUTING.md), [SECURITY.md](SECURITY.md) and
  [native redistribution status](native/REDISTRIBUTION.md) apply to contributions
  and release claims. Source licensing does not complete binary release work.

## Ownership and compatibility

`packages/com.playkitu.recording` owns managed capture/lifecycle/protocol;
`native` owns the C++ runtime, ABI, SDK patches and pinned toolchain;
`backend/issuer` owns Steam verification and scoped grants; `infra` owns deployment
and operations; `tools/download` owns the local library/retrieval; the independent
host is `samples/MinimalUnity`.

Search for the existing implementation before adding another. Shared identity and
diagnostic-limit sources are linked into the backend: change the canonical file,
not a copy. Preserve native exports, wire labels and explicitly supported legacy
identities unless a coordinated migration is part of the requested change. The
remaining legacy names do not authorize changing deployment trust.

Never edit Unity's `Library/PackageCache`. Use the sample's existing local package
dependency, or install this checkout with Add package from disk in a host. Refresh
that Unity Editor after managed changes. The host game's instructions govern its
adapter, menus, build pipeline and optional-removal behavior.

## Efficient proof

Reproduce the weakest unproven dependency first. Managed/package behavior belongs
in focused Unity Editor tests; reuse the host's working Steam integration for
authentication. Native bugs belong in a bounded generated-media/native loop.
Backend, download and infrastructure checks have offline PowerShell entry points
in the development guide. Run those scripts in a separate PowerShell process:
they deliberately isolate AWS settings and can alter process environment.

Do not rebuild a whole game for a backend, browser or isolated package bug. Use a
player when proving IL2CPP, packaged resources/native loading, distribution or an
observed Editor/player difference. Stop only this task's library process before
rebuilding its loaded .NET binaries on Windows. Do not kill unrelated apps.

Tests need meaningful regression coverage for the failure. Report actual behavior,
skips and remaining live/platform gates. A fake transport, decode EOS or generated
clip does not establish real consent, Steam admission, lossless capture or complete
coverage. Preserve uncertainty/gaps and original media; do not fabricate timing.

## Privacy, operations and completion

Preserve explicit recording consent, separate optional microphone consent,
immediate microphone stop, gameplay priority, bounded queues/retries and finite
shutdown. Already-mixed audio may finish uploading after Microphone Off; unmixed
microphone samples are cleared. Never infer consent from test readiness.

Keep credentials, publisher keys, Steam tickets, state/plans, private media and
logs in ignored private output. Do not print or commit them, copy a game's account
bindings into public examples, or publish private screenshots as documentation.

Building, cloning and tests do not authorize capture, deployment, changing quotas,
recording switches, access or resource deletion. Check the user's current scope;
do not repeatedly request approval for work already authorized. Never reseed an
existing CONTROL item or assume an extracted Terraform root safely matches an
older installation. Review real state, handler/configuration and migration first.

When changes affect installation, limits or operations, update the matching guide
and prominent limitations together. Explain a limit's user-visible consequence,
what is configurable, and what remains unchanged. Finish authorized work without
waiting for repeated “keep going”; state concrete blockers and remaining proof.
