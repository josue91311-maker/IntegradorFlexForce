<#
.SYNOPSIS
    Script de instalación como Servicio de Windows (On-Premise) para IntegradorFlexForce.
.DESCRIPTION
    Compila y registra IntegradorFlexForce como un Servicio de Windows con inicio automático
    y reinicio ante fallos.
#>

param(
    [string]$ServiceName = "IntegradorFlexForce",
    [string]$DisplayName = "Integrador FlexForce - Sincronizador ERP",
    [string]$Port = "5050"
)

$ErrorActionPreference = "Stop"

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "  Instalador de Servicio On-Premise: $ServiceName" -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
$PublishDir = Join-Path $ScriptDir "publish"

Write-Host "[1/4] Compilando y publicando en modo Release..." -ForegroundColor Yellow
dotnet publish "$ScriptDir\IntegradorFlexForce.csproj" -c Release -o $PublishDir --self-contained false

$ExePath = Join-Path $PublishDir "IntegradorFlexForce.exe"
if (-not (Test-Path $ExePath)) {
    throw "No se encontró el ejecutable en: $ExePath"
}

Write-Host "[2/4] Deteniendo servicio anterior si existiese..." -ForegroundColor Yellow
$ExistingService = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($ExistingService) {
    if ($ExistingService.Status -eq 'Running') {
        Stop-Service -Name $ServiceName -Force
    }
    sc.exe delete $ServiceName
    Start-Sleep -Seconds 2
}

Write-Host "[3/4] Creando nuevo Servicio de Windows..." -ForegroundColor Yellow
$BinPath = "`"$ExePath`" --urls=http://*:$Port"
sc.exe create $ServiceName binPath= $BinPath start= auto DisplayName= $DisplayName

# Configurar reinicio automático ante fallos
sc.exe failure $ServiceName reset= 86400 actions= restart/60000/restart/60000/restart/60000

Write-Host "[4/4] Iniciando el servicio..." -ForegroundColor Yellow
Start-Service -Name $ServiceName

Write-Host "==========================================================" -ForegroundColor Green
Write-Host "  Servicio $ServiceName instalado y corriendo con éxito!" -ForegroundColor Green
Write-Host "  Portal Web disponible en: http://localhost:$Port" -ForegroundColor Green
Write-Host "==========================================================" -ForegroundColor Green
