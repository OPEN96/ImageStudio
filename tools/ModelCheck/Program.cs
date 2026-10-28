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
