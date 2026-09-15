using Godot;

namespace GodotFluid;

/// <summary>
/// 面向 Godot 新手的液体系统统一入口。
///
/// 最简单的使用方式：
/// 1. 在场景中添加一个 Node2D，并挂载 FluidManager.cs。
/// 2. 从其他脚本通过 GetNode&lt;FluidManager&gt;() 获取它。
/// 3. 把鼠标位置转换为 0～1 坐标，然后调用 AddFluid：
/// <code>
/// FluidManager fluid = GetNode&lt;FluidManager&gt;("FluidManager");
/// Vector2 position = fluid.ViewportToFluid(GetViewport().GetMousePosition());
/// fluid.AddFluid(position, Colors.DodgerBlue);
/// </code>
///
/// 常见项目只需要本类公开的添加液体、冲击、障碍和清理接口。
/// 需要自定义数值算法或渲染管线时，再通过 AdvancedSimulation/AdvancedCanvas 访问底层模块。
/// </summary>
[GlobalClass]
public sealed partial class FluidManager : Node2D
{
    private const float NaturalLogOfTwo = 0.69314718056f;

    /// <summary>
    /// 模拟网格宽度。数值越大细节越清晰，但 CPU、内存和纹理更新成本会线性增加。
    /// 该值只在节点初始化时读取，运行中修改需要重新进入场景才会生效。
    /// </summary>
    [ExportGroup("基础设置")]
    [Export(PropertyHint.Range, "16,512,1")]
    public int GridWidth { get; set; } = 192;

    /// <summary>
    /// 模拟网格高度。通常应与游戏视口保持相同宽高比，例如 192×108 或 320×180。
    /// 该值只在节点初始化时读取，运行中修改需要重新进入场景才会生效。
    /// </summary>
    [Export(PropertyHint.Range, "16,512,1")]
    public int GridHeight { get; set; } = 108;

    /// <summary>
    /// 节点进入场景后是否自动运行模拟。关闭后仍然可以添加和显示液体，
    /// 但必须调用 Resume() 或 SetRunning(true) 才会继续流动和衰减。
    /// </summary>
    [Export]
    public bool AutoRun { get; set; } = true;

    /// <summary>
    /// 密度差产生的扩张压力。越大，浓度高的区域越快向周围铺开；
    /// 推荐从 0.25～0.80 开始调，过大容易产生爆炸式扩散。
    /// </summary>
    [ExportGroup("运动参数")]
    [Export(PropertyHint.Range, "0,3,0.01,or_greater")]
    public float PressureStrength { get; set; } = 0.45f;

    /// <summary>
    /// 速度场换算成画面位移的比例。它是控制整体流速最直接的参数；
    /// 越大移动越快，越小则更像粘稠、缓慢流动的物质。
    /// </summary>
    [Export(PropertyHint.Range, "0,1,0.01")]
    public float AdvectionScale { get; set; } = 0.22f;

    /// <summary>
    /// 速度向周围平均值靠拢的速率，单位约为 1/秒。越大越粘稠、越平滑；
    /// 越小越容易保留尖锐冲击和细小旋涡。
    /// </summary>
    [Export(PropertyHint.Range, "0,8,0.05,or_greater")]
    public float Viscosity { get; set; } = 1.4f;

    /// <summary>
    /// 单个网格允许保存的最大速度。它用于保护数值稳定性；
    /// 普通效果推荐 2～4，爆炸需求可以提高，但不建议无限增大。
    /// </summary>
    [Export(PropertyHint.Range, "0.1,10,0.1,or_greater")]
    public float MaxVelocity { get; set; } = 3f;

    /// <summary>
    /// AddFluid() 未指定半衰期时采用的默认浓度半衰期，单位为秒。
    /// 经过一个半衰期后浓度剩余一半；0 表示永不自动衰减。
    /// </summary>
    [ExportGroup("寿命与衰减")]
    [Export(PropertyHint.Range, "0,30,0.05,or_greater")]
    public float DefaultFluidHalfLifeSeconds { get; set; } = 1.82f;

