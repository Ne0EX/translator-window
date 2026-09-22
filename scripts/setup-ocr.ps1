param(
    [string]$Destination = (Join-Path $PSScriptRoot '../models/tessdata')
)
$ErrorActionPreference = 'Stop'
$revision = '87416418657359cb625c412a48b6e1d6d41c29bd'
$models = [ordered]@{
    eng = '7D4322BD2A7749724879683FC3912CB542F19906C83BCC1A52132556427170B2'
    jpn = '1F5DE9236D2E85F5FDF4B3C500F2D4926F8D9449F28F5394472D9E8D83B91B4D'
    jpn_vert = 'BF1E2640954691797E2DC14F38533E601B59EE37958698AE0F0B81DC6F09C71B'
    kor = '6B85E11D9BBF07863B97B3523B1B112844C43E713DF8B66418A081FD1060B3B2'
    tha = '294227CC2D1292B0ACB28D61D4115C88252B96D466CA90B417CF4CF0C67BF07C'
}
New-Item -ItemType Directory -Path $Destination -Force | Out-Null
foreach ($model in $models.Keys) {
    $path = Join-Path $Destination "$model.traineddata"
    if (!(Test-Path -LiteralPath $path) -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $models[$model]) {
        Write-Host "Downloading local OCR model: $model"
        $partial = "$path.download"
        Invoke-WebRequest "https://raw.githubusercontent.com/tesseract-ocr/tessdata_fast/$revision/$model.traineddata" -OutFile $partial -UseBasicParsing
        if ((Get-FileHash -LiteralPath $partial -Algorithm SHA256).Hash -ne $models[$model]) {
            throw "Downloaded OCR model $model failed SHA256 verification."
        }
        Move-Item -LiteralPath $partial -Destination $path -Force
    }
    Get-Item -LiteralPath $path | Select-Object Name,Length
}
$licensePath = Join-Path $Destination 'LICENSE.tessdata_fast'
if (!(Test-Path -LiteralPath $licensePath)) {
    Invoke-WebRequest "https://raw.githubusercontent.com/tesseract-ocr/tessdata_fast/$revision/LICENSE" -OutFile $licensePath -UseBasicParsing
}
Write-Host 'OCR models are local. Rebuild the application to copy them beside the executable. Runtime OCR never downloads models.'
