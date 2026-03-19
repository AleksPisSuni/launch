# Generate three tiny synthesized WAV samples (kick, snare, hihat) for quick testing.
param(
    [string]$OutDir = "samples"
)

$base = Join-Path $PSScriptRoot $OutDir
if (-not (Test-Path $base)) { New-Item -ItemType Directory -Path $base | Out-Null }

function Write-Wav($path, [int]$sampleRate, [int16]$bitsPerSample, [int16]$channels, [int16[]]$samples) {
    $bytesPerSample = $bitsPerSample / 8
    $dataSize = $samples.Length * $bytesPerSample * $channels
    $fs = [System.IO.File]::Open($path, [System.IO.FileMode]::Create)
    $bw = New-Object System.IO.BinaryWriter($fs)
    $bw.Write([System.Text.Encoding]::ASCII.GetBytes("RIFF"))
    $bw.Write(36 + $dataSize)
    $bw.Write([System.Text.Encoding]::ASCII.GetBytes("WAVE"))
    $bw.Write([System.Text.Encoding]::ASCII.GetBytes("fmt "))
    $bw.Write(16)
    $bw.Write([int16]1) # PCM
    $bw.Write([int16]$channels)
    $bw.Write([int32]$sampleRate)
    $bw.Write([int32]($sampleRate * $channels * $bytesPerSample))
    $bw.Write([int16]($channels * $bytesPerSample))
    $bw.Write([int16]$bitsPerSample)
    $bw.Write([System.Text.Encoding]::ASCII.GetBytes("data"))
    $bw.Write([int32]$dataSize)
    foreach ($s in $samples) { $bw.Write([int16]$s) }
    $bw.Flush(); $bw.Close(); $fs.Close()
}

function Sine($freq, $lengthSec, $sampleRate, $decay = 1.0) {
    $total = [int]($sampleRate * $lengthSec)
    $arr = New-Object 'System.Int16[]' $total
    for ($i = 0; $i -lt $total; $i++) {
        $t = $i / $sampleRate
        $env = [math]::Exp(-$decay * $t)
        $v = [math]::Sin(2 * [math]::PI * $freq * $t) * $env
        $arr[$i] = [int16]([math]::Round($v * 32767))
    }
    return $arr
}

function Noise($lengthSec, $sampleRate) {
    $r = New-Object System.Random
    $total = [int]($sampleRate * $lengthSec)
    $arr = New-Object 'System.Int16[]' $total
    for ($i = 0; $i -lt $total; $i++) { $arr[$i] = [int16]($r.Next(-32768,32767)) }
    return $arr
}

$sr = 22050

# Kick: low sine with strong decay
$kick = Sine 60 0.4 $sr 6.0
Write-Wav (Join-Path $base 'kick.wav') $sr 16 1 $kick

# Snare: short noise burst filtered by envelope
$sn = Noise 0.25 $sr
# apply envelope
for ($i=0; $i -lt $sn.Length; $i++) { $t = $i / $sr; $env = [math]::Exp(-18*$t); $sn[$i] = [int16]([math]::Round($sn[$i] * $env)) }
Write-Wav (Join-Path $base 'snare.wav') $sr 16 1 $sn

# Hihat: very short high-frequency noise
$hh = Noise 0.12 $sr
for ($i=0; $i -lt $hh.Length; $i++) { $t = $i / $sr; $env = [math]::Exp(-40*$t); $hh[$i] = [int16]([math]::Round($hh[$i] * $env / 2)) }
Write-Wav (Join-Path $base 'hihat.wav') $sr 16 1 $hh

Write-Host "Generated synthesized samples in $base"

