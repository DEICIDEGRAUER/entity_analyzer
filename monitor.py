#!/usr/bin/env python3
"""
Entity Analyzer Production Monitor

This script monitors Entity Analyzer performance in production.
It tracks key metrics and alerts on issues.
"""

import os
import sys
import time
import json
import logging
import subprocess
from datetime import datetime, timedelta
from pathlib import Path
from typing import Dict, List, Any

# Configure logging
logging.basicConfig(
    level=logging.INFO,
    format='%(asctime)s - %(levelname)s - %(message)s',
    handlers=[
        logging.FileHandler('entity_analyzer_monitor.log'),
        logging.StreamHandler()
    ]
)
logger = logging.getLogger(__name__)


class EntityAnalyzerMonitor:
    """Monitor Entity Analyzer performance and health"""
    
    def __init__(self, config_path: str = "config.yaml"):
        self.config_path = config_path
        self.metrics_file = "entity_analyzer_metrics.json"
        self.alert_thresholds = {
            'cache_hit_rate': 0.6,  # 60%
            'filter_efficiency': 0.4,  # 40%
            'processing_time': 60.0,  # 60 seconds
            'memory_usage': 2.0,  # 2 GB
            'error_rate': 0.001,  # 0.1%
        }
        self.metrics_history = []
        self.load_metrics()
    
    def load_metrics(self):
        """Load existing metrics from file"""
        if os.path.exists(self.metrics_file):
            try:
                with open(self.metrics_file, 'r') as f:
                    self.metrics_history = json.load(f)
                logger.info(f"Loaded {len(self.metrics_history)} historical metrics")
            except Exception as e:
                logger.warning(f"Failed to load metrics: {e}")
                self.metrics_history = []
    
    def save_metrics(self):
        """Save metrics to file"""
        try:
            with open(self.metrics_file, 'w') as f:
                json.dump(self.metrics_history, f, indent=2)
        except Exception as e:
            logger.error(f"Failed to save metrics: {e}")
    
    def collect_metrics(self, run_id: str, preset: str, results: Dict[str, Any]) -> Dict[str, Any]:
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
        
        self.metrics_history.append(metrics)
        self.save_metrics()
        
        # Check for alerts
        self.check_alerts(metrics)
        
        return metrics
    
    def check_alerts(self, metrics: Dict[str, Any]):
        """Check if any metrics exceed alert thresholds"""
        alerts = []
        
        # Check cache hit rate
        if metrics['cache_hit_rate'] < self.alert_thresholds['cache_hit_rate']:
            alerts.append(
                f"Low cache hit rate: {metrics['cache_hit_rate']:.2%} "
                f"(threshold: {self.alert_thresholds['cache_hit_rate']:.2%})"
            )
        
        # Check filter efficiency
        if metrics['filter_efficiency'] < self.alert_thresholds['filter_efficiency']:
            alerts.append(
                f"Low filter efficiency: {metrics['filter_efficiency']:.2%} "
                f"(threshold: {self.alert_thresholds['filter_efficiency']:.2%})"
            )
        
        # Check processing time
        if metrics['processing_time'] > self.alert_thresholds['processing_time']:
            alerts.append(
                f"High processing time: {metrics['processing_time']:.2f}s "
                f"(threshold: {self.alert_thresholds['processing_time']:.2f}s)"
            )
        
        # Check memory usage
        if metrics['memory_usage'] > self.alert_thresholds['memory_usage']:
            alerts.append(
                f"High memory usage: {metrics['memory_usage']:.2f}GB "
                f"(threshold: {self.alert_thresholds['memory_usage']:.2f}GB)"
            )
        
        # Check error rate
        if metrics['error_rate'] > self.alert_thresholds['error_rate']:
            alerts.append(
                f"High error rate: {metrics['error_rate']:.4f} "
                f"(threshold: {self.alert_thresholds['error_rate']:.4f})"
            )
        
        if alerts:
            self.send_alerts(alerts, metrics)
    
    def send_alerts(self, alerts: List[str], metrics: Dict[str, Any]):
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
        
        # In production, this would send alerts to:
        # - Email
        # - Slack
        # - PagerDuty
        # - Monitoring system
        
        # For now, just log the alert
        with open('entity_analyzer_alerts.log', 'a') as f:
            f.write(f"\n{'='*50}\n")
            f.write(alert_message)
            f.write(f"\n{'='*50}\n")
    
    def generate_report(self, hours: int = 24) -> Dict[str, Any]:
        """Generate a performance report for the last N hours"""
        cutoff_time = datetime.now() - timedelta(hours=hours)
        
        recent_metrics = [
            m for m in self.metrics_history
            if datetime.fromisoformat(m['timestamp']) > cutoff_time
        ]
        
        if not recent_metrics:
            return {
                'period': f'Last {hours} hours',
                'total_runs': 0,
                'average_metrics': {},
                'alerts_count': 0,
            }
        
        # Calculate average metrics
        avg_metrics = {}
        for key in recent_metrics[0].keys():
            if key not in ['timestamp', 'run_id', 'preset']:
                values = [m[key] for m in recent_metrics if isinstance(m[key], (int, float))]
                if values:
                    avg_metrics[key] = sum(values) / len(values)
        
        # Count alerts
        alerts_count = len([m for m in recent_metrics if 'alerts' in m])
        
        return {
            'period': f'Last {hours} hours',
            'total_runs': len(recent_metrics),
            'average_metrics': avg_metrics,
            'alerts_count': alerts_count,
            'preset_distribution': self._get_preset_distribution(recent_metrics),
            'performance_trends': self._get_performance_trends(recent_metrics),
        }
    
    def _get_preset_distribution(self, metrics: List[Dict[str, Any]]) -> Dict[str, int]:
        """Get distribution of runs by preset"""
        distribution = {}
        for m in metrics:
            preset = m['preset']
            distribution[preset] = distribution.get(preset, 0) + 1
        return distribution
    
    def _get_performance_trends(self, metrics: List[Dict[str, Any]]) -> Dict[str, Any]:
        """Get performance trends over time"""
        if len(metrics) < 2:
            return {'trend': 'insufficient_data', 'change_rate': 0}
        
        # Calculate trend for key metrics
        times = [datetime.fromisoformat(m['timestamp']) for m in metrics]
        processing_times = [m['processing_time'] for m in metrics]
        
        # Calculate linear regression for processing time trend
        if len(times) >= 2:
            time_diffs = [(t - times[0]).total_seconds() for t in times]
            processing_time_diffs = [pt - processing_times[0] for pt in processing_times]
            
            if time_diffs and all(t > 0 for t in time_diffs):
                correlation = sum(t * pt for t, pt in zip(time_diffs, processing_time_diffs)) / (
                    sum(t * t for t in time_diffs) * len(time_diffs)
                )
                
                if correlation > 0.1:
                    trend = 'degrading'
                elif correlation < -0.1:
                    trend = 'improving'
                else:
                    trend = 'stable'
            else:
                trend = 'stable'
        else:
            trend = 'stable'
        
        return {
            'trend': trend,
            'change_rate': correlation if 'correlation' in locals() else 0,
            'data_points': len(metrics),
        }


