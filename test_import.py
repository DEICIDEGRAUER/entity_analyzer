import sys
print('Python executable:', sys.executable)
print('Python version:', sys.version)

# Try to import the main module
try:
    import main
    print('Successfully imported main module')
except Exception as e:
    print('Error importing main:', e)
    import traceback
    traceback.print_exc()

# Try to run pytest
import subprocess
result = subprocess.run([sys.executable, '-m', 'pytest', 'tests/test_entity_analyzer.py', '-v'], 
                       capture_output=True, text=True)
print('Pytest exit code:', result.returncode)
print('Pytest stdout:', result.stdout)
print('Pytest stderr:', result.stderr)