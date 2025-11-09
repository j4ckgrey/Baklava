#!/bin/bash
# Baklava v0.1.0 - GitHub Deployment Script

set -e  # Exit on error

echo "🚀 Baklava v0.1.0 - GitHub Deployment Script"
echo "=============================================="
echo ""

# Configuration
REPO_NAME="Baklava"
REPO_DESCRIPTION="Jellyfin plugin for media request management and enhanced search"
GITHUB_USER="j4ckgrey"

# Check if gh CLI is installed
if ! command -v gh &> /dev/null; then
    echo "❌ GitHub CLI (gh) is not installed."
    echo "Install it with: sudo apt install gh"
    echo "Or visit: https://cli.github.com/"
    exit 1
fi

# Check if authenticated
if ! gh auth status &> /dev/null; then
    echo "❌ Not authenticated with GitHub CLI"
    echo "Run: gh auth login"
    exit 1
fi

echo "✅ GitHub CLI found and authenticated"
echo ""

# Step 1: Create GitHub repository
echo "📦 Step 1: Creating private GitHub repository '${REPO_NAME}'..."
if gh repo create "${GITHUB_USER}/${REPO_NAME}" \
    --private \
    --description "${REPO_DESCRIPTION}" \
    --confirm; then
    echo "✅ Repository created successfully!"
else
    echo "⚠️  Repository might already exist, continuing..."
fi
echo ""

# Step 2: Initialize git if needed
if [ ! -d .git ]; then
    echo "📝 Step 2: Initializing git repository..."
    git init
    git branch -M main
    echo "✅ Git initialized"
else
    echo "✅ Git repository already initialized"
fi
echo ""

# Step 3: Add remote
echo "🔗 Step 3: Adding remote origin..."
if git remote | grep -q origin; then
    echo "⚠️  Remote 'origin' already exists, updating..."
    git remote set-url origin "git@github.com:${GITHUB_USER}/${REPO_NAME}.git"
else
    git remote add origin "git@github.com:${GITHUB_USER}/${REPO_NAME}.git"
fi
echo "✅ Remote configured"
echo ""

# Step 4: Stage all files
echo "📋 Step 4: Staging files..."
git add .
echo "✅ Files staged"
echo ""

# Step 5: Commit
echo "💾 Step 5: Creating initial commit..."
if git diff --cached --quiet; then
    echo "⚠️  No changes to commit"
else
    git commit -m "Initial commit: Baklava v0.1.0

Features:
- Media request management system with admin approval
- Search toggle between local (Jellyfin) and global (Gelato) search
- TV client automatic local search enforcement
- Server-side search filtering with prefix handling
- Responsive UI for all screen sizes
- Comprehensive documentation and configuration

Technical:
- .NET 9.0 / ASP.NET Core
- MVC ActionFilters with order-based execution
- TMDB API integration
- Material Icons UI
- Jellyfin 10.11.0.0+ compatible"
    echo "✅ Initial commit created"
fi
echo ""

# Step 6: Push to GitHub
echo "🚀 Step 6: Pushing to GitHub..."
git push -u origin main --force
echo "✅ Code pushed to GitHub"
echo ""

# Step 7: Create release package
echo "📦 Step 7: Creating release package..."
cd bin/Release/net9.0/publish/
zip -r baklava_0.1.0.zip ./* > /dev/null 2>&1
CHECKSUM=$(md5sum baklava_0.1.0.zip | awk '{print $1}')
echo "✅ Release package created"
echo "   File: baklava_0.1.0.zip"
echo "   MD5: ${CHECKSUM}"
echo ""

# Step 8: Create GitHub release
echo "🎉 Step 8: Creating GitHub release v0.1.0..."
cd - > /dev/null

CHANGELOG="## Baklava v0.1.0 - Initial Release

### 🎬 Media Request System
- User request submission for movies and TV series
- Admin approval/denial workflow
- Request tracking and status management
- Responsive card-based UI

### 🔍 Enhanced Search
- Visual search toggle (globe icon)
- Local search (Jellyfin library only)
- Global search (external sources via Gelato)
- Automatic TV client local search enforcement
- Configurable search behavior

### 🎯 Server-Side Features
- SearchActionFilter with order priority (0)
- Automatic prefix handling (local:)
- TV client detection (Android TV, Fire TV, etc.)
- Seamless Gelato integration
- TMDB API integration

### 📱 Responsive Design
- Desktop: 140px × 210px cards
- Tablet: 110px × 165px cards
- Mobile: 90px × 135px cards
- Full-height modals with optimized spacing
- Touch-friendly interface

### 🛠️ Technical
- .NET 9.0
- Jellyfin 10.11.0.0+
- ASP.NET Core MVC ActionFilters
- Material Icons
- MIT License

### 📋 Checksum
- MD5: ${CHECKSUM}"

gh release create v0.1.0 \
    bin/Release/net9.0/publish/baklava_0.1.0.zip \
    --title "Baklava v0.1.0 - Initial Release" \
    --notes "${CHANGELOG}" \
    --repo "${GITHUB_USER}/${REPO_NAME}"

echo "✅ GitHub release created"
echo ""

# Step 9: Update manifest with checksum
echo "📝 Step 9: Updating manifest.json with checksum..."
cd /home/j4ckgrey/zilean/jellyfin-plugin-baklava
sed -i "s/\"checksum\": \"TBD\"/\"checksum\": \"${CHECKSUM}\"/" manifest.json
echo "✅ Manifest updated"
echo ""

# Step 10: Commit and push manifest update
echo "💾 Step 10: Committing manifest update..."
git add manifest.json
git commit -m "Update manifest.json with v0.1.0 checksum: ${CHECKSUM}"
git push origin main
echo "✅ Manifest pushed"
echo ""

echo "=============================================="
echo "🎉 DEPLOYMENT COMPLETE!"
echo "=============================================="
echo ""
echo "📍 Repository: https://github.com/${GITHUB_USER}/${REPO_NAME}"
echo "📦 Release: https://github.com/${GITHUB_USER}/${REPO_NAME}/releases/tag/v0.1.0"
echo "📄 Manifest: https://raw.githubusercontent.com/${GITHUB_USER}/${REPO_NAME}/main/manifest.json"
echo ""
echo "✅ Next steps:"
echo "   1. Test installation from manifest URL in Jellyfin"
echo "   2. Add manifest URL to Jellyfin: Dashboard → Plugins → Repositories"
echo "   3. Install Baklava from catalog"
echo ""
echo "🔒 Repository is PRIVATE"
echo ""
