# ImageStudio

基于 **Avalonia 12 / .NET 10** 的跨平台图像编辑器：图层与混合模式、选区与抠图、画笔/橡皮/油漆桶/渐变/文字，全部手写像素管线（ImageSharp），不依赖任何商业组件。

## 平台支持

| 平台 | 架构 | 产物 |
| --- | --- | --- |
| Linux | x64 | `ImageStudio-linux-x64.tar.gz` |
| Linux | ARM64（树莓派、ARM 服务器） | `ImageStudio-linux-arm64.tar.gz` |
| Windows | x64 | `ImageStudio-win-x64.zip` |

三个产物都是**自包含单文件**（single-file + 压缩），目标机不需要预装 .NET 运行时。
Linux 版额外附带 `.desktop` 桌面入口、图标与 `install.sh`（装到 `~/.local`，无需 root）。

### Linux 运行依赖

`libX11`、`libICE`、`libSM`、`libXext`、`fontconfig`、`freetype`、`libicu`，以及一款中文字体。
主流桌面发行版默认已具备；精简系统/容器里补齐即可：

```bash
# Debian / Ubuntu
sudo apt install libx11-6 libice6 libsm6 libxext6 libfontconfig1 libfreetype6 libicu72 fonts-noto-cjk
# Fedora
sudo dnf install libX11 libICE libSM libXext fontconfig freetype icu google-noto-sans-cjk-fonts
```

> 字体：界面与文字工具会在启动时枚举系统字体，按 `微软雅黑 → 苹方 → Noto Sans CJK/思源 → WenQuanYi` 的顺序
> 挑一个真实存在的作为默认中文字体（见 `Services/FontCatalog.cs`），所以同一份代码在三个平台上都能正常显示中文。

## 本地构建

```bash
dotnet build ImageStudio.slnx -c Release        # 编译
dotnet run   --project ImageStudio              # 直接运行
dotnet run   --project tools/ModelCheck -c Release   # 模型层 140 项断言
```

## 打包最小版本

```bash
./scripts/publish.sh                    # 三平台全打（Linux 上交叉打包 win-x64 也可以）
./scripts/publish.sh linux-arm64        # 只打一个
VERSION=1.4.0 ./scripts/publish.sh      # 指定版本号
```

产物在 `out/`，含 `tar.gz` / `zip` 与 `sha256` 校验和。

也可以直接调用 MSBuild（参数与 CI 一致）：

```bash
dotnet publish ImageStudio/ImageStudio.csproj -c Release -r linux-arm64 \
  --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true
```

需要更小的体积可以加 `-p:Trimmed=true`（启用 IL 裁剪，程序集裁剪后请自行回归验证，默认关闭）。

## 持续集成

`.github/workflows/ci.yml`：

1. **test** — Ubuntu 上还原、编译（`-warnaserror`）、跑 `ModelCheck`；
2. **build** — 三个平台并行发布、打包、校验 ELF 架构、生成 SHA256；
   linux-x64 还会在 Xvfb 里真启动一次做冒烟测试；
3. **release** — 只有推 `v*` 标签时触发，把三份产物汇总发到 GitHub Release。

发版：

```bash
git tag v1.3.0 && git push origin v1.3.0
```

## 目录结构

```
ImageStudio/
├─ Models/        像素管线：图层合成、选区、滤镜、画笔、渐变、文档与撤销栈
├─ Controls/      ImageCanvas 画布控件（缩放/平移/裁剪/选区/绘制交互）
├─ ViewModels/    MVVM 视图模型
├─ Services/      字体目录、文字离屏渲染
├─ Views/         主窗口与各种对话框
packaging/linux/  .desktop、图标、安装脚本
scripts/          本地打包脚本
tools/ModelCheck/ 模型层断言测试（控制台）
```
