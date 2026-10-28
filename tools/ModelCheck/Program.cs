using System.Numerics;
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


// ---------------------------------------------------------------- 选区
Console.WriteLine("\n[选区形状]");
var selRect = Selection.FromRect(100, 80, new Rectangle(10, 20, 30, 40));
Check("矩形选区尺寸", selRect.Width == 100 && selRect.Height == 80);
Check("矩形选区外接框",
    selRect.Bounds.X == 10 && selRect.Bounds.Y == 20 && selRect.Bounds.Width == 30 && selRect.Bounds.Height == 40,
    selRect.Bounds.ToString());
Check("矩形选区像素数", selRect.InsidePixelCount == 30 * 40, selRect.InsidePixelCount.ToString());
Check("矩形选区边界为硬边",
    selRect.Coverage[20 * 100 + 10] == 255 && selRect.Coverage[19 * 100 + 10] == 0 &&
    selRect.Coverage[0] == 0);

var clippedSel = Selection.FromRect(50, 50, new Rectangle(-10, 40, 30, 30));
Check("越界矩形被裁剪", clippedSel.InsidePixelCount == 20 * 10, clippedSel.InsidePixelCount.ToString());

var selEllipse = Selection.FromEllipse(100, 100, new Rectangle(0, 0, 100, 100));
Check("椭圆四角未选中", selEllipse.Coverage[0] == 0 && selEllipse.Coverage[99] == 0);
Check("椭圆中心选中", selEllipse.Coverage[50 * 100 + 50] == 255);
Check("椭圆面积接近 π/4", selEllipse.InsidePixelCount is > 7600 and < 7900,
    selEllipse.InsidePixelCount.ToString());

var selTriangle = Selection.FromPolygon(100, 100, [(10f, 10f), (90f, 10f), (10f, 90f)]);
Check("三角形面积接近一半", selTriangle.InsidePixelCount is > 3000 and < 3400,
    selTriangle.InsidePixelCount.ToString());
Check("多边形外的点为 0", selTriangle.Coverage[80 * 100 + 80] == 0);

Console.WriteLine("\n[选区组合]");
var selLeft = Selection.FromRect(100, 100, new Rectangle(0, 0, 50, 100));
var selRight = Selection.FromRect(100, 100, new Rectangle(50, 0, 50, 100));
Check("添加模式合并", selLeft.Combine(selRight, SelectionCombine.Add).InsidePixelCount == 10000);

var overlapA = Selection.FromRect(100, 100, new Rectangle(0, 0, 60, 60));
var overlapB = Selection.FromRect(100, 100, new Rectangle(40, 40, 60, 60));
Check("交集只保留重叠", overlapA.Combine(overlapB, SelectionCombine.Intersect).InsidePixelCount == 400,
    overlapA.Combine(overlapB, SelectionCombine.Intersect).InsidePixelCount.ToString());
Check("减去移除重叠", overlapA.Combine(overlapB, SelectionCombine.Subtract).InsidePixelCount == 3200,
    overlapA.Combine(overlapB, SelectionCombine.Subtract).InsidePixelCount.ToString());
Check("反选得到补集", selLeft.Invert().InsidePixelCount == 5000);
Check("替换模式直接取新选区",
    selLeft.Combine(selRight, SelectionCombine.Replace).InsidePixelCount == 5000);

Console.WriteLine("\n[羽化]");
var hardSel = Selection.FromRect(60, 60, new Rectangle(20, 20, 20, 20));
var softSel = hardSel.Feather(6);
Check("羽化后中心仍为全选", softSel.Coverage[30 * 60 + 30] == 255);
Check("羽化后边界为半透明", softSel.Coverage[30 * 60 + 20] is > 0 and < 255,
    softSel.Coverage[30 * 60 + 20].ToString());
Check("羽化扩展覆盖范围", softSel.Bounds.Width > hardSel.Bounds.Width,
    $"{hardSel.Bounds.Width} -> {softSel.Bounds.Width}");
Check("羽化不修改原选区", hardSel.Coverage[30 * 60 + 20] == 255);
Check("零半径羽化保持原样", hardSel.Feather(0).Coverage[30 * 60 + 20] == 255);

Console.WriteLine("\n[魔棒]");
var blocks = new Image<Bgra32>(80, 40);
blocks.ProcessPixelRows(accessor =>
{
    for (var y = 0; y < accessor.Height; y++)
        accessor.GetRowSpan(y).Fill(new Bgra32 { R = 255, G = 255, B = 255, A = 255 });
});