    /// <summary>
    /// 速度场的半衰期，单位为秒。数值越小，停止施力后越快静止；
    /// 数值越大惯性越明显。0 表示不自动衰减速度。
    /// </summary>
    [Export(PropertyHint.Range, "0,30,0.05,or_greater")]
    public float VelocityHalfLifeSeconds { get; set; } = 0.92f;

    /// <summary>
    /// 整个模拟域的 Y 方向恒定外力。Godot 屏幕坐标向下为正，
    /// 因此正值使液体下落，负值使液体上升；匹配原 demo 时保持 0。
    /// </summary>
    [ExportGroup("可选外力")]
    [Export(PropertyHint.Range, "-10,10,0.05")]
    public float Gravity { get; set; } = 0f;

    /// <summary>
    /// 与局部浓度成正比的向上浮力。正值使高浓度区域上浮，负值使其下沉；
    /// 普通彩色液体建议为 0，烟雾效果可尝试 0.1～1.0。
    /// </summary>
    [Export(PropertyHint.Range, "-5,5,0.05")]
    public float Buoyancy { get; set; } = 0f;

    /// <summary>
    /// 液体画布没有染料时显示的底色。默认透明，便于把 FluidManager 放在
    /// TileMap、Sprite2D 或其他游戏画面上方。
    /// </summary>
    [ExportGroup("显示")]
    [Export]
    public Color BackgroundColor { get; set; } = new Color(0f, 0f, 0f, 0f);

    /// <summary>
    /// 障碍物调试区域的显示色。它只影响可视化，不改变障碍碰撞和速度边界。
    /// </summary>
    [Export]
    public Color ObstacleColor { get; set; } = new Color("d9e3f0");

    /// <summary>
    /// 烟雾高浓度中心的颜色。它是实时显示参数：运行时赋值后，画面中已经存在的烟雾
    /// 也会一起换色，不需要清空或重新释放烟雾。默认是中性浅灰。
    /// </summary>
    [ExportGroup("烟雾外观（实时可调）")]
    [Export]
    public Color SmokeColor { get; set; } = new Color(0.68f, 0.70f, 0.72f, 1f);

    /// <summary>
    /// 烟雾稀薄轮廓的颜色。默认使用深灰，使边缘比高浓度中心更深，
    /// 用较低成本的四邻域梯度近似原 Unity Demo 的烟雾轮廓观感。
    /// </summary>
    [Export]
    public Color SmokeEdgeColor { get; set; } = new Color(0.20f, 0.22f, 0.24f, 1f);

    /// <summary>
    /// 深色边缘的混合强度。0 表示关闭，1 表示最明显；推荐范围 0.5～0.85。
    /// 可在 Remote Inspector 中边运行边调整。
    /// </summary>
    [Export(PropertyHint.Range, "0,1,0.01")]
    public float SmokeEdgeStrength { get; set; } = 0.72f;

    /// <summary>
    /// 烟雾整体不透明度倍率。1 为标准浓度；小于 1 更轻薄，大于 1 更厚重。
    /// 它只改变显示，不改变模拟中的烟雾浓度、运动或寿命。
    /// </summary>
    [Export(PropertyHint.Range, "0,4,0.01,or_greater")]
    public float SmokeOpacity { get; set; } = 1.15f;

    /// <summary>模拟是否已经初始化。节点完成 _Ready 后通常为 true。</summary>
    public bool IsInitialized => _simulation != null;

    /// <summary>当前是否会在每帧自动更新流动和衰减。</summary>
    public bool IsRunning { get; private set; }

    /// <summary>当前可见液体占用的网格数，适合用于调试面板，不代表真实粒子数量。</summary>
    public int ActiveCellCount => _simulation?.ActiveCellCount ?? 0;

    /// <summary>实际使用的模拟网格尺寸。</summary>
    public Vector2I Resolution => _simulation == null
        ? new Vector2I(GridWidth, GridHeight)
        : new Vector2I(_simulation.Width, _simulation.Height);

