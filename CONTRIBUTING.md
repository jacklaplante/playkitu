# Contributing

Contributions are welcome! playreel is open to community bug fixes, documentation improvements, examples, tests and work toward additional platforms. Small improvements are useful too.

Open an [issue](https://github.com/jacklaplante/playreel/issues) to report a problem or discuss an idea, or send a [pull request](https://github.com/jacklaplante/playreel/pulls). For larger changes, an issue is a good place to discuss the approach before investing lots of work. Current Windows-only support describes what works today, not a permanent limit on contributions.

For bug reports, start with a small reproducible problem and the component that owns it. Include Unity/OS/tool versions, render/audio configuration, expected behavior and the smallest reproduction.

Use the [development guide](docs/development.md). Reproduce failures in the minimal Unity project or focused native/backend loop before rebuilding a whole game. Preserve explicit consent, gameplay priority, bounded memory/retries, finite shutdown and strict deployment trust. Keep game-specific code outside the reusable package.

Run relevant checks and report their scope. Add meaningful regression coverage for behavior; avoid tests that merely mirror implementation. State skips and unproven platform/live behavior.

Never post keys, Steam tickets, credential caches, Terraform state, private recordings or unredacted logs. See [SECURITY.md](SECURITY.md).

Original contributions use the project's MIT license unless a file retains another explicit license. Preserve upstream notices and patch provenance; do not relicense third-party work.
