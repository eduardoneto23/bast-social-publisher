# Remove o servico do Windows. Nao apaga videos, banco de dados nem configuracoes.
#Requires -RunAsAdministrator
$name = 'BastSocialPublisher'
if (Get-Service $name -ErrorAction SilentlyContinue) {
    Stop-Service $name -ErrorAction SilentlyContinue
    sc.exe delete $name
} else {
    Write-Host 'Servico nao instalado.'
}
