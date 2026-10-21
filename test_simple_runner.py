#!/usr/bin/env python3
"""
Simple test runner for Entity Analyzer
"""
import os
import sys
import subprocess

def run_test():
    print("Running Entity Analyzer tests...")
    
    # Change to project directory
    os.chdir(os.path.dirname(os.path.abspath(__file__)))
    
    # Check if we can import main
    try:
        import main
        print("✓ Successfully imported main module")
    except Exception as e:
        print(f"✗ Error importing main: {e}")
        return False
    
    # Run the test script directly
    try:
        result = subprocess.run([
            sys.executable, "tests/test_entity_analyzer.py"
        ], capture_output=True, text=True)
        
        print("Test output:")
        print(result.stdout)
        
        if result.stderr:
            print("Test errors:")
            print(result.stderr)
            
        return result.returncode == 0
    except Exception as e:
        print(f"Error running tests: {e}")
        return False

if __name__ == "__main__":
    success = run_test()
    sys.exit(0 if success else 1)