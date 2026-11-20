# Solo ensayo: genera JPEG sintéticos (ruido) de tamaños conocidos en .estado/. Sin datos reales.
param([string]$Salida = (Join-Path $PSScriptRoot '.estado'))
Add-Type -AssemblyName System.Drawing
New-Item -ItemType Directory -Force -Path $Salida | Out-Null
function Nueva([string]$Nombre, [int]$Lado, [int]$MinBytes, [int]$MaxBytes) {
    $rnd = [Random]::new(7)
    $bmp = [System.Drawing.Bitmap]::new($Lado, $Lado)
    $datos = [byte[]]::new($Lado * $Lado * 4); $rnd.NextBytes($datos)
    for ($i = 3; $i -lt $datos.Length; $i += 4) { $datos[$i] = 255 }
    $bits = $bmp.LockBits([System.Drawing.Rectangle]::new(0,0,$Lado,$Lado), 'WriteOnly', 'Format32bppArgb')
    [Runtime.InteropServices.Marshal]::Copy($datos, 0, $bits.Scan0, $datos.Length); $bmp.UnlockBits($bits)
    $codec = [System.Drawing.Imaging.ImageCodecInfo]::GetImageEncoders() | Where-Object MimeType -eq 'image/jpeg'
    $ruta = Join-Path $Salida $Nombre
    foreach ($q in 100,98,95,90,85,80,70,60,50,40,30,20) {
        $p = [System.Drawing.Imaging.EncoderParameters]::new(1)
        $p.Param[0] = [System.Drawing.Imaging.EncoderParameter]::new([System.Drawing.Imaging.Encoder]::Quality, [long]$q)
        $bmp.Save($ruta, $codec, $p)
        $t = (Get-Item $ruta).Length
        if ($t -le $MaxBytes) { break }
    }
    $bmp.Dispose()
    "{0}: {1:N0} bytes ({2:N2} MiB)" -f $Nombre, $t, ($t / 1MB)
    if ($t -lt $MinBytes -or $t -gt $MaxBytes) { throw "$Nombre fuera de rango: $t" }
}
Nueva 'foto-valida.jpg'     1000 1MB 2MB
Nueva 'foto-2_5MiB.jpg'     1700 2200000 2900000
Nueva 'foto-5MiB.jpg'       2400 4500000 6500000
