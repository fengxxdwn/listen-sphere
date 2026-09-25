# ListenSphere branding assets

This directory is the single home for product artwork. The approved ListenSphere
PNG supplied for P11 is the current product icon source.

- `source/` contains replaceable master artwork.
- `windows/` will contain generated Windows resources.
- `android/` will contain generated Android source resources.

Platform projects consume generated assets from this pipeline instead of
keeping unrelated copies. Run `scripts/Generate-ListenSphereIcons.ps1` after
replacing the canonical source PNG.
