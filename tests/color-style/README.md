# Color cover and caption style check

Run the deterministic plain/gradient/fallback and preset checks:

```powershell
.\.tools\dotnet\dotnet.exe run --project tests\color-style\ColorStyleCheck.csproj -c Release
```

The optional SRC01 probe uses a local BMP decoded from the ignored reader-supplied
original. It does not copy the source into the repository:

```powershell
dwebp.exe .cache\planning\0.2.0\color-original.webp -bmp -o .cache\planning\0.2.0\color-original.bmp
.\.tools\dotnet\dotnet.exe run --project tests\color-style\ColorStyleCheck.csproj -c Release -- --src01 .cache\planning\0.2.0\color-original.bmp
```

## Support matrix

| Source background | Decision |
| --- | --- |
| Plain fill with bounded contrasting lettering | Qualified |
| Smooth linear color gradient with bounded contrasting lettering | Qualified |
| Decorated gradient, glow over artwork, faces, hair, or textured background | Source-visible readable caption fallback |

Qualification is CPU only and cancellable. A cover changes only the inferred
source-text footprint inside the caller's permitted area. Typeface choices use
installed Windows Thai fonts; the application bundles no font.
