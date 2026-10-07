$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Drawing
$taskRoot=Split-Path -Parent $PSScriptRoot
$assetDirectory=Join-Path $taskRoot 'src\GlanceNext.Desktop\Assets'
New-Item -ItemType Directory -Force $assetDirectory | Out-Null
$bitmap=[System.Drawing.Bitmap]::new(64,64)
$graphics=[System.Drawing.Graphics]::FromImage($bitmap)
$graphics.SmoothingMode=[System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$graphics.Clear([System.Drawing.Color]::Transparent)
$background=[System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#087D71'))
$shape=[System.Drawing.Drawing2D.GraphicsPath]::new()
$shape.AddArc(2,2,20,20,180,90);$shape.AddArc(42,2,20,20,270,90)
$shape.AddArc(42,42,20,20,0,90);$shape.AddArc(2,42,20,20,90,90);$shape.CloseFigure()
$graphics.FillPath($background,$shape)
$pen=[System.Drawing.Pen]::new([System.Drawing.Color]::White,3.5)
$pen.LineJoin=[System.Drawing.Drawing2D.LineJoin]::Round
$points=[System.Drawing.PointF[]]@([System.Drawing.PointF]::new(32,13),[System.Drawing.PointF]::new(49,20),[System.Drawing.PointF]::new(46,39),[System.Drawing.PointF]::new(32,52),[System.Drawing.PointF]::new(18,39),[System.Drawing.PointF]::new(15,20))
$graphics.DrawPolygon($pen,$points);$graphics.DrawEllipse($pen,27,27,10,10)
$icon=[System.Drawing.Icon]::FromHandle($bitmap.GetHicon())
$stream=[System.IO.File]::Create((Join-Path $assetDirectory 'glancenext.ico'))
try{$icon.Save($stream)}finally{$stream.Dispose();$icon.Dispose();$pen.Dispose();$shape.Dispose();$background.Dispose();$graphics.Dispose();$bitmap.Dispose()}
