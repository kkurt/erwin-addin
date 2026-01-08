$conn = New-Object System.Data.SqlClient.SqlConnection
$conn.ConnectionString = "Server=localhost,1433;Database=Fiba;User Id=sa;Password=Elite12345;Connection Timeout=5;"

try {
    $conn.Open()
    Write-Host "Connected to Fiba database!"

    $cmd = $conn.CreateCommand()

    # Count rows
    $cmd.CommandText = "SELECT COUNT(*) FROM [dbo].[GLOSSARY]"
    $count = $cmd.ExecuteScalar()
    Write-Host "GLOSSARY table has $count rows"

    # Sample data
    Write-Host "`nSample data:"
    $cmd.CommandText = "SELECT TOP 10 [NAME], [DATA_TYPE], [OWNER] FROM [dbo].[GLOSSARY]"
    $reader = $cmd.ExecuteReader()
    while ($reader.Read()) {
        $name = $reader["NAME"]
        $dataType = $reader["DATA_TYPE"]
        $owner = $reader["OWNER"]
        Write-Host "  Name: '$name', DataType: '$dataType', Owner: '$owner'"
    }
    $reader.Close()

    $conn.Close()
} catch {
    Write-Host "Error: $_"
}