def run_monitoring():
    """Run the monitoring script"""
    print("Entity Analyzer Production Monitor")
    print("=" * 50)
    
    # Initialize monitor
    monitor = EntityAnalyzerMonitor()
    
    # Generate report for last 24 hours
    print("\nGenerating performance report for last 24 hours...")
    report = monitor.generate_report(hours=24)
    
    print(f"\nReport Summary:")
    print(f"  Period: {report['period']}")
    print(f"  Total runs: {report['total_runs']}")
    print(f"  Alerts count: {report['alerts_count']}")
    
    if report['average_metrics']:
        print(f"\nAverage Metrics:")
        for key, value in report['average_metrics'].items():
            if isinstance(value, float):
                print(f"  {key}: {value:.4f}")
            else:
                print(f"  {key}: {value}")
    
    if report['preset_distribution']:
        print(f"\nPreset Distribution:")
        for preset, count in report['preset_distribution'].items():
            print(f"  {preset}: {count} runs")
    
    # Check for recent alerts
    recent_alerts = []
    for metric in monitor.metrics_history[-10:]:  # Last 10 runs
        if 'alerts' in metric:
            recent_alerts.extend(metric['alerts'])
    
    if recent_alerts:
        print(f"\nRecent Alerts ({len(recent_alerts)}):")
        for alert in recent_alerts[-5:]:  # Last 5 alerts
            print(f"  - {alert}")
    
    print(f"\n{'='*50}")
    print("Monitoring complete. Metrics saved to entity_analyzer_metrics.json")
    print("Alerts logged to entity_analyzer_alerts.log")


if __name__ == "__main__":
    run_monitoring()