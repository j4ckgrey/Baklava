#!/bin/bash
# Build script for Baklava Jellyfin Plugin

set -e

VERSION="1.0.3"
PLUGIN_NAME="Baklava"
OUTPUT_DIR="release"

echo "Building ${PLUGIN_NAME} v${VERSION}..."

# Clean previous builds
dotnet clean
rm -rf bin obj ${OUTPUT_DIR}

# Build the plugin
dotnet build -c Release

# Create release directory structure
mkdir -p ${OUTPUT_DIR}/${PLUGIN_NAME}_${VERSION}

# Copy DLL
cp bin/Release/net9.0/${PLUGIN_NAME}.dll ${OUTPUT_DIR}/${PLUGIN_NAME}_${VERSION}/

# Copy Files directory
cp -r Files ${OUTPUT_DIR}/${PLUGIN_NAME}_${VERSION}/

# Create meta.json
cat > ${OUTPUT_DIR}/${PLUGIN_NAME}_${VERSION}/meta.json << EOF
{
  "guid": "109470b0-d97c-4540-89b7-856d4e5831c7",
  "name": "${PLUGIN_NAME}",
  "description": "Media request management system for Jellyfin with search enhancements",
  "overview": "Allows users to request movies and TV shows, with admin approval workflow, status tracking, and enhanced search features",
  "owner": "j4ckgrey",
  "category": "General",
  "version": "${VERSION}",
  "changelog": "Initial release with full request management and search integration",
  "targetAbi": "10.11.0.0",
  "timestamp": "$(date -u +%Y-%m-%dT%H:%M:%SZ)"
}
EOF

# Create zip archive
cd ${OUTPUT_DIR}
zip -r ${PLUGIN_NAME}_${VERSION}.zip ${PLUGIN_NAME}_${VERSION}
cd ..

echo "✓ Build complete!"
echo "📦 Package: ${OUTPUT_DIR}/${PLUGIN_NAME}_${VERSION}.zip"
echo ""
echo "To install manually:"
echo "  1. Extract the zip file"
echo "  2. Copy ${PLUGIN_NAME}_${VERSION} folder to your Jellyfin plugins directory"
echo "  3. Restart Jellyfin"
echo ""
echo "To publish to GitHub:"
echo "  1. Create a new release: gh release create v${VERSION}"
echo "  2. Upload: gh release upload v${VERSION} ${OUTPUT_DIR}/${PLUGIN_NAME}_${VERSION}.zip"