void PaintBlock(int x0, int y0, int w, int h)
{
    var block = new Bgra32 { R = 220, G = 30, B = 40, A = 255 };
    blocks.ProcessPixelRows(accessor =>
    {
        for (var y = y0; y < y0 + h; y++)
        {
            var row = accessor.GetRowSpan(y);
            for (var x = x0; x < x0 + w; x++)
                row[x] = block;
        }
    });
}

PaintBlock(5, 5, 16, 16);
PaintBlock(55, 5, 16, 16);

var wandOne = Selection.Wand(blocks, 10, 10, 10, true);
Check("连续魔棒只选中一块", wandOne.InsidePixelCount == 256, wandOne.InsidePixelCount.ToString());

var wandAll = Selection.Wand(blocks, 10, 10, 10, false);
Check("非连续魔棒选中全部同色", wandAll.InsidePixelCount == 512, wandAll.InsidePixelCount.ToString());

var wandOutside = Selection.Wand(blocks, -3, 5, 20, true);
Check("越界种子返回空选区", wandOutside.IsEmpty);

var gradientImg = new Image<Bgra32>(200, 8);
gradientImg.ProcessPixelRows(accessor =>
{
    for (var y = 0; y < accessor.Height; y++)
    {
        var row = accessor.GetRowSpan(y);
        for (var x = 0; x < 200; x++)
        {
            var v = (byte)(x * 255 / 199);
            row[x] = new Bgra32 { R = v, G = v, B = v, A = 255 };
        }
    }
});

var narrowWand = Selection.Wand(gradientImg, 100, 4, 5, true);
var wideWand = Selection.Wand(gradientImg, 100, 4, 40, true);
Check("容差越大选区越大", wideWand.InsidePixelCount > narrowWand.InsidePixelCount,
    $"{narrowWand.InsidePixelCount} -> {wideWand.InsidePixelCount}");

Console.WriteLine("\n[选区轮廓]");
var outlineSel = Selection.FromRect(20, 20, new Rectangle(4, 4, 10, 10));
var outline = outlineSel.GetOutline();
Check("矩形轮廓是一条闭合线", outline is { Count: 1 });
Check("轮廓简化为四个角", outline is [{ Length: 4 }], outline?[0].Length.ToString());

var outlineCorners = outline?[0] ?? [];
Check("轮廓坐标贴住选区边角",
    outlineCorners.Any(p => p is { X: 4, Y: 4 }) && outlineCorners.Any(p => p is { X: 14, Y: 14 }),
    string.Join(" ", outlineCorners));
Check("空选区没有轮廓", Selection.Empty(10, 10).GetOutline() is { Count: 0 });

Console.WriteLine("\n[选区参与像素操作]");
var strip = SolidImage(4, 1, 100, 100, 100);
var rightHalf = Selection.FromRect(4, 1, new Rectangle(2, 0, 2, 1));
var brighterStrip = SolidImage(4, 1, 200, 200, 200);

using (var blended = ImageOps.BlendByMask(strip, brighterStrip, rightHalf))
    Check("混合只作用在选区内", blended[0, 0].R == 100 && blended[3, 0].R == 200,
        $"{blended[0, 0].R}/{blended[3, 0].R}");

using (var cleared = ImageOps.ClearByMask(strip, rightHalf))
    Check("删除选区内容只清空选区内", cleared[0, 0].A == 255 && cleared[3, 0].A == 0,
        $"{cleared[0, 0].A}/{cleared[3, 0].A}");

using (var filled = ImageOps.FillByMask(strip, rightHalf, new Bgra32 { R = 255, G = 0, B = 0, A = 255 }))
    Check("填充只作用在选区内",
        filled[0, 0].R == 100 && filled[3, 0].R == 255 && filled[3, 0].B == 0);

using (var extracted = ImageOps.ExtractByMask(strip, rightHalf))
    Check("提取选区得到透明底图",
        extracted[0, 0].A == 0 && extracted[3, 0].A == 255 && extracted[3, 0].R == 100);

var halfCoverage = Selection.FromCoverage(4, 1, [0, 0, 128, 255]);
using (var mixed = ImageOps.BlendByMask(strip, brighterStrip, halfCoverage))
    Check("半覆盖按比例混合", Math.Abs(mixed[2, 0].R - 150) <= 1, mixed[2, 0].R.ToString());

Console.WriteLine("\n[选区跟随画布变换]");
var movingSel = Selection.FromRect(100, 100, new Rectangle(10, 10, 50, 50));
var croppedSelection = movingSel.Crop(new Rectangle(20, 20, 30, 30));
Check("选区跟随裁剪", croppedSelection.Width == 30 && croppedSelection.InsidePixelCount == 900,
    croppedSelection.InsidePixelCount.ToString());

