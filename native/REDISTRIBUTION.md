# Source terms and binary release gates

`LICENSE` covers original Playreel engine/tooling code. It does not relicense
upstream software, modified AWS source, SDK patches, Microsoft components or
codecs. `third-party/` preserves the exact upstream license/notice files obtained
from the hash-pinned source archives in `dependencies.json`:

| Source | Pin | Upstream terms |
|---|---|---|
| AWS producer C++ / C / PIC | 3.6.0 / 1.6.2 / 1.3.1 | Apache-2.0; retain notices and identify modifications |
| log4cplus | 2.2.1 | Apache-2.0 / BSD-2-Clause; see its license |
| ThreadPool | 3abd50f329c2bc20e379b3d467774ffd8e3b1343 | zlib |

The two tracked canonical patches modify AWS code: bounded in-memory credentials,
redacted SDK diagnostics, finite sender pacing, lifecycle retirement, observation
and UTC timestamp mapping. Their terms remain those of the modified upstream
files. Hash manifests verify complete base/current source states before either
SDK tree changes. Fresh source preparation does not import private evidence or
depend on historical patched caches.

The pinned GStreamer Windows distribution supplies development and runtime
libraries. Packaging copies its license inventory and `versions.txt`, alongside
AWS/log4cplus/ThreadPool notices. Selected libraries/plugins include LGPL-family
GStreamer/GLib components, curl, OpenSSL, libffi, pcre2, orc, zlib and bzip2.
The exact packaged components are determined by `runtime/inventory.json`, not by
the presence of extra notices. GStreamer corresponding-source/build-material and
replacement rights must be addressed for the exact released binaries. Official
guidance: https://gstreamer.freedesktop.org/documentation/frequently-asked-questions/licensing.html

The build-only innounp/7-Zip extractors have their own GPL/public-domain/
LGPL-BSD-unRAR terms and are downloaded to the ignored cache, not shipped in the
runtime. Microsoft VC runtime binaries have separate redistribution eligibility
and license conditions; an MIT source license does not authorize redistributing
them. Windows Media Foundation, D3D, UCRT and GPU drivers are OS dependencies, not
files to copy from System32. Official guidance:
https://learn.microsoft.com/en-us/cpp/windows/redistributing-visual-cpp-files

`collect-source-materials.ps1` creates a fresh local source-material directory
containing this native source/tooling, canonical patches, notices, pins and the
verified AWS/log4cplus/ThreadPool source ZIPs. It deliberately does not claim to
supply corresponding sources for every GStreamer-bundled dependency. No private
runtime, recordings, credentials or build cache is copied into those materials.

**Binary publication remains held.** Before release, assemble exact GStreamer/
dependency corresponding sources and build recipes (including relevant Cerbero
revision/options), verify component security maintenance and TLS trust material,
replacement/relinking conditions, Microsoft eligibility and applicable codec
terms, and test the resulting closure on a clean supported Windows environment.
Local compilation/tests do not close these gates or promise bit-identical builds.
