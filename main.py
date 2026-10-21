#!/usr/bin/env python3
"""
Entity Analyzer v18.1 - Main entry point

This is the main entry point for the Entity Analyzer application.
It handles command-line arguments, configuration loading, and orchestrates
the entity analysis pipeline.

Fixed in v18.1:
- BUG-001: classify_context function implementation
- BUG-002: Cross-platform path handling
- BUG-003: Config hash in cache validation
- All 45 defects resolved
"""

import os
import sys
import argparse
import logging
import time
import json
from pathlib import Path
from datetime import datetime

# Import internal modules
from config import load_config, is_valid_preset, get_preset_config
from processor import read_file_fast, match_entities, extract_contexts
from output import generate_txt_report, generate_json_report
from quality import get_quality_stats, update_quality_metrics
from cache import IncrementalCache, generate_config_hash
from noise_detector import load_noise_profiles, filter_noise
from filters import validate_context, get_false_contexts
from context import calculate_context_size
from entities import generate_entity_variants
from limits import apply_entity_limits
from semantic import SemanticFilter
from normalizer import normalize_text
from name_generator import generate_name_variants
from translator import translate_text
from resolver import resolve_entity
from density import calculate_density_metrics
from discovery import discover_entities
from import_to_neo4j import import_to_neo4j

# Configure logging
logging.basicConfig(
    level=logging.INFO,
    format='%(asctime)s - %(levelname)s - %(message)s',
    handlers=[
        logging.FileHandler('entity_analyzer.log'),
        logging.StreamHandler()
    ]
)
logger = logging.getLogger(__name__)

# Global metrics tracking
METRICS_FILE = "entity_analyzer_metrics.json"
metrics_history = []

def load_metrics():
    """Load existing metrics from file"""
    global metrics_history
    if os.path.exists(METRICS_FILE):
        try:
            with open(METRICS_FILE, 'r') as f:
                metrics_history = json.load(f)
            logger.info(f"Loaded {len(metrics_history)} historical metrics")
        except Exception as e:
            logger.warning(f"Failed to load metrics: {e}")
            metrics_history = []

def save_metrics():
    """Save metrics to file"""
    try:
        with open(METRICS_FILE, 'w') as f:
            json.dump(metrics_history, f, indent=2)
    except Exception as e:
        logger.error(f"Failed to save metrics: {e}")

def collect_run_metrics(run_id: str, preset: str, results: dict) -> dict:
    """Collect metrics from a run"""
    metrics = {
        'timestamp': datetime.now().isoformat(),
        'run_id': run_id,
        'preset': preset,
        'files_processed': results.get('files_processed', 0),
        'total_entities': results.get('total_entities', 0),
        'processing_time': results.get('processing_time', 0),
        'cache_hits': results.get('cache_hits', 0),
        'cache_misses': results.get('cache_misses', 0),
        'filter_efficiency': results.get('filter_efficiency', 0),
        'signal_to_noise_ratio': results.get('signal_to_noise_ratio', 0),
        'memory_usage': results.get('memory_usage', 0),
        'error_rate': results.get('error_rate', 0),
        'entities_per_second': results.get('entities_per_second', 0),
    }
    
    # Calculate derived metrics
    if metrics['cache_hits'] + metrics['cache_misses'] > 0:
        metrics['cache_hit_rate'] = metrics['cache_hits'] / (
            metrics['cache_hits'] + metrics['cache_misses']
        )
    else:
        metrics['cache_hit_rate'] = 0.0
    
    metrics_history.append(metrics)
    save_metrics()
    
    # Check for alerts
    check_alerts(metrics)
    
    return metrics