using (var rotatedMask = ImageOps.RotateQuarter(movingSel.ToMask(), true))
{
    var rotatedSelection = Selection.FromMask(rotatedMask);
    Check("选区跟随旋转",
        rotatedSelection.Bounds.X == 40 && rotatedSelection.Bounds.Y == 10 &&
        rotatedSelection.Bounds.Width == 50 && rotatedSelection.Bounds.Height == 50,
        rotatedSelection.Bounds.ToString());
}

var scaledSelection = movingSel.ScaleTo(200, 200);
Check("选区跟随缩放", scaledSelection.Width == 200 && scaledSelection.InsidePixelCount > 9000,
    scaledSelection.InsidePixelCount.ToString());

Console.WriteLine("\n[选区与文档]");
var selDoc = new ImageDocument(SolidImage(64, 64, 10, 20, 30));
var maskFromSelection = Selection.FromRect(64, 64, new Rectangle(0, 0, 32, 64));
selDoc.SetMaskFromSelection(0, maskFromSelection);
Check("选区可生成图层蒙版", selDoc.Layers[0].HasMask && selDoc.Layers[0].MaskEnabled);
Check("蒙版内部为白色", selDoc.Layers[0].Mask![4, 4].PackedValue == 255);
Check("蒙版外部为黑色", selDoc.Layers[0].Mask![60, 4].PackedValue == 0);

var copyPixels = ImageOps.ExtractByMask(selDoc.Layers[0].Pixels, Selection.FromRect(64, 64, new Rectangle(0, 0, 10, 10)));
selDoc.AddLayerFromPixels("拷贝", copyPixels);
Check("通过拷贝建立图层", selDoc.LayerCount == 2 && selDoc.ActiveIndex == 1);
Check("拷贝图层内容正确", selDoc.Layers[1].Pixels[5, 5].A == 255 && selDoc.Layers[1].Pixels[50, 50].A == 0);
selDoc.Dispose();
blocks.Dispose();
gradientImg.Dispose();

// ---------------------------------------------------------------- 绘画
Console.WriteLine("\n[画笔]");
var brushCanvas = SolidImage(64, 64, 255, 255, 255);
var brushColor = new Bgra32 { R = 220, G = 40, B = 40, A = 255 };

Check("笔刷覆盖率：中心为 1", StrokeSession.DabCoverage(0, 10, 1) == 1);
Check("笔刷覆盖率：半径外为 0", StrokeSession.DabCoverage(10, 10, 1) == 0);
Check("笔刷覆盖率：软边中间约为 0.5", Math.Abs(StrokeSession.DabCoverage(5, 10, 0) - 0.5) < 0.01,
    StrokeSession.DabCoverage(5, 10, 0).ToString("0.00"));

using (var stroke = new StrokeSession(brushCanvas, new BrushSettings { Size = 10, Hardness = 1 }, brushColor))
{
    stroke.MoveTo(32, 32);

    Check("硬边画笔落笔即中心实色",
        stroke.Pixels[32, 32].R == 220 && stroke.Pixels[32, 32].G == 40,
        $"{stroke.Pixels[32, 32].R},{stroke.Pixels[32, 32].G}");
    Check("笔刷边界外像素不变", stroke.Pixels[40, 32].R == 255, stroke.Pixels[40, 32].R.ToString());
    Check("笔刷边缘有抗锯齿",
        stroke.Pixels[35, 32].R is > 40 and < 255, stroke.Pixels[35, 32].R.ToString());
    Check("笔画不影响原图", brushCanvas[32, 32].R == 255);
    Check("落笔后 HasPaint 为真", stroke.HasPaint);
}

using (var stroke = new StrokeSession(brushCanvas, new BrushSettings { Size = 24, Hardness = 0, Opacity = 0.5 }, brushColor))
{
    stroke.MoveTo(32, 32);

    Check("半透明画笔混合到中值",
        Math.Abs(stroke.Pixels[32, 32].R - 238) <= 2 && Math.Abs(stroke.Pixels[32, 32].G - 148) <= 2,
        $"{stroke.Pixels[32, 32].R},{stroke.Pixels[32, 32].G}");

    stroke.MoveTo(32, 32);
    stroke.MoveTo(40, 32);
    stroke.MoveTo(32, 32);
    Check("同一笔内重复涂抹不会加深",
        Math.Abs(stroke.Pixels[32, 32].R - 238) <= 2, stroke.Pixels[32, 32].R.ToString());
}

using (var stroke = new StrokeSession(brushCanvas, new BrushSettings { Size = 20, Hardness = 0 }, brushColor))
{
    stroke.MoveTo(10, 10);
    stroke.MoveTo(50, 10);

    var middle = stroke.Pixels[30, 10];
    Check("快速拖动时笔迹连续", middle.R > 215, middle.R.ToString());
}

