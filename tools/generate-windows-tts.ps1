param(
    [Parameter(Mandatory = $true)]
    [string]$TextFile,

    [Parameter(Mandatory = $true)]
    [string]$OutputFile
)

$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.Speech

$text = Get-Content -LiteralPath $TextFile -Raw -Encoding UTF8
if ([string]::IsNullOrWhiteSpace($text)) {
    throw "Text file is empty: $TextFile"
}

$outputDir = Split-Path -Parent $OutputFile
if (![string]::IsNullOrWhiteSpace($outputDir)) {
    New-Item -ItemType Directory -Force -Path $outputDir | Out-Null
}

$synth = New-Object System.Speech.Synthesis.SpeechSynthesizer
try {
    $voice = $synth.GetInstalledVoices() |
        Where-Object {
            $_.Enabled -and (
                $_.VoiceInfo.Culture.Name -like "zh*" -or
                $_.VoiceInfo.Name -match "Chinese|Huihui|Yaoyao|Kangkang|Hanhan|Xiaoxiao|Yunxi|Yunyang|Xiaochen|Xiaoyi"
            )
        } |
        Select-Object -First 1

    if ($null -ne $voice) {
        $synth.SelectVoice($voice.VoiceInfo.Name)
        Write-Output ("voice=" + $voice.VoiceInfo.Name)
    }
    else {
        Write-Output "voice=default"
    }

    $synth.Rate = -1
    $synth.Volume = 100
    $synth.SetOutputToWaveFile($OutputFile)
    $synth.Speak($text)
    $synth.SetOutputToNull()
}
finally {
    $synth.Dispose()
}

Write-Output ("output=" + $OutputFile)
