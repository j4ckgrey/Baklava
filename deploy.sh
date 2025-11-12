#!/bin/bash

set -e

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m' # No Color

echo -e "${BLUE}========================================${NC}"
echo -e "${BLUE}   Baklava Plugin Deployment Script${NC}"
echo -e "${BLUE}========================================${NC}"
echo ""

# Get current version from AssemblyInfo.cs
CURRENT_VERSION=$(grep -oP 'AssemblyVersion\("\K[^"]+' Properties/AssemblyInfo.cs | head -1)
echo -e "${GREEN}Current version:${NC} $CURRENT_VERSION"
echo ""

# Ask for new version
read -p "Enter new version (e.g., 0.2.3.0): " NEW_VERSION

if [ -z "$NEW_VERSION" ]; then
    echo -e "${RED}Error: Version cannot be empty${NC}"
    exit 1
fi

# Validate version format (x.x.x.x)
if ! [[ $NEW_VERSION =~ ^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
    echo -e "${RED}Error: Invalid version format. Use x.x.x.x (e.g., 0.2.3.0)${NC}"
    exit 1
fi

echo ""
echo -e "${YELLOW}New version will be:${NC} $NEW_VERSION"
echo ""

# Ask for release title
read -p "Enter release title (e.g., 'Hotfix' or 'Feature Release'): " RELEASE_TITLE

if [ -z "$RELEASE_TITLE" ]; then
    RELEASE_TITLE="Release"
fi

# Ask for changelog
echo ""
echo -e "${YELLOW}Enter changelog (leave empty for release title):${NC}"
read -p "> " CHANGELOG

if [ -z "$CHANGELOG" ]; then
    CHANGELOG="$RELEASE_TITLE"
fi

echo ""
echo -e "${BLUE}========================================${NC}"
echo -e "${GREEN}Summary:${NC}"
echo -e "  Version: ${YELLOW}$NEW_VERSION${NC}"
echo -e "  Title: ${YELLOW}$RELEASE_TITLE${NC}"
echo -e "  Changelog: ${YELLOW}$CHANGELOG${NC}"
echo -e "${BLUE}========================================${NC}"
echo ""

read -p "Proceed with deployment? (y/n): " CONFIRM

if [ "$CONFIRM" != "y" ] && [ "$CONFIRM" != "Y" ]; then
    echo -e "${RED}Deployment cancelled${NC}"
    exit 0
fi

echo ""
echo -e "${BLUE}Step 1: Updating version in AssemblyInfo.cs...${NC}"
sed -i "s/AssemblyVersion(\"[^\"]*\")/AssemblyVersion(\"$NEW_VERSION\")/" Properties/AssemblyInfo.cs
sed -i "s/AssemblyFileVersion(\"[^\"]*\")/AssemblyFileVersion(\"$NEW_VERSION\")/" Properties/AssemblyInfo.cs
sed -i "s/AssemblyInformationalVersion(\"[^\"]*\")/AssemblyInformationalVersion(\"$NEW_VERSION\")/" Properties/AssemblyInfo.cs
echo -e "${GREEN}✓ AssemblyInfo.cs updated${NC}"

echo ""
echo -e "${BLUE}Step 2: Building and publishing plugin...${NC}"
dotnet publish Baklava.csproj -c Release -o publish
echo -e "${GREEN}✓ Build completed${NC}"

echo ""
echo -e "${BLUE}Step 3: Creating release package...${NC}"
ZIP_NAME="baklava_${NEW_VERSION}.zip"
rm -f "$ZIP_NAME"
cd publish
zip -r ../"$ZIP_NAME" . > /dev/null 2>&1
cd ..
echo -e "${GREEN}✓ Package created: $ZIP_NAME${NC}"

echo ""
echo -e "${BLUE}Step 4: Calculating MD5 checksum...${NC}"
MD5_CHECKSUM=$(md5sum "$ZIP_NAME" | awk '{print $1}')
echo -e "${GREEN}✓ MD5: $MD5_CHECKSUM${NC}"

echo ""
echo -e "${BLUE}Step 5: Updating manifest.json...${NC}"

# Get current timestamp in ISO format
TIMESTAMP=$(date -u +"%Y-%m-%dT%H:%M:%SZ")

# Create the new version entry
NEW_ENTRY="      {
        \"version\": \"$NEW_VERSION\",
        \"changelog\": \"$CHANGELOG\",
        \"targetAbi\": \"10.11.0.0\",
        \"sourceUrl\": \"https://github.com/j4ckgrey/Baklava/releases/download/v$NEW_VERSION/baklava_$NEW_VERSION.zip\",
        \"checksum\": \"$MD5_CHECKSUM\",
        \"checksumType\": \"md5\",
        \"timestamp\": \"$TIMESTAMP\"
      },"

# Use Python to properly insert the new entry into the JSON
python3 << EOF
import json

with open('manifest.json', 'r') as f:
    manifest = json.load(f)

# Create new version entry
new_version = {
    "version": "$NEW_VERSION",
    "changelog": "$CHANGELOG",
    "targetAbi": "10.11.0.0",
    "sourceUrl": f"https://github.com/j4ckgrey/Baklava/releases/download/v$NEW_VERSION/baklava_$NEW_VERSION.zip",
    "checksum": "$MD5_CHECKSUM",
    "checksumType": "md5",
    "timestamp": "$TIMESTAMP"
}

# Insert at the beginning of versions array
manifest[0]["versions"].insert(0, new_version)

# Write back
with open('manifest.json', 'w') as f:
    json.dump(manifest, f, indent=2)
EOF

echo -e "${GREEN}✓ manifest.json updated${NC}"

echo ""
echo -e "${BLUE}Step 6: Committing changes...${NC}"
git add -A
git commit -m "v$NEW_VERSION - $RELEASE_TITLE"
echo -e "${GREEN}✓ Changes committed${NC}"

echo ""
echo -e "${BLUE}Step 7: Pushing to GitHub...${NC}"
git push origin main
echo -e "${GREEN}✓ Pushed to GitHub${NC}"

echo ""
echo -e "${BLUE}Step 8: Creating GitHub release...${NC}"

# Prepare release notes
if [ -z "$CHANGELOG" ] || [ "$CHANGELOG" == "$RELEASE_TITLE" ]; then
    NOTES=""
else
    NOTES="$CHANGELOG"
fi

gh release create "v$NEW_VERSION" "$ZIP_NAME" --title "$RELEASE_TITLE" --notes "$NOTES"
RELEASE_URL="https://github.com/j4ckgrey/Baklava/releases/tag/v$NEW_VERSION"
echo -e "${GREEN}✓ Release created${NC}"

echo ""
echo -e "${BLUE}========================================${NC}"
echo -e "${GREEN}✓ Deployment Complete!${NC}"
echo -e "${BLUE}========================================${NC}"
echo ""
echo -e "Version: ${YELLOW}$NEW_VERSION${NC}"
echo -e "Release URL: ${BLUE}$RELEASE_URL${NC}"
echo ""