using (var eraser = new StrokeSession(SolidImage(64, 64, 200, 30, 30), new BrushSettings { Size = 16, Hardness = 1, Erase = true }, default))
{
    eraser.MoveTo(32, 32);
    Check("橡皮擦清空透明度", eraser.Pixels[32, 32].A == 0, eraser.Pixels[32, 32].A.ToString());
    Check("橡皮擦保留边缘", eraser.Pixels[0, 0].A == 255);
}

var clipHalf = Selection.FromRect(64, 64, new Rectangle(0, 0, 32, 64));
using (var stroke = new StrokeSession(brushCanvas, new BrushSettings { Size = 12, Hardness = 1 }, brushColor, clipHalf))
{
    stroke.MoveTo(20, 32);
    stroke.MoveTo(44, 32);

    Check("画笔受选区限制：选区内上色", stroke.Pixels[20, 32].R == 220);
    Check("画笔受选区限制：选区外不变", stroke.Pixels[44, 32].R == 255, stroke.Pixels[44, 32].R.ToString());
}

using (var stroke = new StrokeSession(brushCanvas, new BrushSettings { Size = 8 }, brushColor))
{
    stroke.MoveTo(-200, -200);
    stroke.MoveTo(-190, -190);
    Check("画布外落笔不越界", brushCanvas[0, 0].R == 255);
    Check("画布外落笔不产生内容", !stroke.HasPaint);
}

using (var stroke = new StrokeSession(SolidImage(20, 20, 0, 0, 0), new BrushSettings { Size = 60 }, brushColor))
{
    stroke.MoveTo(10, 10);
    Check("笔刷大于画布仍可整片覆盖", stroke.Pixels[0, 0].R == 220);
}

Console.WriteLine("\n[油漆桶]");
var bucketSource = new Image<Bgra32>(60, 30);
bucketSource.ProcessPixelRows(accessor =>
{
    for (var y = 0; y < accessor.Height; y++)
        accessor.GetRowSpan(y).Fill(new Bgra32 { R = 255, G = 255, B = 255, A = 255 });
});
bucketSource.ProcessPixelRows(accessor =>
{
    for (var y = 4; y < 20; y++)
    {
        var row = accessor.GetRowSpan(y);
        for (var x = 4; x < 24; x++)
            row[x] = new Bgra32 { R = 200, G = 20, B = 20, A = 255 };
    }
});

var green = new Bgra32 { R = 20, G = 200, B = 90, A = 255 };
using (var bucketTarget = SolidImage(60, 30, 255, 255, 255))
using (var filled = PaintOps.BucketFill(bucketTarget, bucketSource, null, 10, 10, 10, true, green))
{
    Check("油漆桶填充内部颜色", filled[10, 10].G == 200 && filled[10, 10].R == 20,
        $"{filled[10, 10].R},{filled[10, 10].G}");
    Check("油漆桶不越过边界", filled[2, 2].R == 255);
    Check("油漆桶不填充画布外区域", filled[40, 25].R == 255);
}

using (var bucketTarget = SolidImage(60, 30, 255, 255, 255))
using (var filled = PaintOps.BucketFill(bucketTarget, bucketSource,
           Selection.FromRect(60, 30, new Rectangle(0, 0, 12, 30)), 10, 10, 10, true, green))
{
    Check("油漆桶受选区裁剪", filled[6, 10].G == 200 && filled[18, 10].R == 255,
        $"{filled[6, 10].G}/{filled[18, 10].R}");
}

using (var bucketTarget = SolidImage(60, 30, 255, 255, 255))
using (var unchanged = PaintOps.BucketFill(bucketTarget, bucketSource, null, -5, -5, 10, true, green))
    Check("油漆桶越界种子点不修改图像", unchanged[10, 10].G == 255);

Console.WriteLine("\n[渐变]");
var gradientBase = SolidImage(100, 10, 0, 0, 0);
var white = new Bgra32 { R = 255, G = 255, B = 255, A = 255 };
var transparentWhite = new Bgra32 { R = 255, G = 255, B = 255, A = 0 };

using (var linear = PaintOps.Gradient(gradientBase, null, 0, 5, 100, 5, white, transparentWhite, GradientKind.Linear))
{
    Check("线性渐变起点接近前景色", linear[0, 5].R > 250, linear[0, 5].R.ToString());
    Check("线性渐变中点为半透明混合", Math.Abs(linear[50, 5].R - 127) <= 2, linear[50, 5].R.ToString());
    Check("线性渐变终点保持原样", linear[99, 5].R <= 2, linear[99, 5].R.ToString());
}

