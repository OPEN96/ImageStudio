using ImageStudio.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

var passed = 0;
var failed = 0;

void Check(string name, bool condition, string? detail = null)
{
    if (condition)
    {
        passed++;
        Console.WriteLine($"  PASS  {name}");
    }
    else
    {
        failed++;
        Console.WriteLine($"  FAIL  {name}  {detail}");
    }
}

var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
var samplePng = Path.Combine(root, "samples", "sample.png");
var sampleJpg = Path.Combine(root, "samples", "sample.jpg");
var alphaPng = Path.Combine(root, "samples", "alpha.png");
var temp = Path.Combine(Path.GetTempPath(), "imagestudio-check");
Directory.CreateDirectory(temp);

Console.WriteLine($"样本目录: {root}");
Check("示例文件存在", File.Exists(samplePng) && File.Exists(sampleJpg) && File.Exists(alphaPng));

// ---------------------------------------------------------------- 编解码
Console.WriteLine("\n[编解码]");
var png = ImageCodec.Load(samplePng);
Check("PNG 尺寸", png.Width == 1600 && png.Height == 1000, $"{png.Width}x{png.Height}");

var jpg = ImageCodec.Load(sampleJpg);
Check("JPEG 尺寸", jpg.Width == 1600 && jpg.Height == 1000);

var alpha = ImageCodec.Load(alphaPng);
Check("PNG 透明通道保留", alpha[5, 5].A == 0 && alpha[450, 350].A == 255);

var outPng = Path.Combine(temp, "out.png");
ImageCodec.Save(png, outPng, ImageFormatKind.Png, 90);
Check("PNG 往返", ImageCodec.Load(outPng).Width == 1600);

var outJpg = Path.Combine(temp, "out.jpg");
ImageCodec.Save(png, outJpg, ImageFormatKind.Jpeg, 85);
var jpgInfo = new FileInfo(outJpg).Length;
ImageCodec.Save(png, Path.Combine(temp, "out100.jpg"), ImageFormatKind.Jpeg, 100);
var jpgInfo100 = new FileInfo(Path.Combine(temp, "out100.jpg")).Length;
Check("JPEG 质量影响体积", jpgInfo100 > jpgInfo, $"{jpgInfo} vs {jpgInfo100}");

Check("按扩展名识别格式", ImageCodec.KindFromPath("a.JPG") == ImageFormatKind.Jpeg
    && ImageCodec.KindFromPath("a.webp") == ImageFormatKind.Webp
    && ImageCodec.KindFromPath("a.png") == ImageFormatKind.Png);

// ---------------------------------------------------------------- 几何
Console.WriteLine("\n[几何变换]");
var rotated = ImageOps.RotateQuarter(png, true);
Check("顺时针 90° 尺寸交换", rotated.Width == 1000 && rotated.Height == 1600);
Check("旋转 90° 像素位置正确", rotated[1000 - 1 - 10, 20] == png[20, 10]);

var ccw = ImageOps.RotateQuarter(png, false);
Check("逆时针 90° 尺寸交换", ccw.Width == 1000 && ccw.Height == 1600);

var back = ImageOps.RotateQuarter(rotated, false);
Check("旋转可逆", back.Width == png.Width && back.Height == png.Height);

var cropped = ImageOps.Crop(png, new Rectangle(100, 50, 400, 300));
Check("裁剪尺寸", cropped.Width == 400 && cropped.Height == 300);
Check("裁剪像素对齐", cropped[0, 0] == png[100, 50]);

var clamped = ImageOps.Crop(png, new Rectangle(-50, -50, 200, 200));
Check("越界裁剪被裁边", clamped.Width == 150 && clamped.Height == 150, $"{clamped.Width}x{clamped.Height}");

var flippedH = ImageOps.Flip(png, true);
Check("水平翻转像素", flippedH[0, 0] == png[1599, 0]);
var flippedTwice = ImageOps.Flip(flippedH, true);
Check("水平翻转两次还原", flippedTwice[20, 20] == png[20, 20]);

var rot180 = ImageOps.Rotate180(png);
Check("180° 尺寸不变", rot180.Width == png.Width && rot180.Height == png.Height);
Check("180° 像素映射", rot180[0, 0] == png[1599, 999]);

var resized = ImageOps.Resize(png, 800, 500);
Check("缩放尺寸", resized.Width == 800 && resized.Height == 500);

