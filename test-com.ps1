try {
    $obj = New-Object -ComObject "EliteSoft.Erwin.AddIn"
    Write-Host "SUCCESS: COM object created"
} catch {
    Write-Host "FAILED:"
    Write-Host $_.Exception.GetType().FullName
    Write-Host $_.Exception.Message
    if ($_.Exception.InnerException) {
        Write-Host "Inner: $($_.Exception.InnerException.Message)"
    }
    Write-Host "HResult: $($_.Exception.HResult)"
}