using (var radial = PaintOps.Gradient(gradientBase, null, 50, 5, 80, 5, white, transparentWhite, GradientKind.Radial))
{
    Check("径向渐变中心最亮", radial[50, 5].R > 245, radial[50, 5].R.ToString());
    Check("径向渐变外围淡出", radial[95, 5].R < 40, radial[95, 5].R.ToString());
    Check("径向渐变半径外不过度扩散", radial[50, 9].R < radial[50, 5].R);
}

using (var clipped = PaintOps.Gradient(gradientBase,
           Selection.FromRect(100, 10, new Rectangle(50, 0, 50, 10)),
           0, 5, 100, 5, white, white, GradientKind.Linear))
{
    Check("渐变受选区限制", clipped[10, 5].R == 0 && clipped[75, 5].R > 200,
        $"{clipped[10, 5].R}/{clipped[75, 5].R}");
}

var blue = new Bgra32 { R = 0, G = 0, B = 255, A = 255 };
using (var toBackground = PaintOps.Gradient(gradientBase, null, 0, 5, 100, 5, white, blue, GradientKind.Linear))
    Check("渐变到背景色", toBackground[99, 5].B > 240 && toBackground[99, 5].R < 20,
        $"{toBackground[99, 5].R},{toBackground[99, 5].B}");

using (var zero = PaintOps.Gradient(gradientBase, null, 20, 20, 20, 20, white, blue, GradientKind.Linear))
    Check("零长度渐变不修改图像", zero[50, 5].R == 0);

// ---------------------------------------------------------------- 移动
Console.WriteLine("\n[移动]");

using (var canvas = BlockImage(12, 12, new Rectangle(0, 0, 4, 4)))
using (var moved = MoveOps.Translate(canvas, 4, 0))
{
    Check("整幅平移后内容到位", moved[4, 0].A == 255 && moved[7, 3].A == 255,
        $"{moved[4, 0].A}/{moved[7, 3].A}");
    Check("整幅平移后原位透明", moved[0, 0].A == 0, moved[0, 0].A.ToString());
    Check("整幅平移后越界部分被裁掉", moved[8, 0].A == 0 && moved[11, 0].A == 0);
}

using (var canvas = BlockImage(12, 12, new Rectangle(0, 0, 4, 4)))
using (var same = MoveOps.Translate(canvas, 0, 0))
    Check("零位移保持原样", same[0, 0].A == 255 && same[5, 5].A == 0);

using (var canvas = BlockImage(12, 12, new Rectangle(0, 0, 4, 4)))
using (var far = MoveOps.Translate(canvas, -8, -8))
    Check("整幅移出画布后全透明", far[0, 0].A == 0 && far[11, 11].A == 0);

// 选区外必须保持原样：白块之外再放一个红色标记做“见证点”
using (var canvas = BlockImage(12, 12, new Rectangle(0, 0, 4, 4)))
{
    canvas[9, 9] = new Bgra32 { R = 200, G = 0, B = 0, A = 255 };
    var clip = Selection.FromRect(12, 12, new Rectangle(0, 0, 4, 4));
    using var moved = MoveOps.Translate(canvas, 6, 0, clip);

    Check("选区内容移动到新位置", moved[6, 0].A == 255 && moved[9, 3].A == 255,
        $"{moved[6, 0].A}/{moved[9, 3].A}");
    Check("选区原位被清空", moved[0, 0].A == 0 && moved[3, 3].A == 0);
    Check("选区外像素不受影响", moved[9, 9].A == 255 && moved[9, 9].R == 200,
        $"{moved[9, 9].A}/{moved[9, 9].R}");
    Check("选区边界外一列保持原样", moved[4, 0].A == 0);
}

// 半覆盖（羽化边缘）像素：原位减淡、目标位置按覆盖度叠加
var partialCoverage = new byte[16];
partialCoverage[0] = 128;
using (var sparse = SparseImage(4, 4, (0, 0, (byte)255)))
{
    var partial = Selection.FromCoverage(4, 4, partialCoverage);
    using var half = MoveOps.Translate(sparse, 2, 0, partial);

    Check("半覆盖像素原位减淡", half[0, 0].A is >= 120 and <= 136, half[0, 0].A.ToString());
    Check("半覆盖像素目标按覆盖度叠加", half[2, 0].A is >= 120 and <= 136, half[2, 0].A.ToString());
    Check("半覆盖移动不影响其他像素", half[3, 0].A == 0, half[3, 0].A.ToString());
}

