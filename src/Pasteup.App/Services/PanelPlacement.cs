namespace Pasteup.App.Services;

/// <summary>
/// 面板落位（纯函数，DIP 单位）：锚点优先取文本插入光标、取不到退回鼠标指针，两者共用同一套规则——
/// 下 → 上 → 右 → 左 → 贴边，间隙 10 DIP，永不跨屏、不压任务栏。
/// 方向定稿、caret 可用性实测与单测矩阵见 docs/ideas/panel-anchor-placement.md。
/// WPF 侧按目标屏 DPI 折成物理像素后调用（函数本身不关心单位，只要求输入同一单位），单测直接喂 DIP 数字。
/// </summary>
public static class PanelPlacement
{
    /// <summary>锚点与面板之间的间隙（DIP）。保证指针/当前编辑行不被面板压住。</summary>
    public const double Gap = 10.0;

    /// <summary>命中的规则；只有 <see cref="Fallback"/> 允许盖住锚点。</summary>
    public enum Rule
    {
        Below,
        Above,
        Right,
        Left,
        Fallback,
    }

    /// <summary>左上角 + 宽高（同一坐标单位）。鼠标锚点用 0×0 的退化矩形表示。</summary>
    public readonly record struct Rect(double X, double Y, double W, double H)
    {
        public double Right => X + W;
        public double Bottom => Y + H;
        public double CenterY => Y + H / 2.0;
    }

    public readonly record struct Result(Rect Panel, Rule Rule, bool CoversAnchor);

    /// <summary>
    /// 求面板左上角：下方（左上角对齐）→ 上方（左下角对齐）→ 右侧（竖直居中）→ 左侧（竖直居中）→
    /// 贴边兜底（允许与锚点重叠）。前四条要求整块落在工作区内，所以下/上不遮当前编辑行、
    /// 右/左不遮锚点；只有兜底可能盖住锚点。
    /// </summary>
    public static Result Solve(Rect anchor, Rect work, double panelW, double panelH, double gap = Gap)
    {
        // 面板比工作区还大时（WPF 侧由 MaxWidth/MaxHeight 保证不会发生，纯函数仍要退化得干净）：
        // 钳制区间先保证 min ≤ max，而不是把面板推到负坐标。
        var xMin = work.X;
        var xMax = Math.Max(xMin, work.Right - panelW);
        var yMin = work.Y;
        var yMax = Math.Max(yMin, work.Bottom - panelH);
        var x = Math.Clamp(anchor.X, xMin, xMax);
        var yCentered = Math.Clamp(anchor.CenterY - panelH / 2.0, yMin, yMax);

        var belowTop = anchor.Bottom + gap;
        if (belowTop + panelH <= work.Bottom)
            return new Result(new Rect(x, belowTop, panelW, panelH), Rule.Below, false);

        var aboveTop = anchor.Y - gap - panelH;
        if (aboveTop >= work.Y)
            return new Result(new Rect(x, aboveTop, panelW, panelH), Rule.Above, false);

        var rightX = anchor.Right + gap;
        if (rightX + panelW <= work.Right)
            return new Result(new Rect(rightX, yCentered, panelW, panelH), Rule.Right, false);

        var leftX = anchor.X - gap - panelW;
        if (leftX >= work.X)
            return new Result(new Rect(leftX, yCentered, panelW, panelH), Rule.Left, false);

        var y = Math.Clamp(belowTop, yMin, yMax);
        var panel = new Rect(x, y, panelW, panelH);
        return new Result(panel, Rule.Fallback, Overlaps(panel, anchor));
    }

    /// <summary>两矩形是否相交；退化矩形（0×0 的鼠标锚点）按“点是否落在矩形内”处理。</summary>
    public static bool Overlaps(Rect a, Rect b) =>
        a.X < b.Right && a.Right > b.X && a.Y < b.Bottom && a.Bottom > b.Y;
}
