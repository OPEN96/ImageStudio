#!/bin/sh
# ImageStudio 的 Linux 本地安装脚本（只装到当前用户目录，不需要 root）。
# 用法：把 ImageStudio、imagestudio.png、imagestudio.desktop 与本脚本放在同一目录，
#      然后执行 ./install.sh            —— 安装
#            ./install.sh --uninstall  —— 卸载
set -eu

HERE=$(cd "$(dirname "$0")" && pwd)
BIN_DIR="$HOME/.local/bin"
DATA_DIR="${XDG_DATA_HOME:-$HOME/.local/share}"
APP_DIR="$DATA_DIR/applications"
ICON_DIR="$DATA_DIR/icons/hicolor/256x256/apps"

if [ "${1:-}" = "--uninstall" ]; then
    rm -f "$BIN_DIR/ImageStudio" "$APP_DIR/imagestudio.desktop" "$ICON_DIR/imagestudio.png"
    command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database "$APP_DIR" || true
    echo "已卸载 ImageStudio"
    exit 0
fi

[ -f "$HERE/ImageStudio" ] || { echo "找不到 ImageStudio 可执行文件" >&2; exit 1; }

mkdir -p "$BIN_DIR" "$APP_DIR" "$ICON_DIR"
install -m 755 "$HERE/ImageStudio" "$BIN_DIR/ImageStudio"

if [ -f "$HERE/imagestudio.png" ]; then
    install -m 644 "$HERE/imagestudio.png" "$ICON_DIR/imagestudio.png"
fi

if [ -f "$HERE/imagestudio.desktop" ]; then
    # 桌面文件里的 Exec 使用 PATH 中的 ImageStudio，这里仅替换图标为绝对路径，兼容不认主题图标的桌面
    sed "s|^Icon=.*|Icon=$ICON_DIR/imagestudio.png|" "$HERE/imagestudio.desktop" > "$APP_DIR/imagestudio.desktop"
    chmod 644 "$APP_DIR/imagestudio.desktop"
fi

command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database "$APP_DIR" || true
command -v gtk-update-icon-cache >/dev/null 2>&1 && gtk-update-icon-cache -q -t "$DATA_DIR/icons/hicolor" 2>/dev/null || true

echo "已安装：$BIN_DIR/ImageStudio"
echo "如果 $BIN_DIR 不在 PATH 中，请加入后再运行：export PATH=\"$BIN_DIR:\$PATH\""
