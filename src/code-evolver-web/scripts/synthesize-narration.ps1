param(
    [Parameter(Mandatory = $true)][string]$InputPath,
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [string]$Voice = $env:DEMO_VOICE
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Speech
$synth = New-Object System.Speech.Synthesis.SpeechSynthesizer
try {
    $voices = @($synth.GetInstalledVoices() | Where-Object { $_.Enabled -and $_.VoiceInfo.Culture.TwoLetterISOLanguageName -eq 'en' })
    if ($Voice) {
        $selected = $voices | Where-Object { $_.VoiceInfo.Name -eq $Voice } | Select-Object -First 1
    } else {
        $selected = $voices | Sort-Object { $_.VoiceInfo.Name } | Select-Object -First 1
    }
    if (-not $selected) {
        throw 'No matching English desktop speech voice found. Install an English Windows text-to-speech voice, or set DEMO_VOICE to an installed English desktop voice name.'
    }
    $synth.SelectVoice($selected.VoiceInfo.Name)
    $synth.Rate = 0
    $synth.Volume = 100
    $script = Get-Content -LiteralPath $InputPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($script.Count -eq 0) { throw 'The narration script is empty.' }
    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
    $segments = @(
        for ($index = 0; $index -lt $script.Count; $index++) {
            $entry = $script[$index]
            if ([string]::IsNullOrWhiteSpace($entry.text)) { throw 'Narration text must not be empty.' }
            $file = 'voice-{0:D2}.wav' -f ($index + 1)
            $sentences = [regex]::Split($entry.text.Trim(), '(?<=[.!?])\s+')
            $captions = @(
                for ($sentenceIndex = 0; $sentenceIndex -lt $sentences.Count; $sentenceIndex++) {
                    $captionFile = 'voice-{0:D2}-caption-{1:D2}.wav' -f ($index + 1), ($sentenceIndex + 1)
                    $synth.SetOutputToWaveFile((Join-Path $OutputDirectory $captionFile))
                    $synth.Speak($sentences[$sentenceIndex])
                    $synth.SetOutputToNull()
                    if ((Get-Item -LiteralPath (Join-Path $OutputDirectory $captionFile)).Length -le 44) { throw "Speech generation produced no audio for $captionFile." }
                    [pscustomobject]@{ text = $sentences[$sentenceIndex]; file = $captionFile }
                }
            )
            [pscustomobject]@{ title = $entry.title; text = $entry.text; file = $file; captions = $captions }
        }
    )
    $metadata = [pscustomobject]@{ voice = $selected.VoiceInfo.Name; culture = $selected.VoiceInfo.Culture.Name; segments = $segments }
    $metadata | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'narration.json') -Encoding UTF8
    Write-Output "Generated $($segments.Count) English narration segments using $($metadata.voice)."
} finally {
    $synth.Dispose()
}