// 蒙版跟随内容一起移动 + 一条历史记录
using (var moveDoc = new ImageDocument(BlockImage(12, 12, new Rectangle(0, 0, 4, 4))))
{
    var layer = moveDoc.ActiveLayer!;
    var mask = Layer.CreateMask(12, 12, 0);
    mask[0, 0] = new L8(200);
    layer.Mask = mask;

    var movedOk = moveDoc.MoveLayerContent(0, 4, 0, null, "移动图层");
    Check("文档移动返回成功", movedOk);
    Check("移动后像素到位", layer.Pixels[4, 0].A == 255 && layer.Pixels[0, 0].A == 0);
    Check("蒙版跟随内容移动", layer.Mask![4, 0].PackedValue == 200 && layer.Mask[0, 0].PackedValue == 0,
        $"{layer.Mask[4, 0].PackedValue}/{layer.Mask[0, 0].PackedValue}");
    Check("移动产生一条历史记录", moveDoc.CanUndo);

    moveDoc.Undo();
    var restored = moveDoc.ActiveLayer!;
    Check("撤销后内容回到原位", restored.Pixels[0, 0].A == 255 && restored.Pixels[4, 0].A == 0,
        $"{restored.Pixels[0, 0].A}/{restored.Pixels[4, 0].A}");

    Check("零位移不产生历史", !moveDoc.MoveLayerContent(0, 0, 0, null, "移动图层"));
}

// 选区内移动到已有内容上时按 source-over 叠加（整层平移是覆盖语义，不做混合）
using (var sparse = SparseImage(4, 4, (1, 0, (byte)128)))
{
    sparse[2, 0] = new Bgra32 { R = 0, G = 0, B = 255, A = 255 };

    var onlySecond = new byte[16];
    onlySecond[1] = 255;

    using var over = MoveOps.Translate(sparse, 1, 0, Selection.FromCoverage(4, 4, onlySecond));
    Check("选区内的半透明像素与目标内容混合",
        over[2, 0].A == 255 && over[2, 0].R is >= 120 and <= 136 && over[2, 0].B == 255,
        $"{over[2, 0].R},{over[2, 0].G},{over[2, 0].B}");
    Check("整层平移是覆盖语义", MoveOps.Translate(sparse, 1, 0)[2, 0].A == 128);
}

// ---------------------------------------------------------------- 自由变换
Console.WriteLine("\n[自由变换]");

// 8x8 画布，左上放一个 2x2 白块作为可追踪的标记
var baseGeometry = TransformGeometry.ForBounds(0, 0, 8, 8);
Check("初始几何为原样", baseGeometry.IsIdentity && baseGeometry.CenterX == 4 && baseGeometry.CenterY == 4,
    $"{baseGeometry.CenterX},{baseGeometry.CenterY}");

using (var canvas = BlockImage(8, 8, new Rectangle(2, 2, 2, 2)))
using (var same = TransformOps.Warp(canvas, baseGeometry))
{
    Check("恒等变换保持像素", same[2, 2].A == 255 && same[3, 3].A == 255 && same[0, 0].A == 0);
    Check("恒等变换后包围盒不变", TransformOps.ContentBounds(same) == new Rectangle(2, 2, 2, 2),
        TransformOps.ContentBounds(same).ToString());
}

using (var canvas = BlockImage(8, 8, new Rectangle(2, 2, 2, 2)))
{
    var moved = TransformMath.Translate(baseGeometry, new Vector2(2, 0));
    using var warped = TransformOps.Warp(canvas, moved);
    Check("平移后内容到位", warped[4, 2].A == 255 && warped[5, 3].A == 255);
    Check("平移后原位清空", warped[2, 2].A == 0 && warped[3, 3].A == 0);
}

using (var canvas = BlockImage(8, 8, new Rectangle(2, 2, 2, 2)))
{
    var scaled = baseGeometry;
    scaled.ScaleX = 2;
    scaled.ScaleY = 2;
    using var warped = TransformOps.Warp(canvas, scaled);
    var grown = TransformOps.ContentBounds(warped);
    Check("放大 2 倍后覆盖更大区域", warped[1, 1].A == 255 && grown.Width >= 4 && grown.Height >= 4,
        $"{warped[1, 1].A}/{grown}");
    Check("放大后区域之外仍透明", warped[6, 6].A == 0 && warped[7, 7].A == 0,
        $"{warped[6, 6].A}/{warped[7, 7].A}");
}

using (var canvas = BlockImage(8, 8, new Rectangle(2, 2, 2, 2)))
{
    var quarterTurn = baseGeometry;
    quarterTurn.Rotation = Math.PI / 2;
    using var warped = TransformOps.Warp(canvas, quarterTurn);
    Check("旋转 90° 后位置正确", warped[4, 2].A == 255 && warped[5, 3].A == 255,
        $"{warped[4, 2].A}/{warped[5, 3].A}");
    Check("旋转后原位清空", warped[2, 2].A == 0);
}

