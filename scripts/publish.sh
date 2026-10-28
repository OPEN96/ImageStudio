#!/usr/bin/env bash
# 本地一键打包各平台最小版本（与 CI 中的步骤一致）。
#
#   ./scripts/publish.sh                 # 三个平台全打：linux-x64、linux-arm64、win-x64
#   ./scripts/publish.sh linux-x64       # 只打一个
#   VERSION=1.4.0 ./scripts/publish.sh   # 指定版本号（默认读 git tag，取不到就用 0.0.0-dev）
#
# 产物输出到 out/ 目录：每个平台一个子目录 + 一个压缩包（Linux 为 tar.gz，Windows 为 zip）。
set -euo pipefail

cd "$(dirname "$0")/.."
ROOT=$(pwd)
CONFIGURATION=${CONFIGURATION:-Release}
VERSION=${VERSION:-$(git describe --tags --abbrev=0 2>/dev/null | sed 's/^v//' || echo 0.0.0-dev)}

RIDS=("$@")
if [ ${#RIDS[@]} -eq 0 ]; then
    RIDS=(linux-x64 linux-arm64 win-x64)
fi

echo "版本：$VERSION"
rm -rf out
mkdir -p out

for rid in "${RIDS[@]}"; do
    echo ""
    echo "=== 发布 $rid ==="
    dotnet publish ImageStudio/ImageStudio.csproj \
        -c "$CONFIGURATION" \
        -r "$rid" \
        --self-contained true \
        -p:Version="$VERSION" \
        -o "out/$rid"

    case "$rid" in
        linux-*)
            cp packaging/linux/imagestudio.desktop packaging/linux/imagestudio.png \
               packaging/linux/install.sh "out/$rid/"
            chmod +x "out/$rid/ImageStudio" "out/$rid/install.sh"
            chmod 644 "out/$rid/imagestudio.desktop" "out/$rid/imagestudio.png"
            tar -C "out/$rid" -czf "out/ImageStudio-$rid.tar.gz" .
            ( cd out && sha256sum "ImageStudio-$rid.tar.gz" > "ImageStudio-$rid.tar.gz.sha256" )
            ;;
        win-*)
            # Git Bash / MSYS 上通常没有 zip，退回到 PowerShell 的 Compress-Archive
            if command -v zip >/dev/null 2>&1; then
                ( cd "out/$rid" && zip -q -r "../ImageStudio-$rid.zip" . )
            else
                powershell -NoProfile -Command \
                    "Compress-Archive -Path 'out/$rid/*' -DestinationPath 'out/ImageStudio-$rid.zip' -Force"
            fi
            ( cd out && sha256sum "ImageStudio-$rid.zip" > "ImageStudio-$rid.zip.sha256" )
            ;;
    esac
done

echo ""
echo "=== 产物 ==="
ls -lh out/*.tar.gz out/*.zip 2>/dev/null || true
