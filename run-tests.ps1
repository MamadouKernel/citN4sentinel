param(
    [switch]$Coverage = $false
)

$args = @("test")
if ($Coverage) {
    $args += "/p:CollectCoverage=true"
    $args += "/p:CoverletOutputFormat=cobertura"
    $args += "/p:CoverletOutput=./coverage.cobertura.xml"
}

dotnet @args
