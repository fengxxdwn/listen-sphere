# ListenSphere branding assets

This directory is the single home for product artwork. The current Android
launcher artwork is a temporary development icon and is not the final
ListenSphere brand.

- `source/` contains replaceable master artwork.
- `windows/` will contain generated Windows resources.
- `android/` will contain generated Android source resources.

Platform projects must consume generated assets from this pipeline instead of
keeping unrelated copies. P11-A establishes the contract only; final icon
generation and application to Windows executables belongs to the later
packaging stages.
