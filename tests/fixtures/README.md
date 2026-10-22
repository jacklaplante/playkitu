# Historical synthetic timing fixtures

These 13 JSON files were exported from tracked generated-media/native-clock and
safe managed-ABI tests. They contain no recorded gameplay or microphone media.
They are regression inputs, not proof of a new Playreel recording or physical
capture/log synchronization. No private logs, original evidence directories or
media files are distributed with them.

Normalization changes only the decoded-probe kind to
`playreel.decoded-media-probe` and redacts the personal absolute `sourceArtifact`
location in managed-bridge provenance. That original artifact is not distributed;
its recorded native DLL hash and the measured fixture hash remain historical
provenance, not hashes of the newly built runtime. `provenance.json` records old
and exported fixture hashes. Measured numeric values, IDs, gaps, uncertainty and
unavailable results are preserved. The managed fixture itself is byte-identical,
so its provenance `fixtureSha256` remains valid. Media hashes refer to original
generated media, not to JSON filenames or publicly supplied media files.

Clock normal/delay correspondences establish only their selected observed points.
Overflow/reset cases deliberately remain unavailable. Consumers must preserve
those limitations rather than infer continuous coverage or join unrelated runs.