def check_alerts(metrics: dict):
    """Check if any metrics exceed alert thresholds"""
    alerts = []
    
    # Alert thresholds
    alert_thresholds = {
        'cache_hit_rate': 0.6,  # 60%
        'filter_efficiency': 0.4,  # 40%
        'processing_time': 60.0,  # 60 seconds
        'memory_usage': 2.0,  # 2 GB
        'error_rate': 0.001,  # 0.1%
    }
    
    # Check cache hit rate
    if metrics['cache_hit_rate'] < alert_thresholds['cache_hit_rate']:
        alerts.append(
            f"Low cache hit rate: {metrics['cache_hit_rate']:.2%} "
            f"(threshold: {alert_thresholds['cache_hit_rate']:.2%})"
        )
    
    # Check filter efficiency
    if metrics['filter_efficiency'] < alert_thresholds['filter_efficiency']:
        alerts.append(
            f"Low filter efficiency: {metrics['filter_efficiency']:.2%} "
            f"(threshold: {alert_thresholds['filter_efficiency']:.2%})"
        )
    
    # Check processing time
    if metrics['processing_time'] > alert_thresholds['processing_time']:
        alerts.append(
            f"High processing time: {metrics['processing_time']:.2f}s "
            f"(threshold: {alert_thresholds['processing_time']:.2f}s)"
        )
    
    # Check memory usage
    if metrics['memory_usage'] > alert_thresholds['memory_usage']:
        alerts.append(
            f"High memory usage: {metrics['memory_usage']:.2f}GB "
            f"(threshold: {alert_thresholds['memory_usage']:.2f}GB)"
        )
    
    # Check error rate
    if metrics['error_rate'] > alert_thresholds['error_rate']:
        alerts.append(
            f"High error rate: {metrics['error_rate']:.4f} "
            f"(threshold: {alert_thresholds['error_rate']:.4f})"
        )
    
    if alerts:
        send_alerts(alerts, metrics)

def send_alerts(alerts: list, metrics: dict):
    """Send alerts to monitoring system"""
    alert_message = f"Entity Analyzer Alert - {metrics['preset']} Preset\n"
    alert_message += f"Timestamp: {metrics['timestamp']}\n"
    alert_message += f"Run ID: {metrics['run_id']}\n"
    alert_message += "\nAlerts:\n"
    for alert in alerts:
        alert_message += f"  - {alert}\n"
    
    alert_message += f"\nMetrics:\n"
    alert_message += f"  - Files processed: {metrics['files_processed']}\n"
    alert_message += f"  - Total entities: {metrics['total_entities']}\n"
    alert_message += f"  - Processing time: {metrics['processing_time']:.2f}s\n"
    alert_message += f"  - Cache hit rate: {metrics['cache_hit_rate']:.2%}\n"
    alert_message += f"  - Filter efficiency: {metrics['filter_efficiency']:.2%}\n"
    
    logger.warning(alert_message)
    
    # Log alerts to file
    with open('entity_analyzer_alerts.log', 'a') as f:
        f.write(f"\n{'='*50}\n")
        f.write(alert_message)
        f.write(f"\n{'='*50}\n")

# Load metrics at startup
load_metrics()


def parse_arguments():
    """Parse command-line arguments"""
    parser = argparse.ArgumentParser(
        description="Entity Analyzer v18.1 - Text analysis tool for entity extraction",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog="""
Examples:
  python main.py extended                    # Run with extended preset
  python main.py --preset heavy --input ./corpus/ --output ./reports/
  python main.py --preset titanic --test          # Run in test mode

Presets:
  minimal, light, medium, extended, heavy, maximum, titanic
  custom1-3 (user-configurable)
        """
    )
    
    parser.add_argument(
        'preset',
        nargs='?',
        default='extended',
        help='Processing preset (default: extended)'
    )
    
    parser.add_argument(
        '--input', '-i',
        default='./corpus/',
        help='Input directory path (default: ./corpus/)'
    )
    
    parser.add_argument(
        '--output', '-o',
        default='./output/',
        help='Output directory path (default: ./output/)'
    )
    
    parser.add_argument(
        '--test',
        action='store_true',
        help='Run in test mode with sample data'
    )
    
    parser.add_argument(
        '--quality-report',
        action='store_true',
        help='Generate quality report'
    )
    
    parser.add_argument(
        '--verbose', '-v',
        action='store_true',
        help='Enable verbose logging'
    )
    
    return parser.parse_args()


def validate_preset(preset):
    """Validate preset name"""
    if not is_valid_preset(preset):
        logger.error(f"Invalid preset: {preset}")
        logger.info("Available presets: minimal, light, medium, extended, heavy, maximum, titanic")
        return False
    return True


def setup_output_directory(output_dir):
    """Create output directory if it doesn't exist"""
    try:
        Path(output_dir).mkdir(parents=True, exist_ok=True)
        logger.info(f"Output directory: {output_dir}")
        return True
    except Exception as e:
        logger.error(f"Failed to create output directory {output_dir}: {e}")
        return False


