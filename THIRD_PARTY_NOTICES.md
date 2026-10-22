# Third-party code and dependencies

The root MIT license covers original project code/documentation. It does not replace third-party terms, authorize proprietary SDK redistribution, or certify codec patent rights.

## Included upstream material

AWS SDK patch payloads modify Apache-2.0 source and retain those upstream terms. Changes are identified by patches and hash manifests. Exact notices from the pinned source archives are retained:

| Component | Retained material |
| --- | --- |
| AWS producer C++ 3.6.0 | [LICENSE](native/third-party/amazon-kinesis-video-streams-producer-sdk-cpp-3.6.0/LICENSE), [NOTICE](native/third-party/amazon-kinesis-video-streams-producer-sdk-cpp-3.6.0/NOTICE) |
| AWS producer C 1.6.2 | [LICENSE](native/third-party/amazon-kinesis-video-streams-producer-c-1.6.2/LICENSE), [NOTICE](native/third-party/amazon-kinesis-video-streams-producer-c-1.6.2/NOTICE) |
| AWS PIC 1.3.1 | [LICENSE](native/third-party/amazon-kinesis-video-streams-pic-1.3.1/LICENSE), [NOTICE](native/third-party/amazon-kinesis-video-streams-pic-1.3.1/NOTICE) |
| log4cplus 2.2.1 | [COPYING](native/third-party/log4cplus-REL_2_2_1/COPYING), [LICENSE](native/third-party/log4cplus-REL_2_2_1/LICENSE) |
| ThreadPool, pinned commit | [COPYING](native/third-party/ThreadPool-3abd50f329c2bc20e379b3d467774ffd8e3b1343/COPYING) |

Complete upstream source trees are downloaded into ignored build storage. The source-material collector preserves verified archives and patch/build metadata locally; its report identifies material not yet collected.

## External dependencies

- **GStreamer, selected plugins and dependencies:** component-specific LGPL-family and other terms. Local packaging preserves the distribution's notices. Exact corresponding sources/build recipes and replacement rights must be addressed before distributing binaries.
- **curl, OpenSSL, GLib, libffi, pcre2, orc, zlib and bzip2:** preserve terms/notices for the exact selected versions. The runtime inventory identifies included binaries.
- **Microsoft Visual C++ runtime and Windows media/graphics components:** separate Microsoft/OS terms apply. Do not copy OS/driver files from System32. Redistribution eligibility is separate from MIT.
- **.NET, NuGet, AWS SDK and Unity dependencies:** their own terms apply to restored/runtime packages. Lockfiles and preparation scripts identify versions.
- **Steamworks.NET:** an optional external MIT wrapper. Valve SDK materials and publisher access requirements are separate.
- **innounp and 7-Zip tools:** build-only dependencies under their respective terms; extractors are not shipped in the player runtime.

No prebuilt runtime, player, recordings, credentials or SDK cache is included in this source release. Local build success is not binary redistribution clearance.

See [native/REDISTRIBUTION.md](native/REDISTRIBUTION.md) for exact scope, remaining release work and official references.
