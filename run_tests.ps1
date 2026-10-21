# PowerShell script to run tests
$ErrorActionPreference = "Stop"

# Change to project directory
Set-Location $PSScriptRoot

# Install requirements
Write-Host "Installing requirements..."
& .venv_test\Scripts\python.exe -m pip install -r requirements.txt

# Run tests
Write-Host "Running tests..."
& .venv_test\Scripts\python.exe -m pytest tests/ -v --cov=entity_analyzer --cov-report=html

Write-Host "Tests completed successfully!"