    /// <summary>
    /// 高级入口：直接访问纯 C# 数值模拟器。普通注入、冲击和障碍需求不应使用它；
    /// 仅在需要手动 Step、自定义网格读取或实现特殊算法时访问。
    /// </summary>
    public FluidSimulation AdvancedSimulation
    {
        get
        {
            EnsureInitialized();
            return _simulation;
        }
    }

    /// <summary>
    /// 高级入口：直接访问默认显示画布。仅在需要替换颜色映射或自定义绘制时使用。
    /// </summary>
    public FluidCanvas AdvancedCanvas
    {
        get
        {
            EnsureInitialized();
            return _canvas;
        }
    }

    private FluidSimulation _simulation;
    private FluidCanvas _canvas;

    public override void _Ready()
    {
        EnsureInitialized();
        IsRunning = AutoRun;
        GetViewport().SizeChanged += HandleViewportSizeChanged;
    }

    public override void _ExitTree()
    {
        if (GetViewport() != null)
            GetViewport().SizeChanged -= HandleViewportSizeChanged;
    }

    public override void _Process(double delta)
    {
        EnsureInitialized();
        SynchronizeSettings();
        if (!IsRunning) return;

        _simulation.Step((float)delta);
        _canvas.Refresh();
    }

    /// <summary>
    /// 添加一团液体。
    /// </summary>
    /// <param name="normalizedPosition">
    /// 模拟域归一化坐标：左上角为 (0,0)，右下角为 (1,1)。鼠标像素坐标可以先传给 ViewportToFluid()。
    /// </param>
    /// <param name="color">液体颜色。Alpha 当前不参与浓度计算，建议通过 RGB 表示浓度和颜色。</param>
    /// <param name="radius">归一化半径；0.04 大约覆盖画面宽高的 4%。</param>
    /// <param name="velocity">注入的初速度。零表示只添加颜色，常用范围约为 -3～3。</param>
    /// <param name="halfLifeSeconds">
    /// 浓度半衰期：负数使用 Inspector 默认值，0 表示永不衰减，正数表示浓度减半所需秒数。
    /// </param>
    public void AddFluid(
        Vector2 normalizedPosition,
        Color color,
        float radius = 0.04f,
        Vector2 velocity = default,
        float halfLifeSeconds = -1f)
    {
        EnsureInitialized();
        float decayRate = halfLifeSeconds < 0f
            ? HalfLifeToDecayRate(DefaultFluidHalfLifeSeconds)
            : HalfLifeToDecayRate(halfLifeSeconds);
        _simulation.AddInk(normalizedPosition, color, radius, velocity, decayRate);
        RefreshAfterExternalChange();
    }

    /// <summary>
    /// 添加一团不会被彩色染料污染的独立烟雾。烟雾颜色由 SmokeColor、SmokeEdgeColor、
    /// SmokeEdgeStrength 和 SmokeOpacity 统一控制，这些属性可在运行时自由修改。
    /// </summary>
    /// <param name="normalizedPosition">0～1 的模拟域坐标；鼠标像素坐标可先调用 ViewportToFluid()。</param>
    /// <param name="radius">烟雾团归一化半径；0.08 表示约占画面宽高的 8%。</param>
    /// <param name="velocity">烟雾初速度；Godot 中负 Y 表示向上。</param>
    /// <param name="halfLifeSeconds">浓度半衰期；负数使用默认液体半衰期，0 表示不衰减。</param>
    /// <param name="amount">注入浓度倍率；1 为标准浓度，0.1～2 适合大多数效果。</param>
    public void AddSmoke(
        Vector2 normalizedPosition,
        float radius = 0.08f,
        Vector2 velocity = default,
        float halfLifeSeconds = -1f,
        float amount = 1f)
    {
        EnsureInitialized();
        float decayRate = halfLifeSeconds < 0f
            ? HalfLifeToDecayRate(DefaultFluidHalfLifeSeconds)
            : HalfLifeToDecayRate(halfLifeSeconds);
        _simulation.AddSmoke(normalizedPosition, radius, velocity, decayRate, amount);
        RefreshAfterExternalChange();
    }

