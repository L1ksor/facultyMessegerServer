param(
    [int]$Port = 3000
)

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$url = "http://localhost:$Port/"

Write-Host "Starting Faculty Messenger Debug UI on $url" -ForegroundColor Cyan
Write-Host "Serving directory: $root" -ForegroundColor DarkGray
Write-Host "Press Ctrl+C to stop." -ForegroundColor Yellow

$listener = [System.Net.HttpListener]::new()
$listener.Prefixes.Add($url)
$listener.Start()

$mimeMap = @{
    ".html" = "text/html; charset=utf-8"
    ".css"  = "text/css; charset=utf-8"
    ".js"   = "application/javascript; charset=utf-8"
    ".csv"  = "text/csv; charset=utf-8"
    ".json" = "application/json; charset=utf-8"
    ".svg"  = "image/svg+xml"
    ".png"  = "image/png"
}

try {
    while ($listener.IsListening) {
        $ctx = $listener.GetContext()
        $reqPath = $ctx.Request.Url.AbsolutePath.TrimStart('/')
        if ([string]::IsNullOrWhiteSpace($reqPath)) { $reqPath = "index.html" }

        $safeRel = $reqPath -replace '\.\.', '' -replace '/', [IO.Path]::DirectorySeparatorChar
        $fullPath = [IO.Path]::GetFullPath((Join-Path $root $safeRel))

        if ($fullPath.StartsWith($root, [StringComparison]::OrdinalIgnoreCase) -and (Test-Path $fullPath -PathType Leaf)) {
            $ext = [IO.Path]::GetExtension($fullPath).ToLowerInvariant()
            $ctx.Response.ContentType = if ($mimeMap.ContainsKey($ext)) { $mimeMap[$ext] } else { "application/octet-stream" }
            $bytes = [IO.File]::ReadAllBytes($fullPath)
            $ctx.Response.ContentLength64 = $bytes.Length
            $ctx.Response.StatusCode = 200
            $ctx.Response.OutputStream.Write($bytes, 0, $bytes.Length)
        }
        else {
            $ctx.Response.StatusCode = 404
            $msg = [Text.Encoding]::UTF8.GetBytes("404 Not Found")
            $ctx.Response.OutputStream.Write($msg, 0, $msg.Length)
        }
        $ctx.Response.Close()
    }
}
finally {
    $listener.Stop()
}