var rotated45 = ImageOps.RotateArbitrary(png, 45);
Check("任意角度画布扩展", rotated45.Width > png.Width && rotated45.Height > png.Height,
    $"{rotated45.Width}x{rotated45.Height}");

// ---------------------------------------------------------------- 色彩与滤镜
Console.WriteLine("\n[色彩与滤镜]");
static double MeanLuma(Image<Bgra32> img)
{
    double sum = 0;
    long count = 0;
    for (var y = 0; y < img.Height; y += 7)
    for (var x = 0; x < img.Width; x += 7)
    {
        var p = img[x, y];
        sum += 0.299 * p.R + 0.587 * p.G + 0.114 * p.B;
        count++;
    }
    return sum / count;
}

var baseLuma = MeanLuma(png);
var brighter = ImageOps.Adjust(png, 0.25, 0, 0);
Check("亮度提高", MeanLuma(brighter) > baseLuma + 15, $"{baseLuma:0.0} -> {MeanLuma(brighter):0.0}");

var darker = ImageOps.Adjust(png, -0.25, 0, 0);
Check("亮度降低", MeanLuma(darker) < baseLuma - 15);

var contrast = ImageOps.Adjust(png, 0, 0.35, 0);
Check("对比度提升离散度", StdDev(contrast) > StdDev(png), $"{StdDev(png):0.0} -> {StdDev(contrast):0.0}");

var saturated = ImageOps.Adjust(png, 0, 0, 0.6);
Check("饱和度提升改变像素", saturated[200, 200] != png[200, 200]);

var gray = ImageOps.Grayscale(png);
Check("灰度 R=G=B", gray[300, 300].R == gray[300, 300].G && gray[300, 300].G == gray[300, 300].B);

var inverted = ImageOps.Invert(png);
Check("反色正确", inverted[10, 10].R == 255 - png[10, 10].R);

var sepia = ImageOps.Sepia(png);
Check("棕褐色偏暖", sepia[300, 300].R > sepia[300, 300].B);

var edges = ImageOps.Edges(png);
Check("边缘检测保持尺寸", edges.Width == png.Width && edges.Height == png.Height);

var blurred = ImageOps.Blur(png, 4);
Check("模糊保持尺寸", blurred.Width == png.Width && blurred.Height == png.Height);

var sharpened = ImageOps.Sharpen(png, 1);
Check("锐化保持尺寸", sharpened.Width == png.Width && sharpened.Height == png.Height);

// ---------------------------------------------------------------- 文档与历史
Console.WriteLine("\n[文档与撤销栈]");
var doc = new ImageDocument(ImageCodec.Load(samplePng), samplePng);
Check("初始只有背景层", doc.Layers.Count == 1 && !doc.CanUndo && !doc.IsDirty);
Check("合成尺寸与文档一致", doc.Composite().Width == 1600 && doc.Composite().Height == 1000);

doc.TransformAll("裁剪",
    img => ImageOps.Crop(img, new Rectangle(0, 0, 800, 500)),
    m => ImageOps.Crop(m, new Rectangle(0, 0, 800, 500)));
Check("提交后尺寸变化", doc.Width == 800 && doc.Height == 500);
Check("图层尺寸随画布同步", doc.Layers[0].Width == 800);
Check("提交后可撤销且标记脏", doc.CanUndo && doc.IsDirty);

doc.TransformAll("旋转", img => ImageOps.RotateQuarter(img, true), m => ImageOps.RotateQuarter(m, true));
Check("连续提交尺寸", doc.Width == 500 && doc.Height == 800);

doc.Undo();
Check("撤销回退一步", doc.Width == 800 && doc.Height == 500);
Check("撤销后可重做", doc.CanRedo);

doc.Redo();
Check("重做恢复", doc.Width == 500 && doc.Height == 800);

doc.Undo();
doc.Undo();
Check("撤销到底", doc.Width == 1600 && doc.Height == 1000);
Check("到底后不可再撤销", !doc.CanUndo);

doc.Dispose();

// 历史条目上限
Console.WriteLine("\n[历史上限]");
var doc2 = new ImageDocument(new Image<Bgra32>(64, 64));
for (var i = 0; i < 40; i++)
{
    using var scope = doc2.BeginEdit("步骤" + i);
    doc2.Layers[0].ReplacePixels(Solid((byte)(i * 5 % 255)));
}
Check("历史长度受限", HistoryLength(doc2) <= 20, HistoryLength(doc2).ToString());
doc2.Dispose();