    /// <summary>
    /// 在代码中实时修改烟雾外观。此方法会影响整个烟雾场（包括已经存在的烟雾），
    /// 适合昼夜变化、毒雾变色、阵营颜色或剧情过渡。边缘色不传时自动使用更深的同色系。
    /// </summary>
    /// <param name="centerColor">烟雾中心颜色。</param>
    /// <param name="edgeColor">可选边缘颜色；null 时由中心色自动变暗得到。</param>
    /// <param name="edgeStrength">可选边缘强度；负数表示保留当前值。</param>
    /// <param name="opacity">可选不透明度倍率；负数表示保留当前值。</param>
    public void SetSmokeAppearance(
        Color centerColor,
        Color? edgeColor = null,
        float edgeStrength = -1f,
        float opacity = -1f)
    {
        SmokeColor = centerColor;
        SmokeEdgeColor = edgeColor ?? centerColor.Darkened(0.62f);
        if (edgeStrength >= 0f)
            SmokeEdgeStrength = Mathf.Clamp(edgeStrength, 0f, 1f);
        if (opacity >= 0f)
            SmokeOpacity = Mathf.Max(0f, opacity);
        SynchronizeSettings();
    }

    /// <summary>
    /// 在指定归一化坐标产生径向冲击。正强度把流体向外推，负强度把流体吸向中心。
    /// 适用于爆炸、子弹命中、风洞或吸尘器等效果；它只修改速度，不添加颜色。
    /// </summary>
    /// <param name="normalizedPosition">0～1 的模拟域坐标。</param>
    /// <param name="radius">冲击作用的归一化半径。</param>
    /// <param name="strength">冲击强度，普通命中可用 0.3～1.0，爆炸可用 1～4。</param>
    public void AddImpulse(Vector2 normalizedPosition, float radius = 0.08f, float strength = 1f)
    {
        EnsureInitialized();
        _simulation.AddRadialImpulse(normalizedPosition, radius, strength);
        RefreshAfterExternalChange();
    }

    /// <summary>
    /// 绘制或擦除圆形障碍区域。障碍会清空自身网格中的液体和速度，并阻止流体直接穿过。
    /// </summary>
    /// <param name="normalizedPosition">0～1 的模拟域坐标。</param>
    /// <param name="radius">障碍的归一化半径。</param>
    /// <param name="solid">true 为绘制障碍，false 为擦除障碍。</param>
    public void SetObstacle(Vector2 normalizedPosition, float radius = 0.03f, bool solid = true)
    {
        EnsureInitialized();
        _simulation.SetObstacle(normalizedPosition, radius, solid);
        RefreshAfterExternalChange();
    }

    /// <summary>
    /// 将视口/鼠标像素坐标转换为 AddFluid、AddImpulse 和 SetObstacle 所需的 0～1 坐标。
    /// 超出窗口的坐标会自动限制在模拟边界内。
    /// </summary>
    public Vector2 ViewportToFluid(Vector2 viewportPosition)
    {
        Vector2 viewportSize = GetViewportRect().Size;
        if (viewportSize.X <= 0f || viewportSize.Y <= 0f)
            return Vector2.Zero;

        float x = float.IsFinite(viewportPosition.X) ? viewportPosition.X / viewportSize.X : 0f;
        float y = float.IsFinite(viewportPosition.Y) ? viewportPosition.Y / viewportSize.Y : 0f;
        return new Vector2(Mathf.Clamp(x, 0f, 1f), Mathf.Clamp(y, 0f, 1f));
    }

    /// <summary>清除全部颜色和速度，但保留当前障碍物布局。</summary>
    public void ClearFluid()
    {
        EnsureInitialized();
        _simulation.ClearFluid();
        _canvas.Refresh();
    }

    /// <summary>清除全部障碍物，但保留当前颜色和速度。</summary>
    public void ClearObstacles()
    {
        EnsureInitialized();
        _simulation.ClearObstacles();
        _canvas.Refresh();
    }