using (var canvas = BlockImage(8, 8, new Rectangle(2, 2, 2, 2)))
{
    var halfTurn = baseGeometry;
    halfTurn.Rotation = Math.PI;
    using var warped = TransformOps.Warp(canvas, halfTurn);
    Check("旋转 180° 后关于中心对称", warped[4, 4].A == 255 && warped[5, 5].A == 255 && warped[2, 2].A == 0,
        $"{warped[4, 4].A}/{warped[5, 5].A}/{warped[2, 2].A}");
}

// 单个白点放大后，所有非零 alpha 的像素都应该仍是纯白（预乘采样，无黑边）
using (var dot = SparseImage(16, 16, (8, 8, (byte)255)))
{
    var scaled = TransformGeometry.ForBounds(0, 0, 16, 16);
    scaled.ScaleX = 3;
    scaled.ScaleY = 3;
    using var warped = TransformOps.Warp(dot, scaled);

    var dark = false;
    var partial = false;

    for (var y = 0; y < warped.Height; y++)
    for (var x = 0; x < warped.Width; x++)
    {
        var pixel = warped[x, y];
        if (pixel.A == 0)
            continue;
        if (pixel.R != 255 || pixel.G != 255 || pixel.B != 255)
            dark = true;
        if (pixel.A < 250)
            partial = true;
    }

    Check("双线性缩放不产生暗边", !dark);
    Check("缩放边缘有半透明过渡", partial);
}

// 选区裁剪：只搬运选区内像素，选区外保持原样
using (var canvas = BlockImage(10, 10, new Rectangle(0, 0, 4, 4)))
{
    canvas[9, 9] = new Bgra32 { R = 200, G = 0, B = 0, A = 255 };
    var clip = Selection.FromRect(10, 10, new Rectangle(0, 0, 4, 4));
    using var warped = TransformOps.Warp(canvas, TransformMath.Translate(TransformGeometry.ForBounds(0, 0, 4, 4), new Vector2(5, 0)), clip);

    Check("选区内内容变换到新位置", warped[5, 0].A == 255 && warped[8, 3].A == 255);
    Check("选区原位清空", warped[0, 0].A == 0 && warped[3, 3].A == 0);
    Check("选区外像素不受影响", warped[9, 9].A == 255 && warped[9, 9].R == 200);
}

// 蒙版跟随内容
using (var transformDoc = new ImageDocument(BlockImage(10, 10, new Rectangle(0, 0, 4, 4))))
{
    var layer = transformDoc.ActiveLayer!;
    var mask = Layer.CreateMask(10, 10, 0);
    mask[1, 1] = new L8(180);
    layer.Mask = mask;

    var geometry = TransformMath.Translate(TransformGeometry.ForBounds(0, 0, 4, 4), new Vector2(5, 0));
    var done = transformDoc.TransformLayerContent(0, geometry, null, "自由变换");

    Check("文档变换返回成功", done);
    Check("变换后像素到位", layer.Pixels[5, 1].A == 255 && layer.Pixels[1, 1].A == 0);
    Check("蒙版跟随内容变换", layer.Mask![6, 1].PackedValue >= 175 && layer.Mask[1, 1].PackedValue == 0,
        $"{layer.Mask[6, 1].PackedValue}/{layer.Mask[1, 1].PackedValue}");
    Check("变换产生一条历史记录", transformDoc.CanUndo);

    transformDoc.Undo();
    var restored = transformDoc.ActiveLayer!;
    Check("撤销后内容复原", restored.Pixels[1, 1].A == 255 && restored.Pixels[5, 1].A == 0);

    Check("恒等变换不产生历史", !transformDoc.TransformLayerContent(0, TransformGeometry.ForBounds(0, 0, 4, 4), null, "自由变换"));
}

// 拖拽换算
Check("拖动角点到对角点等于不变", SameGeometry(
    TransformMath.Resize(baseGeometry, TransformHandle.TopLeft, new Vector2(0, 0), false, false),
    baseGeometry));

var halfScale = TransformMath.Resize(baseGeometry, TransformHandle.TopLeft, new Vector2(4, 4), false, false);
Check("把角点拖到中心得到 50%", Math.Abs(halfScale.ScaleX - 0.5) < 1e-6 && Math.Abs(halfScale.ScaleY - 0.5) < 1e-6,
    $"{halfScale.ScaleX:0.###}/{halfScale.ScaleY:0.###}");