// ---------------------------------------------------------------- 图层合成
Console.WriteLine("\n[图层合成]");

static Bgra32 At(Image<Bgra32> img, int x, int y) => img[x, y];

var stacked = new ImageDocument(SolidImage(32, 32, 0, 0, 255));      // 蓝底
Check("文档初始一层", stacked.Layers.Count == 1);

var top = stacked.AddLayer("顶层");
top.ReplacePixels(SolidImage(32, 32, 255, 0, 0));                     // 红色
Check("新建图层位于上方", stacked.Layers.Count == 2 && stacked.ActiveIndex == 1);

using (var c = stacked.Composite())
{
    var p = At(c, 10, 10);
    Check("上层完全遮盖下层", p.R == 255 && p.B == 0 && p.A == 255, $"{p.R},{p.G},{p.B}");
}

top.IsVisible = false;
stacked.Touch();
using (var c = stacked.Composite())
{
    var p = At(c, 10, 10);
    Check("隐藏图层后露出底层", p.B == 255 && p.R == 0, $"{p.R},{p.G},{p.B}");
}

top.IsVisible = true;
top.Opacity = 0.5;
stacked.Touch();
using (var c = stacked.Composite())
{
    var p = At(c, 10, 10);
    Check("50% 不透明度混合居中",
        Math.Abs(p.R - 128) <= 1 && Math.Abs(p.B - 128) <= 1 && p.A == 255,
        $"{p.R},{p.G},{p.B}");
}

top.Opacity = 1;
top.BlendMode = BlendMode.Multiply;
stacked.Touch();
using (var c = stacked.Composite())
{
    var p = At(c, 10, 10);
    Check("正片叠底：红×蓝=黑", p.R == 0 && p.G == 0 && p.B == 0, $"{p.R},{p.G},{p.B}");
}

top.BlendMode = BlendMode.Screen;
stacked.Touch();
using (var c = stacked.Composite())
{
    var p = At(c, 10, 10);
    Check("滤色：红+蓝=品红", p.R == 255 && p.B == 255 && p.G == 0, $"{p.R},{p.G},{p.B}");
}

top.BlendMode = BlendMode.Normal;

// 蒙版
top.Mask = Layer.CreateMask(32, 32, 0);
stacked.Touch();
using (var c = stacked.Composite())
{
    var p = At(c, 10, 10);
    Check("全黑蒙版等同隐藏", p.B == 255 && p.R == 0, $"{p.R},{p.G},{p.B}");
}

top.Mask.Dispose();
top.Mask = Layer.CreateMask(32, 32, 128);
stacked.Touch();
using (var c = stacked.Composite())
{
    var p = At(c, 10, 10);
    Check("半灰蒙版混合居中", Math.Abs(p.R - 128) <= 2 && Math.Abs(p.B - 128) <= 2, $"{p.R},{p.G},{p.B}");
}

stacked.SetMaskEnabled(1, false);
using (var c = stacked.Composite())
{
    var p = At(c, 10, 10);
    Check("停用蒙版后恢复完全显示", p.R == 255 && p.B == 0, $"{p.R},{p.G},{p.B}");
}
stacked.SetMaskEnabled(1, true);
stacked.RemoveMask(1);
Check("删除蒙版", !stacked.Layers[1].HasMask);

// 亮度蒙版
var luminanceDoc = new ImageDocument(SolidImage(8, 8, 128, 128, 128));
luminanceDoc.AddMaskFromLuminance(0);
Check("按亮度生成蒙版", luminanceDoc.Layers[0].HasMask
    && Math.Abs(luminanceDoc.Layers[0].Mask![0, 0].PackedValue - 128) <= 1);

// 预览覆盖（色彩调整只作用于当前图层）
Console.WriteLine("\n[图层预览覆盖]");
var previewDoc = new ImageDocument(SolidImage(16, 16, 0, 0, 255));
var previewTop = previewDoc.AddLayer("顶层");
previewTop.ReplacePixels(SolidImage(16, 16, 255, 0, 0));

using (var over = previewDoc.CompositeWith(1, SolidImage(16, 16, 0, 255, 0)))
    Check("预览覆盖层参与合成", At(over, 3, 3).G == 255 && At(over, 3, 3).R == 0,
        $"{At(over, 3, 3).R},{At(over, 3, 3).G}");

Check("预览不改动图层像素", previewTop.Pixels[3, 3].R == 255);

