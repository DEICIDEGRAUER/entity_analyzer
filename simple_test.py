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
    try:
        result = subprocess.run([sys.executable, "main.py", "minimal"], 
                               capture_output=True, text=True, timeout=120)
        print("STDOUT (first 2000 chars):", result.stdout[:2000])
        print("STDERR (first 1000 chars):", result.stderr[:1000])
        print("Return code:", result.returncode)
        
        # Check if output was generated
        if os.path.exists('output'):
            print("Output directory exists")
            for root, dirs, files in os.walk('output'):
                level = root.replace('output', '').count(os.sep)
                indent = ' ' * 2 * level
                print(f'{indent}{os.path.basename(root)}/')
                subindent = ' ' * 2 * (level + 1)
                for file in files:
                    print(f'{subindent}{file}')
        else:
            print("Output directory not found")
            
    except subprocess.TimeoutExpired:
        print("Test timed out after 120 seconds")
    except Exception as e:
        print(f"Error running test: {e}")
else:
    print("\nmain.py not found")