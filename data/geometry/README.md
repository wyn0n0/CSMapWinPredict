# Contact collision geometry

Run `python tools/prepare_contact_geometry.py`, then rebuild .NET. The binary is
local generated data, excluded from Git; the project copies it to `Geometry/`
beside the API/CLI/test assemblies. No nav data is used.

- Source: https://github.com/pnxenopoulos/awpy-data/releases/tag/2000917
- ClientVersion: 2000917; Steam build: 25515854; format: AWMH v1
- `geometry.zip` SHA-256: `1bc559fa9b1048b078a47be972c0546e71b1d1d958dcbfcc94e17eeb2e454971`
- `de_mirage.mesh` SHA-256: `d5e3aabe17583f07ca899236d015ecc46f7e542fa093ada4782944941b41e679`
- 1,409,404 bytes; 73,290 triangles. Upstream geometry is decimated static world
  collision data, not exact render geometry or dynamic prop state.

Geometry is in Hammer world units, Z-up. Runtime reverses the existing radar X/Y
transform and scales Z by 5120. Player origin is approximated by height samples
36/52/64; nine pairwise rays conservatively include standing/crouching heights.
Actual posture, body width, smoke, FOV and wall penetration are not reconstructed.

This build matches the locally installed game as checked on 2026-09-26, **not a
certification that historical demos use identical Mirage geometry**. The sample
report is technical regression evidence, not an accuracy claim. Updating assets
requires a new pinned rule version/hash and validation.

The game assets belong to Valve Corporation; they are not covered by the upstream
scripts' MIT license. The C# reader implements the publicly documented AWMH layout;
the ray/BVH implementation is local. See THIRD_PARTY_NOTICES.md for attribution.
