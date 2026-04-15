Get-ChildItem -Path . -Recurse -Filter *.cs | ForEach-Object {
    $content = Get-Content $_.FullName -Raw
    $updated = $content -replace '\[Authorize\(Policy = "RequiresElevation"\)\]', '[Authorize]'
    if ($content -ne $updated) {
        Set-Content $_.FullName $updated
        Write-Host "Fixed: $($_.FullName)"
    }
}
