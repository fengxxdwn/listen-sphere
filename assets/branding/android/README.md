# Android generated branding

The approved source master generates:

- `icon-foreground.png` (transparent adaptive-icon foreground)
- `icon-background.png` using the approved dark background color
- legacy launcher PNGs for mdpi, hdpi, xhdpi, xxhdpi and xxxhdpi

`scripts/Generate-ListenSphereIcons.ps1` writes these source outputs and the
app's `mipmap-*`/adaptive resources. The existing notification small icon stays
separate because Android renders notification icons as monochrome masks.
