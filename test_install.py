import subprocess
import sys
import os

# Change to the project directory
os.chdir(os.path.dirname(os.path.abspath(__file__)))

# Create venv
result = subprocess.run([sys.executable, "-m", "venv", ".venv_test"], capture_output=True, text=True)
print("Venv creation:", result.returncode, result.stdout, result.stderr)

# Install wheel
venv_python = os.path.join(".venv_test", "Scripts", "python.exe")
result = subprocess.run([venv_python, "-m", "pip", "install", "dist/entity_analyzer-18.1.0-py3-none-any.whl"], capture_output=True, text=True)
print("Install:", result.returncode, result.stdout, result.stderr)

# Test import
result = subprocess.run([venv_python, "-c", "import main; print('Import OK')"], capture_output=True, text=True)
print("Import test:", result.returncode, result.stdout, result.stderr)