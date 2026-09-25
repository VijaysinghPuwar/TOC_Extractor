.DEFAULT_GOAL := help
SHELL := /bin/bash

# Override to build against a specific interpreter:
#   make setup PYTHON=/Library/Frameworks/Python.framework/Versions/3.14/bin/python3
PYTHON ?= python3
VENV := .venv
BIN := $(VENV)/bin

.PHONY: help setup deps check-tk lint fmt typecheck test test-fast test-browser run gui clean \
        cs-build cs-test cs-lint cs-fmt cs-clean

help:
	@echo "setup        deps, plus Chromium and a Tk check (what you want locally)"
	@echo "deps         create $(VENV) and install the package and dev extras only"
	@echo "lint         ruff check + format check"
	@echo "fmt          apply ruff formatting"
	@echo "typecheck    mypy strict over src/"
	@echo "test         full suite, including browser-marked tests"
	@echo "test-fast    skip browser-marked tests (the CI matrix target)"
	@echo "test-browser only browser-marked tests"
	@echo "run          run the CLI: make run ARGS='--toc ... --link ...'"
	@echo "gui          open the graphical front end"
	@echo "clean        remove the venv and tooling caches"
	@echo ""
	@echo "cs-build     build the C# solution"
	@echo "cs-test      run the C# suite (no browser)"
	@echo "cs-test-browser  only the C# browser tests"
	@echo "cs-browser-install  download Chromium for the C# browser tests"
	@echo "cs-lint      build with warnings as errors + format check"
	@echo "cs-fmt       apply C# formatting"
	@echo "cs-run       run the C# CLI: make cs-run ARGS='--toc ... --link ...'"
	@echo "cs-clean     remove C# build output"

$(BIN)/python:
	$(PYTHON) -m venv $(VENV)

# Split out so CI's matrix jobs, which run only test-fast, do not each
# download a browser they never launch.
deps: $(BIN)/python
	$(BIN)/python -m pip install --quiet --upgrade pip
	$(BIN)/python -m pip install --quiet -e ".[dev]"

setup: deps
	$(BIN)/python -m playwright install chromium
	@$(MAKE) --no-print-directory check-tk

# The GUI needs Tk; the CLI does not. This warns rather than fails so a
# headless or CI setup still succeeds.
check-tk:
	@$(BIN)/python -c "import tkinter" 2>/dev/null && \
		echo "tk: ok ($$($(BIN)/python -c 'import tkinter; print(tkinter.TkVersion)'))" || { \
		echo ""; \
		echo "tk: MISSING - the CLI works, the GUI will not."; \
		echo "  Homebrew's python3 ships without _tkinter, and python-tk@3.11 /"; \
		echo "  python-tk@3.12 only help if you also have the matching brew python."; \
		echo "  /usr/bin/python3 has the deprecated Tk 8.5."; \
		echo "  Rebuild against a python.org framework build:"; \
		echo "    make clean"; \
		echo "    make setup PYTHON=/Library/Frameworks/Python.framework/Versions/3.14/bin/python3"; \
		echo ""; }

lint:
	$(BIN)/ruff check .
	$(BIN)/ruff format --check .

fmt:
	$(BIN)/ruff format .
	$(BIN)/ruff check --fix .

typecheck:
	$(BIN)/mypy

test:
	$(BIN)/python -m pytest

# The CI matrix target. Every job but one runs this, so a matrix job never
# waits on a Chromium download.
test-fast:
	$(BIN)/python -m pytest -m "not browser"

test-browser:
	$(BIN)/python -m pytest -m browser

# make run ARGS='--toc https://... --link a.ch --title h1 --content article'
run:
	$(BIN)/python -m toc_extractor $(ARGS)

gui:
	$(BIN)/python -m toc_extractor --gui

clean:
	rm -rf $(VENV) .mypy_cache .ruff_cache .pytest_cache
	find . -name __pycache__ -type d -prune -exec rm -rf {} +

# -- C# ----------------------------------------------------------------------
# The second implementation. Same invariants, same golden corpus; see
# dotnet/README.md for what is shared and what is deliberately different.

SLN := dotnet/TocExtractor.slnx
export DOTNET_CLI_TELEMETRY_OPTOUT := 1
export DOTNET_NOLOGO := 1

cs-build:
	dotnet build $(SLN)

# The browser tests are a separate project so this stays fast and needs no
# Chromium, mirroring how the Python suite deselects its browser marker.
CS_CORE := dotnet/tests/TocExtractor.Core.Tests
CS_BROWSER := dotnet/tests/TocExtractor.Browser.Tests

CS_CLI := dotnet/tests/TocExtractor.Cli.Tests

cs-test:
	dotnet test $(CS_CORE)
	dotnet test $(CS_CLI)

cs-test-browser:
	dotnet test $(CS_BROWSER)

# No pwsh on every machine, so the driver's own node runs its CLI.
cs-browser-install: cs-build
	cd $(CS_BROWSER)/bin/Debug/net10.0 && \
		./.playwright/node/linux-x64/node ./.playwright/package/cli.js install chromium

# Warnings are already errors via Directory.Build.props; the format check is
# the part a plain build does not cover.
cs-lint:
	dotnet build $(SLN)
	dotnet format $(SLN) --verify-no-changes

cs-fmt:
	dotnet format $(SLN)

cs-run:
	dotnet run --project dotnet/src/TocExtractor.Cli -- $(ARGS)

cs-clean:
	rm -rf dotnet/src/*/bin dotnet/src/*/obj dotnet/tests/*/bin dotnet/tests/*/obj
