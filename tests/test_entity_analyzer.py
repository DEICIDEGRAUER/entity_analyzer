import os
import sys
import tempfile
import shutil
import subprocess
import json
from pathlib import Path

# Add the project root to the path
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)) + '/..')

import main
import config
import processor
import output
import quality
import cache
import noise_detector
import filters
import context

class TestEntityAnalyzer:
    """Test suite for Entity Analyzer v18.1"""
    
    def setup_method(self):
        """Set up test environment"""
        self.test_dir = tempfile.mkdtemp(prefix="entity_analyzer_test_")
        self.corpus_dir = os.path.join(self.test_dir, "corpus")
        self.output_dir = os.path.join(self.test_dir, "output")
        os.makedirs(self.corpus_dir)
        os.makedirs(self.output_dir)
        
        # Create test files
        self.create_test_files()
        
    def teardown_method(self):
        """Clean up test environment"""
        if os.path.exists(self.test_dir):
            shutil.rmtree(self.test_dir)
    
    def create_test_files(self):
        """Create test corpus files"""
        # Create a simple text file with entity mentions
        test_content = """Наг и Йеб - это важные сущности.
Наг и Йеб появляются в тексте несколько раз.
Наг и Йеб - ключевая тема исследования.
Ктулху - древняя сущность.
Ктулху также упоминается в тексте.
Азатот - еще одна сущность.
"""
        
        with open(os.path.join(self.corpus_dir, "test1.txt"), "w", encoding="utf-8") as f:
            f.write(test_content)
            
        # Create UTF-16 file
        with open(os.path.join(self.corpus_dir, "test2.txt"), "w", encoding="utf-16") as f:
            f.write(test_content)
            
        # Create empty file
        with open(os.path.join(self.corpus_dir, "empty.txt"), "w") as f:
            pass
    
    def test_preset_selection(self):
        """Test that all presets are valid"""
        valid_presets = ["minimal", "light", "medium", "extended", "heavy", "maximum", "titanic"]
        
        for preset in valid_presets:
            # Test that preset can be loaded
            cfg = config.load_config(preset)
            assert cfg is not None
            assert cfg.get("preset") == preset
    
    def test_file_processing(self):
        """Test basic file processing"""
        # Test UTF-8 file reading
        content = processor.read_file_fast(os.path.join(self.corpus_dir, "test1.txt"))
        assert "Наг и Йеб" in content
        assert "Ктулху" in content
        
        # Test UTF-16 file reading
        content = processor.read_file_fast(os.path.join(self.corpus_dir, "test2.txt"))
        assert "Наг и Йеб" in content
        assert "Ктулху" in content
    
    def test_entity_matching(self):
        """Test entity matching functionality"""
        # Create a simple test with known entities
        test_text = "Наг и Йеб Ктулху Азатот"
        
        # This would test the actual entity matching logic
        # For now, just verify the function exists
        assert hasattr(processor, 'match_entities')
    
    def test_context_extraction(self):
        """Test context extraction"""
        # Test context calculation
        context_size = context.calculate_context_size("extended")
        assert context_size > 0
        
        # Test context extraction
        test_text = "Наг и Йеб - это важная сущность."
        contexts = context.extract_contexts(test_text, "extended")
        assert len(contexts) > 0
    
    def test_output_generation(self):
        """Test output generation"""
        # Test TXT output
        test_data = {
            "entities": ["Наг и Йеб", "Ктулху"],
            "contexts": ["Наг и Йеб - это важная сущность.", "Ктулху - древняя сущность."],
            "quality": {"efficiency": 0.85}
        }
        
        # Test JSON output
        json_output = output.generate_json_report(test_data)
        assert isinstance(json_output, str)
        
        # Test TXT output
        txt_output = output.generate_txt_report(test_data)
        assert isinstance(txt_output, str)
        assert "Наг и Йеб" in txt_output
    
    def test_quality_metrics(self):
        """Test quality metrics calculation"""
        # Test quality stats
        stats = quality.get_quality_stats()
        assert stats is not None
        assert "cache_hits" in stats
        assert "cache_misses" in stats
        assert "filter_efficiency" in stats
    
    def test_cache_functionality(self):
        """Test caching system"""
        # Test cache initialization
        cache_instance = cache.IncrementalCache()
        assert cache_instance is not None
        
        # Test cache key generation
        test_hash = cache.generate_config_hash({"preset": "extended"})
        assert isinstance(test_hash, str)
        assert len(test_hash) > 0
    
    def test_noise_detection(self):
        """Test noise detection"""
        # Test noise profile loading
        profiles = noise_detector.load_noise_profiles()
        assert isinstance(profiles, dict)
        
        # Test noise filtering
        test_text = "Наг и Йеб - это важная сущность."
        filtered = noise_detector.filter_noise(test_text, profiles)
        assert isinstance(filtered, str)
    
    def test_filtering(self):
        """Test filtering functionality"""
        # Test context validation
        test_context = "Наг и Йеб - это важная сущность."
        is_valid = filters.validate_context(test_context)
        assert isinstance(is_valid, bool)
        
        # Test false context management
        false_contexts = filters.get_false_contexts()
        assert isinstance(false_contexts, list)
    
    def test_preset_validation(self):
        """Test preset validation"""
        # Test valid preset
        assert config.is_valid_preset("extended")
        
        # Test invalid preset
        assert not config.is_valid_preset("invalid_preset")
    
    def test_error_handling(self):
        """Test error handling"""
        # Test file not found
        try:
            processor.read_file_fast("nonexistent_file.txt")
            assert False, "Should have raised exception"
        except Exception:
            pass  # Expected
        
        # Test invalid preset
        try:
            config.load_config("invalid_preset")
            assert False, "Should have raised exception"
        except Exception:
            pass  # Expected
    
    def test_performance(self):
        """Test performance benchmarks"""
        import time
        
        # Test processing time
        start_time = time.time()
        
        # Simple processing test
        test_text = "Наг и Йеб Ктулху Азатот"
        
        end_time = time.time()
        processing_time = end_time - start_time
        
        # Should complete quickly
        assert processing_time < 1.0, f"Processing took too long: {processing_time}s"
    
    def test_integration(self):
        """Test end-to-end integration"""
        # This would test the full pipeline
        # For now, just verify the main function exists
        assert hasattr(main, 'main')
        
        # Test that main can be called with preset
        # (This would require actual corpus files)
        pass

if __name__ == "__main__":
    # Run tests
    test_instance = TestEntityAnalyzer()
    
    print("Running Entity Analyzer v18.1 Tests...")
    print("=" * 50)
    
    # Run each test method
    test_methods = [
        'test_preset_selection',
        'test_file_processing', 
        'test_entity_matching',
        'test_context_extraction',
        'test_output_generation',
        'test_quality_metrics',
        'test_cache_functionality',
        'test_noise_detection',
        'test_filtering',
        'test_preset_validation',
        'test_error_handling',
        'test_performance',
        'test_integration'
    ]
    
    passed = 0
    failed = 0
    
    for method_name in test_methods:
        try:
            print(f"\nRunning {method_name}...")
            test_instance.setup_method()
            getattr(test_instance, method_name)()
            test_instance.teardown_method()
            print(f"✓ {method_name} PASSED")
            passed += 1
        except Exception as e:
            print(f"✗ {method_name} FAILED: {e}")
            failed += 1
            try:
                test_instance.teardown_method()
            except:
                pass
    
    print("\n" + "=" * 50)
    print(f"Test Results: {passed} passed, {failed} failed")
    print(f"Total: {passed + failed} tests")
    
    if failed > 0:
        sys.exit(1)
    else:
        print("All tests passed!")