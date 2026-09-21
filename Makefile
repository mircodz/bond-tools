.PHONY: help build build-release test clean pack install uninstall coverage format

# Variables
VERSION := $(shell cat version)
OUT_DIR := ./out
NUPKG_DIR := $(OUT_DIR)/package/release
TOOL_NAME := bond
PKG_ID := bond-tools

help: ## Show this help message
	@echo 'Usage: make [target]'
	@echo ''
	@echo 'Available targets:'
	@grep -E '^[a-zA-Z_-]+:.*?## .*$$' $(MAKEFILE_LIST) | awk 'BEGIN {FS = ":.*?## "}; {printf "  %-20s %s\n", $$1, $$2}'

build: ## Build the project in Debug mode
	dotnet build Bond.sln -c Debug

build-release: ## Build the project in Release mode
	dotnet build Bond.sln -c Release

test: ## Run all tests
	dotnet test --project Bond.Parser.Tests/Bond.Parser.Tests.csproj

coverage: ## Run tests with coverage report
	bash scripts/coverage.sh

clean: ## Clean build artifacts
	dotnet clean Bond.sln || true
	rm -rf $(OUT_DIR)
	rm -rf */bin */obj

pack: build-release ## Pack the tool, parser, model support, and build integration
	dotnet pack Bond.sln -c Release --no-build --no-restore /p:Version=$(VERSION)
	@echo ""
	@echo "Packages created in $(NUPKG_DIR)"

install: pack ## Install the tool globally
	dotnet tool uninstall -g $(PKG_ID) 2>/dev/null || true
	dotnet tool install --global --add-source $(NUPKG_DIR) $(PKG_ID) --version $(VERSION)
	@echo ""
	@echo "Tool installed! You can now use: $(TOOL_NAME)"
	@echo "Note: You may need to add ~/.dotnet/tools to your PATH"
	@echo ""
	@echo "For zsh, run:"
	@echo "  echo 'export PATH=\"\$$PATH:\$$HOME/.dotnet/tools\"' >> ~/.zprofile"
	@echo "  source ~/.zprofile"

uninstall: ## Uninstall the tool
	dotnet tool uninstall -g $(PKG_ID)

reinstall: uninstall install ## Reinstall the tool (clean install)

# Setup
setup: ## Initial setup (restore packages)
	./scripts/setup-hooks.sh
	dotnet restore

all: clean build test ## Clean, build, and test
