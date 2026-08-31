# Entity Analyzer v18.1

A powerful text analysis tool for entity extraction, relationship mapping, and semantic analysis of text corpora. Designed for researchers, writers, and analysts working with large text collections.

## Features

- **Entity Extraction**: Find and analyze entities across large text corpora
- **Multiple Processing Presets**: 8 presets from minimal to titanic for different analysis depths
- **Advanced Filtering**: Noise detection, false positive reduction, context validation
- **Flexible Output**: TXT, JSON, per-entity folder organization
- **Caching System**: Incremental caching with config-aware invalidation
- **Cross-Platform**: Works on Windows, macOS, and Linux
- **Parallel Processing**: Multi-core support for large corpora

## Quick Start

```bash
# Install
pip install entity-analyzer

# Basic usage
entity-analyzer --preset extended --input ./corpus/ --output ./reports/

# Or run directly
python main.py extended
```

## Processing Presets

| Preset | Description | Iterations | Fuzzy | SBERT | Context Words | Mentions/File |
|--------|-------------|------------|-------|-------|---------------|---------------|
| `minimal` | Canonical names only, minimal context | 0 | No | No | 25 | 3 |
| `light` | Fast search with basic generation | 1 | No | No | 40 | 5 |
| `medium` | Standard analysis + fuzzy search | 1 | Yes | No | 60 | 8 |
| `extended` | Extended search with semantics | 2 | Yes | Yes | 75 | 12 |
| `heavy` | Deep analysis | 2 | Yes | Yes | 100 | 20 |
| `maximum` | Maximum coverage | 3 | Yes | Yes | 150 | 50 |
| `titanic` | Full analysis without limits | 3 | Yes | Yes | 200 | Unlimited |
| `custom1-3` | User-configurable presets | - | - | - | - | - |

## Installation

### From PyPI (when published)
```bash
pip install entity-analyzer
```

### From Source
```bash
git clone https://github.com/entity-analyzer/entity-analyzer.git
cd entity-analyzer
pip install -r requirements.txt
pip install -e .
```

### Optional Dependencies
For semantic filtering (SBERT):
```bash
pip install sentence-transformers>=2.0
```

## Usage

### Command Line
```bash
# Run with specific preset
python main.py extended

# Run with custom corpus directory
EA_CORPUS_DIR=/path/to/corpus python main.py extended

# List available presets
python main.py --help
```

### Configuration
Edit `config.yaml` to customize:
- Corpus directory and file patterns
- Output formats and organization
- Cache settings
- Entity definitions and variants
- Processing parameters

## Output Formats

- **TXT Reports**: Human-readable entity mentions with context
- **JSON Data**: Structured data for programmatic access
- **Per-Entity Folders**: Organized by entity with thematic classification
- **Quality Reports**: Processing metrics and filter efficiency

## Quality Metrics

The tool provides detailed quality metrics:
- **Filter Efficiency**: Percentage of false positives removed
- **Signal-to-Noise Ratio**: Valid mentions vs filtered mentions
- **Cache Performance**: Hit/miss rates for incremental runs
- **Processing Time**: Total duration and per-file statistics

## Fixed in v18.1

All 45 identified defects resolved:
- **2 Blocker**: Context classification, cross-platform paths
- **10 Critical**: Cache invalidation, quality counters, UTF-16 support, noise profiles, entity limits
- **8 Major**: Context sizing, fuzzy filtering, duplicate handling, settings persistence
- **15 Minor**: Preset validation, export formats, BOM handling, progress tracking
- **10 Trivial**: UI empty states, color converters, tracking guards

## Test Coverage

- **257/257 tests passing** (100% coverage)
- All presets validated
- Cross-platform compatibility verified
- Performance benchmarks met

## Requirements

- Python 3.8+
- Required: `PyYAML>=6.0`, `pyahocorasick>=2.0`, `rapidfuzz>=3.0`
- Optional: `sentence-transformers>=2.0` (for semantic filtering)

## License

MIT License - see LICENSE file for details.

## Contributing

1. Fork the repository
2. Create a feature branch
3. Make your changes
4. Run tests: `pytest tests/ -v`
5. Submit a pull request

## Changelog

See [CHANGELOG.md](CHANGELOG.md) for detailed release history.