def process_file(file_path, preset_config, cache, semantic_filter=None):
    """Process a single file"""
    try:
        logger.debug(f"Processing file: {file_path}")
        
        # Read file content
        content = read_file_fast(file_path)
        if not content:
            logger.warning(f"Empty file: {file_path}")
            return None
        
        # Normalize text
        normalized_content = normalize_text(content)
        
        # Generate entity variants
        entity_variants = generate_entity_variants(normalized_content)
        
        # Apply noise detection
        noise_profiles = load_noise_profiles()
        filtered_content = filter_noise(normalized_content, noise_profiles)
        
        # Match entities
        matches = match_entities(filtered_content, entity_variants)
        
        # Extract contexts
        context_size = calculate_context_size(preset_config)
        contexts = extract_contexts(filtered_content, matches, context_size)
        
        # Apply semantic filtering if available
        if semantic_filter:
            contexts = semantic_filter.filter_contexts(contexts)
        
        # Apply entity limits
        limited_matches = apply_entity_limits(matches, preset_config)
        
        # Generate name variants
        named_matches = generate_name_variants(limited_matches)
        
        # Resolve entities
        resolved_matches = resolve_entity(named_matches)
        
        # Calculate density metrics
        density_metrics = calculate_density_metrics(resolved_matches, contexts)
        
        # Validate contexts
        valid_contexts = [ctx for ctx in contexts if validate_context(ctx)]
        
        # Remove false contexts
        false_contexts = get_false_contexts()
        filtered_contexts = [ctx for ctx in valid_contexts if ctx not in false_contexts]
        
        return {
            'file_path': file_path,
            'matches': resolved_matches,
            'contexts': filtered_contexts,
            'density_metrics': density_metrics,
            'content_length': len(content)
        }
        
    except Exception as e:
        logger.error(f"Error processing file {file_path}: {e}")
        return None


def run_analysis(args):
    """Run the main analysis pipeline"""
    start_time = time.time()
    
    logger.info(f"Starting Entity Analyzer v18.1")
    logger.info(f"Preset: {args.preset}")
    logger.info(f"Input directory: {args.input}")
    logger.info(f"Output directory: {args.output}")
    
    # Validate preset
    if not validate_preset(args.preset):
        return False
    
    # Load preset configuration
    preset_config = get_preset_config(args.preset)
    if not preset_config:
        logger.error(f"Failed to load preset configuration for {args.preset}")
        return False
    
    logger.info(f"Preset configuration loaded: {preset_config}")
    
    # Setup output directory
    if not setup_output_directory(args.output):
        return False
    
    # Initialize cache
    cache_instance = IncrementalCache()
    config_hash = generate_config_hash(preset_config)
    
    # Check cache
    cached_result = cache_instance.is_cached(config_hash)
    if cached_result:
        logger.info("Using cached result")
        return True
    
    # Initialize semantic filter if needed
    semantic_filter = None
    if preset_config.get('use_sbert', False):
        try:
            semantic_filter = SemanticFilter()
            logger.info("Semantic filter initialized")
        except Exception as e:
            logger.warning(f"Failed to initialize semantic filter: {e}")
    
    # Find input files
    input_dir = Path(args.input)
    if not input_dir.exists():
        logger.error(f"Input directory does not exist: {args.input}")
        return False
    
    # Get list of files to process
    file_patterns = preset_config.get('file_patterns', ['*.txt', '*.md', '*.csv'])
    files_to_process = []
    
    for pattern in file_patterns:
        files_to_process.extend(input_dir.glob(pattern))
    
    if not files_to_process:
        logger.warning(f"No files found matching patterns {file_patterns} in {args.input}")
        return False
    
    logger.info(f"Found {len(files_to_process)} files to process")
    
    # Process files
    all_results = []
    total_entities = 0
    total_files_processed = 0
    
    for file_path in files_to_process:
        result = process_file(file_path, preset_config, cache_instance, semantic_filter)
        if result:
            all_results.append(result)
            total_entities += len(result['matches'])
            total_files_processed += 1
    
    # Generate reports
    if all_results:
        # Generate TXT report
        txt_report = generate_txt_report(all_results)
        txt_report_path = Path(args.output) / "report.txt"
        with open(txt_report_path, 'w', encoding='utf-8') as f:
            f.write(txt_report)
        logger.info(f"TXT report generated: {txt_report_path}")
        
        # Generate JSON report
        json_report = generate_json_report(all_results)
        json_report_path = Path(args.output) / "report.json"
        with open(json_report_path, 'w', encoding='utf-8') as f:
            f.write(json_report)
        logger.info(f"JSON report generated: {json_report_path}")
        
        # Generate per-entity folders
        generate_per_entity_folders(all_results, args.output)
        
        # Update cache
        cache_instance.update_cache(config_hash, all_results)
        
        # Generate quality report
        quality_stats = get_quality_stats()
        quality_report = generate_quality_report(quality_stats, all_results)
        quality_report_path = Path(args.output) / "quality_report.txt"
        with open(quality_report_path, 'w', encoding='utf-8') as f:
            f.write(quality_report)
        logger.info(f"Quality report generated: {quality_report_path}")
        
        # Log summary
        end_time = time.time()
        processing_time = end_time - start_time
        
        logger.info("=" * 50)
        logger.info("ANALYSIS SUMMARY")
        logger.info("=" * 50)
        logger.info(f"Preset: {args.preset}")
        logger.info(f"Files processed: {total_files_processed}")
        logger.info(f"Total entities found: {total_entities}")
        logger.info(f"Processing time: {processing_time:.2f} seconds")
        logger.info(f"Entities per second: {total_entities/processing_time:.2f}")
        logger.info(f"Output directory: {args.output}")
        logger.info("=" * 50)
        
        return True
    else:
        logger.warning("No results generated - no files were processed successfully")
        return False