using (var normal = previewDoc.Composite())
    Check("预览结束后恢复原合成", At(normal, 3, 3).R == 255);
previewDoc.Dispose();

// 顺序 / 合并 / 拼合
Console.WriteLine("\n[图层顺序与合并]");var orderDoc = new ImageDocument(SolidImage(16, 16, 0, 0, 255));
orderDoc.AddLayer("红层").ReplacePixels(SolidImage(16, 16, 255, 0, 0));
using (var c = orderDoc.Composite())
    Check("默认顶层在上", At(c, 1, 1).R == 255);

orderDoc.MoveLayer(1, 0);
using (var c = orderDoc.Composite())
    Check("下移后底层在上", At(c, 1, 1).B == 255, $"{At(c, 1, 1).R}");

orderDoc.MoveLayer(0, 1);
var beforeMerge = orderDoc.Composite().Clone();
orderDoc.MergeDown(1);
Check("向下合并后层数减少", orderDoc.Layers.Count == 1);
using (var after = orderDoc.Composite())
{
    Check("合并结果与合并前一致",
        At(after, 5, 5).R == At(beforeMerge, 5, 5).R &&
        At(after, 5, 5).B == At(beforeMerge, 5, 5).B,
        $"{At(after, 5, 5).R} vs {At(beforeMerge, 5, 5).R}");
}
beforeMerge.Dispose();

orderDoc.Undo();
Check("撤销合并恢复图层", orderDoc.Layers.Count == 2);
orderDoc.Redo();
Check("重做合并再次生效", orderDoc.Layers.Count == 1);

Console.WriteLine("\n[拼合与保护]");
var flatDoc = new ImageDocument(SolidImage(16, 16, 0, 255, 0));
flatDoc.AddLayer("半透明");
flatDoc.Layers[1].ReplacePixels(SolidImage(16, 16, 255, 0, 0, 128));
var preFlatten = flatDoc.Composite().Clone();
flatDoc.Flatten();
Check("拼合后只剩一层", flatDoc.Layers.Count == 1);
Check("拼合保留透明合成结果", flatDoc.Layers[0].Pixels[8, 8].A == 255);
using (var post = flatDoc.Composite())
    Check("拼合前后视觉一致",
        Math.Abs(post[8, 8].R - preFlatten[8, 8].R) <= 1 &&
        Math.Abs(post[8, 8].G - preFlatten[8, 8].G) <= 1,
        $"{post[8, 8].R},{post[8, 8].G} vs {preFlatten[8, 8].R},{preFlatten[8, 8].G}");
preFlatten.Dispose();

flatDoc.DeleteLayer(0);
Check("只剩一层时拒绝删除", flatDoc.Layers.Count == 1);
flatDoc.Dispose();
stacked.Dispose();
orderDoc.Dispose();
luminanceDoc.Dispose();

Console.WriteLine($"\n结果: {passed} 通过, {failed} 失败");
return failed == 0 ? 0 : 1;

static Image<Bgra32> SolidImage(int w, int h, byte r, byte g, byte b, byte a = 255)
{
    var img = new Image<Bgra32>(w, h);
    img.ProcessPixelRows(accessor =>
    {
        for (var y = 0; y < accessor.Height; y++)
            accessor.GetRowSpan(y).Fill(new Bgra32 { R = r, G = g, B = b, A = a });
    });
    return img;
}

static Image<Bgra32> Solid(byte value)
{
    var img = new Image<Bgra32>(64, 64);
    img.ProcessPixelRows(accessor =>
    {
        for (var y = 0; y < accessor.Height; y++)
            accessor.GetRowSpan(y).Fill(new Bgra32 { R = value, G = value, B = value, A = 255 });
    });
    return img;
}

static double StdDev(Image<Bgra32> img)
{
    double sum = 0, sumSq = 0;
    long count = 0;
    for (var y = 0; y < img.Height; y += 5)
    for (var x = 0; x < img.Width; x += 5)
    {
        var p = img[x, y];
        var luma = 0.299 * p.R + 0.587 * p.G + 0.114 * p.B;
        sum += luma;
        sumSq += luma * luma;
        count++;
    }
    var mean = sum / count;
    return Math.Sqrt(sumSq / count - mean * mean);
}

static int HistoryLength(ImageDocument doc)
{
    var count = 0;
    while (doc.CanUndo)
    {
        doc.Undo();
        count++;
        if (count > 200) break;
    }
    return count;
}
