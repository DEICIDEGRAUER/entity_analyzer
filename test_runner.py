import os
import subprocess
import sys

print("Current working directory:", os.getcwd())
print("Files in current directory:")
for f in os.listdir('.'):
    print(f"  {f}")

# Check if main.py exists
if os.path.exists('main.py'):
    print("\nmain.py exists - can run tests")
    
    # Test 1: Run with minimal preset (non-interactive)
    print("\n=== Testing minimal preset ===")
    result = subprocess.run([sys.executable, "main.py", "minimal"], 
                           capture_output=True, text=True, timeout=120)
    print("STDOUT:", result.stdout[-3000:] if len(result.stdout) > 3000 else result.stdout)
    print("STDERR:", result.stderr[-2000:] if len(result.stderr) > 2000 else result.stderr)
    print("Return code:", result.returncode)
else:
    print("\nmain.py not found - need to locate it")
    # Try to find it
    for root, dirs, files in os.walk('/'):
        if 'main.py' in files:
            print(f"Found main.py at: {root}/main.py")
            break