Check("对角点保持不动", Math.Abs(halfScale.CenterX - 6) < 1e-6 && Math.Abs(halfScale.CenterY - 6) < 1e-6,
    $"{halfScale.CenterX}/{halfScale.CenterY}");

var uniform = TransformMath.Resize(baseGeometry, TransformHandle.TopLeft, new Vector2(0, 5), true, false);
Check("Shift 等比缩放", Math.Abs(uniform.ScaleX - uniform.ScaleY) < 1e-6 && Math.Abs(uniform.ScaleX - 1) < 1e-6,
    $"{uniform.ScaleX:0.###}/{uniform.ScaleY:0.###}");

var fromCenter = TransformMath.Resize(baseGeometry, TransformHandle.TopLeft, new Vector2(2, 2), false, true);
Check("Alt 围绕中心缩放不移动中心", Math.Abs(fromCenter.CenterX - 4) < 1e-6 && Math.Abs(fromCenter.CenterY - 4) < 1e-6,
    $"{fromCenter.CenterX}/{fromCenter.CenterY}");
Check("Alt 围绕中心缩放尺寸减半", Math.Abs(fromCenter.ScaleX - 0.5) < 1e-6, fromCenter.ScaleX.ToString("0.###"));

var edge = TransformMath.Resize(baseGeometry, TransformHandle.Right, new Vector2(16, 0), false, false);
Check("拖右边中点只改变宽度", Math.Abs(edge.ScaleX - 2) < 1e-6 && Math.Abs(edge.ScaleY - 1) < 1e-6,
    $"{edge.ScaleX:0.###}/{edge.ScaleY:0.###}");

var collapsed = TransformMath.Resize(baseGeometry, TransformHandle.TopLeft, new Vector2(10, 10), false, false);
Check("拖成零面积时夹到最小尺寸",
    Math.Abs(collapsed.ScaleX) > 0 && Math.Abs(collapsed.ScaleX * collapsed.HalfWidth) >= TransformGeometry.MinExtent - 1e-9,
    collapsed.ScaleX.ToString("0.####"));

var flipped = TransformMath.Resize(baseGeometry, TransformHandle.TopLeft, new Vector2(14, 14), false, false);
Check("拖过锚点得到镜像（负缩放）", flipped.ScaleX < 0 && flipped.ScaleY < 0, $"{flipped.ScaleX:0.###}");

var rotatedGeometry = TransformMath.RotateTo(baseGeometry, new Vector2(8, 4), new Vector2(4, 8), false);
Check("拖动旋转 90°", Math.Abs(rotatedGeometry.Rotation - Math.PI / 2) < 1e-6, rotatedGeometry.Rotation.ToString("0.####"));

var snapped = TransformMath.RotateTo(baseGeometry, new Vector2(8, 4),
    new Vector2((float)(4 + 8 * Math.Cos(92 * Math.PI / 180)), (float)(4 + 8 * Math.Sin(92 * Math.PI / 180))), true);
Check("Shift 旋转吸附到 15°", Math.Abs(snapped.Rotation - Math.PI / 2) < 1e-6, (snapped.Rotation * 180 / Math.PI).ToString("0.#"));

Check("恒等判断不会把平移误判为无事发生",
    !TransformMath.Translate(baseGeometry, new Vector2(1, 0)).IsIdentity);

using (var blank = new Image<Bgra32>(8, 8))
    Check("全透明图退回整幅画布", TransformOps.ContentBounds(blank) == new Rectangle(0, 0, 8, 8));

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

static bool SameGeometry(TransformGeometry a, TransformGeometry b) =>
    Math.Abs(a.CenterX - b.CenterX) < 1e-6 &&
    Math.Abs(a.CenterY - b.CenterY) < 1e-6 &&
    Math.Abs(a.ScaleX - b.ScaleX) < 1e-6 &&
    Math.Abs(a.ScaleY - b.ScaleY) < 1e-6 &&
    Math.Abs(a.Rotation - b.Rotation) < 1e-6;

static Image<Bgra32> BlockImage(int width, int height, Rectangle block)
{
    var img = new Image<Bgra32>(width, height);
    for (var y = block.Y; y < block.Bottom; y++)
    for (var x = block.X; x < block.Right; x++)
        img[x, y] = new Bgra32 { R = 255, G = 255, B = 255, A = 255 };
    return img;
}

static Image<Bgra32> SparseImage(int width, int height, params (int X, int Y, byte Alpha)[] pixels)
{
    var img = new Image<Bgra32>(width, height);
    foreach (var (x, y, alpha) in pixels)
        img[x, y] = new Bgra32 { R = 255, G = 255, B = 255, A = alpha };
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
