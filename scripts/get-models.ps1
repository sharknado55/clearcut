# Downloads the two AI models into src/Assets and checks their SHA-256 hashes.
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$assets = Join-Path (Split-Path $PSScriptRoot -Parent) 'src\Assets'
$models = @(
    @{ Name = 'birefnet-lite.onnx'
       Url = 'https://huggingface.co/onnx-community/BiRefNet_lite-ONNX/resolve/main/onnx/model.onnx'
       Sha256 = '5600024376f572a557870a5eb0afb1e5961636bef4e1e22132025467d0f03333' },
    @{ Name = 'isnet-anime.onnx'
       Url = 'https://huggingface.co/skytnt/anime-seg/resolve/main/isnetis.onnx'
       Sha256 = 'f15622d853e8260172812b657053460e20806f04b9e05147d49af7bed31a6e99' }
)
foreach ($model in $models) {
    $path = Join-Path $assets $model.Name
    if ((Test-Path $path) -and (Get-FileHash $path -Algorithm SHA256).Hash -eq $model.Sha256) {
        Write-Host "$($model.Name) is already present"
        continue
    }
    Write-Host "Downloading $($model.Name)..."
    $temp = "$path.download"
    Invoke-WebRequest $model.Url -OutFile $temp -UseBasicParsing
    if ((Get-FileHash $temp -Algorithm SHA256).Hash -ne $model.Sha256) {
        Remove-Item $temp
        throw "$($model.Name) failed its checksum. Try again later."
    }
    Move-Item $temp $path -Force
}
Write-Host 'Models ready.'
