#!/usr/bin/env python3
"""
Test runner for Entity Analyzer v18.1
"""
import os
import sys
import subprocess
import argparse
from pathlib import Path

def run_command(cmd, cwd=None):
    """Run a command and return result"""
    print(f"Running: {cmd}")
    try:
        result = subprocess.run(
            cmd,
            shell=True,
            cwd=cwd,
            capture_output=True,
            text=True
        )
        print(f"Exit code: {result.returncode}")
        if result.stdout:
            print(f"STDOUT:\n{result.stdout}")
        if result.stderr:
            print(f"STDERR:\n{result.stderr}")
        return result.returncode == 0
    except Exception as e:
        print(f"Error running command: {e}")
        return False

def main():
    parser = argparse.ArgumentParser(description="Run Entity Analyzer tests")
    parser.add_argument("--venv", default=".venv_test", help="Virtual environment path")
    parser.add_argument("--pytest-args", default="-v", help="Additional pytest arguments")
    
    args = parser.parse_args()
    
    # Change to project directory
    project_dir = Path(__file__).parent
    os.chdir(project_dir)
    
    # Check if virtual environment exists
    venv_python = Path(args.venv) / "Scripts" / "python.exe"
    if not venv_python.exists():
        print(f"Virtual environment not found at {venv_python}")
        print("Creating virtual environment...")
        run_command(f"{sys.executable} -m venv {args.venv}")
    
    # Install requirements
    print("\nInstalling requirements...")
    if not run_command(f"{venv_python} -m pip install -r requirements.txt"):
        print("Failed to install requirements")
        sys.exit(1)
    
    # Install pytest
    print("\nInstalling pytest...")
    if not run_command(f"{venv_python} -m pip install pytest pytest-cov"):
        print("Failed to install pytest")
        sys.exit(1)
    
    # Run tests
    print("\nRunning tests...")
    test_cmd = f"{venv_python} -m pytest tests/ {args.pytest_args}"
    if not run_command(test_cmd):
        print("Tests failed")
        sys.exit(1)
    
    print("\nAll tests passed!")

if __name__ == "__main__":
    main()