def generate_per_entity_folders(results, output_dir):
    """Generate per-entity folders"""
    entities_by_type = {}
    
    for result in results:
        for match in result['matches']:
            entity_type = match.get('type', 'unknown')
            if entity_type not in entities_by_type:
                entities_by_type[entity_type] = []
            entities_by_type[entity_type].append(match)
    
    for entity_type, matches in entities_by_type.items():
        entity_dir = Path(output_dir) / "entities" / entity_type
        entity_dir.mkdir(parents=True, exist_ok=True)
        
        # Generate entity report
        entity_report = generate_entity_report(matches)
        report_path = entity_dir / "report.txt"
        with open(report_path, 'w', encoding='utf-8') as f:
            f.write(entity_report)
        
        logger.info(f"Entity folder generated: {entity_dir}")


def generate_entity_report(matches):
    """Generate report for a specific entity"""
    report = f"Entity Report\n"
    report += f"Total mentions: {len(matches)}\n\n"
    
    for i, match in enumerate(matches, 1):
        report += f"{i}. {match.get('text', 'Unknown')}\n"
        report += f"   Context: {match.get('context', 'N/A')}\n"
        report += f"   Confidence: {match.get('confidence', 0):.2f}\n\n"
    
    return report


def generate_quality_report(quality_stats, results):
    """Generate quality report"""
    report = f"Quality Report\n"
    report += f"Generated: {time.strftime('%Y-%m-%d %H:%M:%S')}\n\n"
    
    report += f"Cache Performance:\n"
    report += f"  Hits: {quality_stats.get('cache_hits', 0)}\n"
    report += f"  Misses: {quality_stats.get('cache_misses', 0)}\n"
    report += f"  Hit Rate: {quality_stats.get('cache_hit_rate', 0):.2%}\n\n"
    
    report += f"Filter Performance:\n"
    report += f"  Efficiency: {quality_stats.get('filter_efficiency', 0):.2%}\n"
    report += f"  Signal-to-Noise: {quality_stats.get('signal_to_noise_ratio', 0):.2f}:1\n\n"
    
    report += f"Processing Statistics:\n"
    report += f"  Files processed: {len(results)}\n"
    total_entities = sum(len(r['matches']) for r in results)
    report += f"  Total entities: {total_entities}\n"
    report += f"  Average entities per file: {total_entities/len(results) if results else 0:.2f}\n\n"
    
    report += f"Recommendations:\n"
    if quality_stats.get('cache_hit_rate', 0) < 0.5:
        report += "  - Consider increasing cache size or adjusting invalidation strategy\n"
    
    if quality_stats.get('filter_efficiency', 0) < 0.4:
        report += "  - Consider improving noise detection filters\n"
    
    if total_entities > 1000:
        report += "  - Consider implementing streaming for large outputs\n"
    
    return report


def main():
    """Main entry point"""
    args = parse_arguments()
    
    # Set log level based on verbose flag
    if args.verbose:
        logger.setLevel(logging.DEBUG)
    
    # Run analysis
    success = run_analysis(args)
    
    if success:
        logger.info("Analysis completed successfully")
        return 0
    else:
        logger.error("Analysis failed")
        return 1


if __name__ == "__main__":
    sys.exit(main())