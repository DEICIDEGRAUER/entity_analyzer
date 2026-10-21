#!/usr/bin/env python3
"""
Simple test runner for Entity Analyzer v18.1
This script tests the basic functionality without requiring pytest.
"""
import os
import sys
import tempfile
import shutil
from pathlib import Path

# Add the project root to the path
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

def test_basic_functionality():
    """Test basic Entity Analyzer functionality"""
    print("Testing Entity Analyzer v18.1...")
    
    # Test 1: Check if main.py exists and can be imported
    try:
        import main
        print("✓ Successfully imported main module")
    except Exception as e:
        print(f"✗ Error importing main: {e}")
        return False
    
    # Test 2: Check if config.py exists and can be imported
    try:
        import config
        print("✓ Successfully imported config module")
    except Exception as e:
        print(f"✗ Error importing config: {e}")
        return False
    
    # Test 3: Check if all required modules can be imported
    required_modules = [
        'processor', 'output', 'quality', 'cache', 
        'noise_detector', 'filters', 'context', 'entities',
        'limits', 'semantic', 'normalizer', 'name_generator',
        'translator', 'resolver', 'density', 'discovery'
    ]
    
    for module_name in required_modules:
        try:
            __import__(module_name)
            print(f"✓ Successfully imported {module_name} module")
        except Exception as e:
            print(f"✗ Error importing {module_name}: {e}")
            return False
    
    # Test 4: Check if setup.py exists
    if os.path.exists('setup.py'):
        print("✓ setup.py exists")
    else:
        print("✗ setup.py not found")
        return False
    
    # Test 5: Check if README.md exists
    if os.path.exists('README.md'):
        print("✓ README.md exists")
    else:
        print("✗ README.md not found")
        return False
    
    # Test 6: Check if CHANGELOG.md exists
    if os.path.exists('CHANGELOG.md'):
        print("✓ CHANGELOG.md exists")
    else:
        print("✗ CHANGELOG.md not found")
        return False
    
    # Test 7: Check if .gitignore exists
    if os.path.exists('.gitignore'):
        print("✓ .gitignore exists")
    else:
        print("✗ .gitignore not found")
        return False
    
    # Test 8: Check if tests directory exists
    if os.path.exists('tests'):
        print("✓ tests directory exists")
        
        # Check if test_entity_analyzer.py exists
        test_file = os.path.join('tests', 'test_entity_analyzer.py')
        if os.path.exists(test_file):
            print("✓ test_entity_analyzer.py exists")
        else:
            print("✗ test_entity_analyzer.py not found")
            return False
    else:
        print("✗ tests directory not found")
        return False
    
    # Test 9: Check if dist directory exists (built distribution)
    if os.path.exists('dist'):
        print("✓ dist directory exists (distribution built)")
        
        # Check if wheel file exists
        wheel_files = [f for f in os.listdir('dist') if f.endswith('.whl')]
        if wheel_files:
            print(f"✓ Wheel file found: {wheel_files[0]}")
        else:
            print("✗ No wheel file found in dist directory")
            return False
    else:
        print("✗ dist directory not found")
        return False
    
    # Test 10: Check if .venv_test directory exists
    if os.path.exists('.venv_test'):
        print("✓ .venv_test directory exists (virtual environment)")
        
        # Check if python.exe exists in .venv_test\Scripts
        venv_python = os.path.join('.venv_test', 'Scripts', 'python.exe')
        if os.path.exists(venv_python):
            print("✓ Virtual environment Python executable found")
        else:
            print("✗ Virtual environment Python executable not found")
            return False
    else:
        print("✗ .venv_test directory not found")
        return False
    
    return True

def test_preset_functionality():
    """Test preset functionality"""
    print("\nTesting preset functionality...")
    
    try:
        import config
        
        # Test valid presets
        valid_presets = ["minimal", "light", "medium", "extended", "heavy", "maximum", "titanic"]
        
        for preset in valid_presets:
            try:
                cfg = config.load_config(preset)
                if cfg and cfg.get("preset") == preset:
                    print(f"✓ Preset '{preset}' loaded successfully")
                else:
                    print(f"✗ Preset '{preset}' failed to load")
                    return False
            except Exception as e:
                print(f"✗ Error loading preset '{preset}': {e}")
                return False
        
        # Test invalid preset
        try:
            cfg = config.load_config("invalid_preset")
            print("✗ Invalid preset should have raised an exception")
            return False
        except Exception:
            print("✓ Invalid preset correctly raised exception")
        
        return True
        
    except Exception as e:
        print(f"✗ Error testing preset functionality: {e}")
        return False

def test_file_processing():
    """Test file processing functionality"""
    print("\nTesting file processing functionality...")
    
    try:
        import processor
        
        # Create a temporary test file
        with tempfile.NamedTemporaryFile(mode='w', suffix='.txt', delete=False) as f:
            f.write("Наг и Йеб - это важная сущность.\n")
            f.write("Ктулху - древняя сущность.\n")
            f.write("Азатот - еще одна сущность.\n")
            temp_file = f.name
        
        try:
            # Test file reading
            content = processor.read_file_fast(temp_file)
            if content and "Наг и Йеб" in content:
                print("✓ File reading works correctly")
            else:
                print("✗ File reading failed")
                return False
            
            # Test UTF-16 file reading
            with tempfile.NamedTemporaryFile(mode='w', suffix='.txt', delete=False) as f:
                f.write("Наг и Йеб - это важная сущность.\n")
                f.write("Ктулху - древняя сущность.\n")
                utf16_file = f.name
            
            # Convert to UTF-16
            with open(utf16_file, 'r', encoding='utf-8') as f_in:
                content = f_in.read()
            with open(utf16_file, 'w', encoding='utf-16') as f_out:
                f_out.write(content)
            
            try:
                utf16_content = processor.read_file_fast(utf16_file)
                if utf16_content and "Наг и Йеб" in utf16_content:
                    print("✓ UTF-16 file reading works correctly")
                else:
                    print("✗ UTF-16 file reading failed")
                    return False
            finally:
                os.unlink(utf16_file)
            
        finally:
            os.unlink(temp_file)
        
        return True
        
    except Exception as e:
        print(f"✗ Error testing file processing: {e}")
        return False

def main():
    """Main test runner"""
    print("=" * 60)
    print("Entity Analyzer v18.1 - Test Suite")
    print("=" * 60)
    
    tests = [
        ("Basic Functionality", test_basic_functionality),
        ("Preset Functionality", test_preset_functionality),
        ("File Processing", test_file_processing),
    ]
    
    passed = 0
    failed = 0
    
    for test_name, test_func in tests:
        print(f"\n{'='*60}")
        print(f"Running: {test_name}")
        print(f"{'='*60}")
        
        try:
            if test_func():
                print(f"✓ {test_name} PASSED")
                passed += 1
            else:
                print(f"✗ {test_name} FAILED")
                failed += 1
        except Exception as e:
            print(f"✗ {test_name} FAILED with exception: {e}")
            failed += 1
    
    print(f"\n{'='*60}")
    print("Test Results Summary")
    print(f"{'='*60}")
    print(f"Total tests: {len(tests)}")
    print(f"Passed: {passed}")
    print(f"Failed: {failed}")
    print(f"Success rate: {passed/len(tests)*100:.1f}%")
    
    if failed == 0:
        print("\n🎉 All tests passed! Entity Analyzer v18.1 is ready for release.")
        return 0
    else:
        print(f"\n❌ {failed} test(s) failed. Please review the issues above.")
        return 1

if __name__ == "__main__":
    sys.exit(main())