    /// <summary>清空液体、速度和障碍物，使模拟域恢复初始空白状态。</summary>
    public void ClearAll()
    {
        EnsureInitialized();
        _simulation.Clear();
        _canvas.Refresh();
    }

    /// <summary>暂停自动模拟。暂停期间仍可添加液体、冲击和障碍，并会立即显示。</summary>
    public void Pause() => SetRunning(false);

    /// <summary>继续自动模拟。</summary>
    public void Resume() => SetRunning(true);

    /// <summary>在暂停和运行状态之间切换，并返回切换后的运行状态。</summary>
    public bool TogglePaused()
    {
        SetRunning(!IsRunning);
        return IsRunning;
    }

    /// <summary>
    /// 显式设置是否自动运行。适合游戏暂停系统统一控制，不会清除当前模拟内容。
    /// </summary>
    public void SetRunning(bool running)
    {
        IsRunning = running;
    }

    /// <summary>要求默认画布在下一绘制帧重画，常用于窗口或宿主布局改变后。</summary>
    public void RequestRedraw()
    {
        EnsureInitialized();
        _canvas.QueueRedraw();
    }

    private void EnsureInitialized()
    {
        if (_simulation != null) return;

        _simulation = new FluidSimulation(GridWidth, GridHeight, new FluidSimulation.Settings());
        SynchronizeSettings();

        _canvas = new FluidCanvas
        {
            Name = "FluidCanvas",
            BackgroundColor = BackgroundColor,
            ObstacleColor = ObstacleColor,
            SmokeColor = SmokeColor,
            SmokeEdgeColor = SmokeEdgeColor,
            SmokeEdgeStrength = SmokeEdgeStrength,
            SmokeOpacity = SmokeOpacity
        };
        AddChild(_canvas);
        _canvas.SetSimulation(_simulation);
    }

    private void SynchronizeSettings()
    {
        if (_simulation == null) return;
        FluidSimulation.Settings settings = _simulation.Parameters;
        settings.Pressure = PressureStrength;
        settings.AdvectionScale = AdvectionScale;
        settings.Viscosity = Viscosity;
        settings.MaxVelocity = MaxVelocity;
        settings.DensityDecayPerSecond = HalfLifeToDecayRate(DefaultFluidHalfLifeSeconds);
        settings.VelocityDecayPerSecond = HalfLifeToDecayRate(VelocityHalfLifeSeconds);
        settings.Gravity = Gravity;
        settings.Buoyancy = Buoyancy;

        if (_canvas != null)
        {
            bool displayChanged = _canvas.BackgroundColor != BackgroundColor
                || _canvas.ObstacleColor != ObstacleColor
                || _canvas.SmokeColor != SmokeColor
                || _canvas.SmokeEdgeColor != SmokeEdgeColor
                || !Mathf.IsEqualApprox(_canvas.SmokeEdgeStrength, SmokeEdgeStrength)
                || !Mathf.IsEqualApprox(_canvas.SmokeOpacity, SmokeOpacity);
            _canvas.BackgroundColor = BackgroundColor;
            _canvas.ObstacleColor = ObstacleColor;
            _canvas.SmokeColor = SmokeColor;
            _canvas.SmokeEdgeColor = SmokeEdgeColor;
            _canvas.SmokeEdgeStrength = Mathf.Clamp(SmokeEdgeStrength, 0f, 1f);
            _canvas.SmokeOpacity = Mathf.Max(0f, SmokeOpacity);
            if (displayChanged)
                _canvas.Refresh();
        }
    }

    private void RefreshAfterExternalChange()
    {
        // 运行状态下下一帧会统一刷新；暂停时则立即刷新，保证编辑和调试反馈及时。
        if (!IsRunning)
            _canvas.Refresh();
    }

    private void HandleViewportSizeChanged() => _canvas?.QueueRedraw();

    private static float HalfLifeToDecayRate(float halfLifeSeconds)
    {
        if (!float.IsFinite(halfLifeSeconds)) return 0f;
        if (halfLifeSeconds <= 0f) return 0f;
        return NaturalLogOfTwo / halfLifeSeconds;
    }
}
