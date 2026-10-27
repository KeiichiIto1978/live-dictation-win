<#
.SYNOPSIS
    仕上げパス用に、日本語専用のオフラインモデルと VAD モデルを取得する。

.DESCRIPTION
    録音停止後の作り直し（仕上げパス）で使うモデルを
    %LOCALAPPDATA%\LiveDictation\models-offline 配下に置く。
    ライブ表示用のストリーミングモデルとはフォルダを分ける。

.PARAMETER Repository
    Hugging Face のモデルリポジトリ。

.PARAMETER Force
    既に存在するファイルも取得し直す。
#>
[CmdletBinding()]
param(
    [string]$Repository = 'csukuangfj2/sherpa-onnx-moonshine-base-ja-quantized-2026-02-27',
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

$root = Join-Path $env:LOCALAPPDATA 'LiveDictation\models-offline'
New-Item -ItemType Directory -Path $root -Force | Out-Null

function Get-RemoteFile {
    param([string]$Url, [string]$Destination, [long]$ExpectedSize = 0)

    if ((Test-Path $Destination) -and -not $Force) {
        if ($ExpectedSize -eq 0 -or (Get-Item $Destination).Length -eq $ExpectedSize) {
            Write-Host "  スキップ (取得済み): $(Split-Path $Destination -Leaf)"
            return
        }
    }

    $sizeText = if ($ExpectedSize -gt 0) { " ($([math]::Round($ExpectedSize / 1MB, 1)) MB)" } else { '' }
    Write-Host "  取得中: $(Split-Path $Destination -Leaf)$sizeText"

    # 途中で失敗したファイルを本番の名前で残さない。
    # 壊れたモデルは起動時に判別できず、原因不明の失敗になるため。
    $temp = "$Destination.part"
    try {
        Invoke-WebRequest -Uri $Url -OutFile $temp -UseBasicParsing
        Move-Item -Path $temp -Destination $Destination -Force
    }
    catch {
        if (Test-Path $temp) { Remove-Item $temp -Force }
        throw "取得に失敗しました: $Url`n$($_.Exception.Message)"
    }
}

# --- VAD モデル -------------------------------------------------------------
Write-Host 'VAD モデル'
Get-RemoteFile `
    -Url 'https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/silero_vad.onnx' `
    -Destination (Join-Path $root 'silero_vad.onnx')

# --- 音声認識モデル ---------------------------------------------------------
Write-Host ''
Write-Host "音声認識モデル: $Repository"

$modelName = $Repository.Split('/')[-1]
$targetDir = Join-Path $root $modelName
New-Item -ItemType Directory -Path $targetDir -Force | Out-Null

try {
    $entries = Invoke-RestMethod -Uri "https://huggingface.co/api/models/$Repository/tree/main" -Method Get
}
catch {
    throw "モデル一覧を取得できませんでした: $Repository`n$($_.Exception.Message)"
}

# 認識に必要なのはモデル本体と語彙だけ。テスト音声などは落とさない。
# モデル本体は .onnx と .ort の両方がありうる（Moonshine 系は .ort）
$wanted = $entries | Where-Object {
    $_.type -eq 'file' -and ($_.path -like '*.onnx' -or $_.path -like '*.ort' -or $_.path -eq 'tokens.txt')
}

if (-not $wanted) {
    throw "必要なモデルファイルが見つかりませんでした: $Repository"
}

# 量子化済みがあるときは int8 側だけを使う。
$models = $wanted | Where-Object { $_.path -notlike 'tokens.txt' }
$int8 = $models | Where-Object { $_.path -like '*.int8.*' }
$selected = @()
$selected += if ($int8) { $int8 } else { $models }
$selected += $wanted | Where-Object { $_.path -eq 'tokens.txt' }

foreach ($entry in $selected) {
    Get-RemoteFile `
        -Url "https://huggingface.co/$Repository/resolve/main/$($entry.path)" `
        -Destination (Join-Path $targetDir $entry.path) `
        -ExpectedSize $entry.size
}

Write-Host ''
Write-Host '完了しました。録音を停止すると自動で文字起こしを作り直します。' -ForegroundColor Green
Write-Host '保存済みのセッションに後からかけ直すこともできます:'
Write-Host '  LiveDictation.exe --finalize "<セッションフォルダ>"'
