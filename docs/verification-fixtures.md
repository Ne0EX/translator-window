# Verification fixture provenance

## Author-provided manga OCR fixture

- **Title:** *Give My Regards to Black Jack* / ブラックジャックによろしく
- **Author:** SHUHO SATO / 佐藤秀峰
- **Official download and terms:** https://www.densho810.com/free/
- **Official Japanese volume 1 archive:** https://www.densho810.com/free/dl/001bj.zip
- **Retrieved:** 2026-09-22

The archive is subject to the author's custom free secondary-use terms on the
official page. It is not presented here as Creative Commons or public-domain
material. Anyone acquiring, using, or redistributing the material must review
and comply with the current official terms.

This repository does not include the archive, PDF, rendered pages, OCR output,
or screenshots. Local verification files stay under the ignored
`artifacts/manga-test/` directory. Tests use pages rendered directly from the
unchanged official PDF without manually altering their content.

The original page 12 regression fixture is a 1273 × 1800 PNG rendered with
Poppler:

```powershell
pdftoppm -f 12 -l 12 -singlefile -scale-to 1800 -png artifacts/manga-test/volume1/001bj.pdf artifacts/manga-test/page12
```

The resulting `artifacts/manga-test/page12.png` is the input referenced by the
optional OCR and manga checks.

## Publisher preview corpus

The separate 27-position preview corpus, including the family-chart pages 10
and 21, came from the publisher-hosted reader at:

https://nc.tameshiyo.me/9784094066371

These preview captures are unrelated to the author-provided archive above, and
the archive's terms must not be inferred to apply to them. The repository does
not redistribute preview images, screenshots, OCR output, or derivatives. They
remain local verification inputs under ignored `.cache/` and `artifacts/`
directories; anyone reproducing those checks must follow the publisher's terms.
