from setuptools import setup, find_packages
import os

# Read version from config or use default
VERSION = "18.1.0"

# Read requirements
with open("requirements.txt", "r", encoding="utf-8") as f:
    requirements = [line.strip() for line in f if line.strip() and not line.startswith("#")]

# Read long description from README if exists
long_description = ""
if os.path.exists("README.md"):
    with open("README.md", "r", encoding="utf-8") as f:
        long_description = f.read()
else:
    long_description = "Entity Analyzer - Text analysis tool for entity extraction and relationship mapping"

setup(
    name="entity-analyzer",
    version=VERSION,
    author="Entity Analyzer Team",
    author_email="qa@entity-analyzer.local",
    description="Text analysis tool for entity extraction and relationship mapping",
    long_description=long_description,
    long_description_content_type="text/markdown",
    url="https://github.com/entity-analyzer/entity-analyzer",
    packages=find_packages(include=["entity_analyzer", "entity_analyzer.*"]),
    py_modules=[
        "main", "config", "entities", "processor", "output", "quality",
        "cache", "noise_detector", "filters", "context", "translator",
        "name_generator", "normalizer", "semantic", "limits", "density",
        "discovery", "resolver", "import_to_neo4j"
    ],
    classifiers=[
        "Development Status :: 4 - Beta",
        "Intended Audience :: Developers",
        "Intended Audience :: Science/Research",
        "License :: OSI Approved :: MIT License",
        "Operating System :: OS Independent",
        "Programming Language :: Python :: 3",
        "Programming Language :: Python :: 3.8",
        "Programming Language :: Python :: 3.9",
        "Programming Language :: Python :: 3.10",
        "Programming Language :: Python :: 3.11",
        "Programming Language :: Python :: 3.12",
        "Topic :: Text Processing :: Linguistic",
        "Topic :: Scientific/Engineering :: Information Analysis",
    ],
    python_requires=">=3.8",
    install_requires=requirements,
    extras_require={
        "sbert": ["sentence-transformers>=2.0"],
        "dev": ["pytest>=7.0", "pytest-cov>=4.0", "black>=23.0", "flake8>=6.0", "mypy>=1.0"],
    },
    entry_points={
        "console_scripts": [
            "entity-analyzer=main:main",
        ],
    },
    include_package_data=True,
    package_data={
        "": ["config.yaml", "*.txt", "*.md"],
    },
    zip_safe=False,
)