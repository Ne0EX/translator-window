# Production capture check

Run this check on an unlocked Windows desktop with an unrotated output of at least
1024 by 720 pixels. The output must support the production FP16 scRGB desktop
duplication path. The check opens a visible synthetic window, so run it while the
app is idle and from a desktop-enabled terminal:

```powershell
.\.tools\dotnet\dotnet.exe run --project tests\capture\CaptureCheck.csproj -c Release
```

The check exercises the real native capture and `ScreenCapture.Session` paths,
including exact pixels, crop changes, cancellation, disposal, GDI fallback, and
the retry cooldown. The fallback gate forces the existing private failure state;
it does not induce a hardware `DXGI_ERROR_ACCESS_LOST`, which would require a real
display reset. Captured pixels are never saved. The JSON artifact contains only
geometry, hashes, and outcomes at `.cache/verification/production-capture-check.json`.
