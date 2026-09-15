using Godot;
using System;
using System.Collections.Generic;
using System.IO;

namespace GodotFluid;

/// <summary>
/// Godot 液体演示入口。
///
/// 本类只负责“如何演示”液体系统：创建响应式 UI、切换工具、解释输入，并把不同
/// 工具统一翻译为 FluidManager 的 AddFluid/AddImpulse/SetObstacle 调用。
/// 正式项目可以丢弃本文件，只保留 FluidManager 作为简单稳定的 SDK 入口。
/// </summary>
[GlobalClass]
public sealed partial class Main : Node2D
{
    /// <summary>
    /// 画笔颜料的浓度半衰期，单位为秒。默认 3.15 秒；
    /// 它比雾和闪光保留更久，用于模拟较稳定的染料或颜料。
    /// </summary>
    [ExportGroup("演示工具 - 浓度半衰期")]
    [Export(PropertyHint.Range, "0,10,0.05,or_greater")]
    public float BrushHalfLifeSeconds { get; set; } = 3.15f;

    /// <summary>
    /// AK-47 命中闪光的浓度半衰期，单位为秒。默认 0.43 秒，
    /// 用于让短促的冲击快速消失，避免连续射击永久染色。
    /// </summary>
    [Export(PropertyHint.Range, "0,10,0.05,or_greater")]
    public float RifleHalfLifeSeconds { get; set; } = 0.43f;

    /// <summary>
    /// 灭火器悬浮雾的浓度半衰期，单位为秒。默认 0.73 秒；
    /// 调高会形成持续白雾，调低则更接近短距离喷射颗粒。
    /// </summary>
    [Export(PropertyHint.Range, "0,10,0.05,or_greater")]
    public float ExtinguisherHalfLifeSeconds { get; set; } = 0.73f;

    /// <summary>
    /// 烟雾弹烟雾的浓度半衰期，单位为秒。默认 2.48 秒；
    /// 烟雾会比灭火剂保留更久，并在工具逻辑中获得轻微向上初速度。
    /// </summary>
    [Export(PropertyHint.Range, "0,10,0.05,or_greater")]
    public float SmokeHalfLifeSeconds { get; set; } = 2.48f;

    /// <summary>
    /// 魔法信号弹外层暖色流体的浓度半衰期，单位为秒。
    /// 默认 0.99 秒；信号弹的黄色核心寿命会再缩短约 22%。
    /// </summary>
    [Export(PropertyHint.Range, "0,10,0.05,or_greater")]
    public float FlareHalfLifeSeconds { get; set; } = 0.99f;

    /// <summary>
    /// 火箭爆炸外层颜色的浓度半衰期，单位为秒。默认 0.63 秒；
    /// 爆炸黄色核心寿命会再缩短约 18%，以形成由亮到暗的消散过程。
    /// </summary>
    [Export(PropertyHint.Range, "0,10,0.05,or_greater")]
    public float ExplosionHalfLifeSeconds { get; set; } = 0.63f;

    /// <summary>原 Unity demo 中最能体现液体交互的六种工具。</summary>
    private enum DemoTool
    {
        Brush,
        Rifle,
        Extinguisher,
        SmokeGrenade,
        FlareLauncher,
        RocketLauncher
    }

    /// <summary>工具的展示元数据集中管理，避免在输入、UI 和效果代码中重复字符串。</summary>
    private sealed class ToolInfo
    {
        public readonly string Name;
        public readonly string Hint;
        public readonly string IconPath;

        public ToolInfo(string name, string hint, string iconPath)
        {
            Name = name;
            Hint = hint;
            IconPath = iconPath;
        }
    }

    private static readonly ToolInfo[] ToolInfos =
    {
        new("画笔", "拖动注入彩色染料，并把鼠标位移写入速度场", "res://Assets/Original/Inventory/brush.png"),
        new("AK-47", "持续射击，在命中点产生小范围冲击", "res://Assets/Original/Inventory/ak47.png"),
        new("灭火器", "持续喷出低速冷色流体", "res://Assets/Original/Inventory/FireExtinguisher.png"),
        new("烟雾弹", "单击释放带深色边缘的中性灰烟雾", "res://Assets/Original/Inventory/SmokeGranade.png"),
        new("魔法信号枪", "单击注入带方向的高亮暖色流体", "res://Assets/Original/Inventory/MagicFlareLauncher.png"),
        new("火箭筒", "单击制造大范围染料爆炸和径向速度", "res://Assets/Original/Inventory/rocketlauncher.png")
    };

    private static readonly string[] ToolSlugs =
    {
        "brush",
        "rifle",
        "extinguisher",
        "smoke-grenade",
        "flare-launcher",
        "rocket-launcher"
    };

    private FluidManager _fluid;
    private Label _status;
    private Label _selectedToolLabel;
    private readonly List<Button> _toolButtons = new();

    private DemoTool _selectedTool;
    private bool _leftButtonHeld;
    private bool _paintingObstacle;
    private Vector2? _lastPaintPosition;
    private float _continuousToolCooldown;
    private bool _captureMode;

    // 原项目工厂素材在这里作为背景参照使用；液体画布保持透明，可覆盖在它们之上。
    private Texture2D _wallTexture;
    private Texture2D _gridTexture;
    private Texture2D _crossTexture;

    public override void _Ready()
    {
        // Demo 自己也只通过 FluidManager 的简易 API 工作，确保 SDK 门面覆盖实际需求。
        _fluid = GetNode<FluidManager>("FluidManager");

        // README 动图由真实模拟代码无界面生成。普通启动不会进入此分支。
        if (TryCaptureShowcases())
            return;

        LoadFactoryAssets();
        CreateOverlay();
        SeedDemo();

        GetViewport().SizeChanged += OnViewportSizeChanged;
        SelectTool(0);
        QueueRedraw();
    }

    /// <summary>
    /// 通过 --capture-showcases=目录 生成六种工具的逐帧 PNG。该入口用于维护 README 动图，
    /// 直接调用与交互 Demo 相同的 UseSelectedTool 和 FluidSimulation，不依赖录屏权限。
    /// </summary>
    private bool TryCaptureShowcases()
    {
        const string prefix = "--capture-showcases=";
        foreach (string argument in OS.GetCmdlineUserArgs())
        {
            if (!argument.StartsWith(prefix, StringComparison.Ordinal))
                continue;

            _captureMode = true;
            string outputDirectory = argument[prefix.Length..].Trim();
            if (string.IsNullOrWhiteSpace(outputDirectory))
            {
                GD.PushError("--capture-showcases 需要提供输出目录。");
                GetTree().Quit(1);
                return true;
            }

            CaptureShowcases(Path.GetFullPath(outputDirectory));
            GetTree().Quit();
            return true;
        }

        return false;
    }

    private void CaptureShowcases(string outputDirectory)
    {
        const int frameCount = 60;
        const float fixedDelta = 1f / 20f;

        Directory.CreateDirectory(outputDirectory);
        _fluid.Pause();

        FluidSimulation simulation = _fluid.AdvancedSimulation;
        Image fluidFrame = Image.CreateEmpty(simulation.Width, simulation.Height, false, Image.Format.Rgba8);
        Image composedFrame = Image.CreateEmpty(simulation.Width, simulation.Height, false, Image.Format.Rgba8);

        for (int toolIndex = 0; toolIndex < ToolInfos.Length; toolIndex++)
        {
            string toolDirectory = Path.Combine(outputDirectory, ToolSlugs[toolIndex]);
            Directory.CreateDirectory(toolDirectory);

            _fluid.ClearAll();
            _fluid.SetObstacle(new Vector2(0.50f, 0.56f), 0.045f);
            _fluid.SetObstacle(new Vector2(0.30f, 0.42f), 0.022f);
            _fluid.SetObstacle(new Vector2(0.70f, 0.42f), 0.022f);
            SelectTool(toolIndex);

            Vector2 previousBrushPosition = new(0.14f, 0.52f);
            for (int frame = 0; frame < frameCount; frame++)
            {
                DriveShowcaseTool((DemoTool)toolIndex, frame, frameCount, ref previousBrushPosition);
                simulation.Step(fixedDelta);
                simulation.WriteImage(
                    fluidFrame,
                    _fluid.SmokeColor,
                    _fluid.SmokeEdgeColor,
                    _fluid.SmokeEdgeStrength,
                    _fluid.SmokeOpacity);

                ComposeShowcaseFrame(composedFrame, fluidFrame, toolIndex);
                string framePath = Path.Combine(toolDirectory, $"{frame:D3}.png");
                Error error = composedFrame.SavePng(framePath);
                if (error != Error.Ok)
                    throw new IOException($"无法保存展示帧 {framePath}: {error}");
            }
        }

        GD.Print($"Showcase frames written to {outputDirectory}");
    }

    private void DriveShowcaseTool(
        DemoTool tool,
        int frame,
        int frameCount,
        ref Vector2 previousBrushPosition)
    {
        float progress = frame / (float)(frameCount - 1);
        Vector2 normalizedPosition;

        switch (tool)
        {
            case DemoTool.Brush:
                if (frame < 3 || frame > 48)
                    return;
                float brushProgress = (frame - 3f) / 45f;
                normalizedPosition = new Vector2(
                    Mathf.Lerp(0.13f, 0.87f, brushProgress),
                    0.48f + Mathf.Sin(brushProgress * Mathf.Tau * 1.5f) * 0.19f);
                Vector2 brushVelocity = (normalizedPosition - previousBrushPosition) * 22f;
                previousBrushPosition = normalizedPosition;
                UseSelectedTool(ToScreenPosition(normalizedPosition), brushVelocity.LimitLength(3.2f));
                break;

            case DemoTool.Rifle:
                if (frame is >= 4 and <= 48 && frame % 2 == 0)
                {
                    normalizedPosition = new Vector2(
                        0.50f + Mathf.Sin(frame * 0.43f) * 0.24f,
                        0.36f + Mathf.Cos(frame * 0.31f) * 0.10f);
                    UseSelectedTool(ToScreenPosition(normalizedPosition), Vector2.Zero);
                }
                break;

            case DemoTool.Extinguisher:
                if (frame is >= 3 and <= 49)
                {
                    normalizedPosition = new Vector2(
                        0.50f + Mathf.Sin(progress * Mathf.Tau * 1.25f) * 0.22f,
                        Mathf.Lerp(0.66f, 0.27f, progress));
                    UseSelectedTool(ToScreenPosition(normalizedPosition), Vector2.Zero);
                }
                break;

            case DemoTool.SmokeGrenade:
                if (frame is 4 or 19 or 34)
                {
                    int burst = frame == 4 ? 0 : frame == 19 ? 1 : 2;
                    normalizedPosition = new Vector2(0.30f + burst * 0.20f, 0.58f - burst * 0.08f);
                    UseSelectedTool(ToScreenPosition(normalizedPosition), Vector2.Zero);
                }
                break;

            case DemoTool.FlareLauncher:
                if (frame is 5 or 18 or 31 or 44)
                {
                    int shot = (frame - 5) / 13;
                    normalizedPosition = new Vector2(0.25f + shot * 0.17f, 0.58f - shot * 0.08f);
                    UseSelectedTool(ToScreenPosition(normalizedPosition), Vector2.Zero);
                }
                break;

            case DemoTool.RocketLauncher:
                if (frame is 7 or 30)
                {
                    normalizedPosition = frame == 7
                        ? new Vector2(0.38f, 0.48f)
                        : new Vector2(0.65f, 0.42f);
                    UseSelectedTool(ToScreenPosition(normalizedPosition), Vector2.Zero);
                }
                break;
        }
    }

    private Vector2 ToScreenPosition(Vector2 normalizedPosition)
    {
        Vector2 viewportSize = GetViewportRect().Size;
        return new Vector2(normalizedPosition.X * viewportSize.X, normalizedPosition.Y * viewportSize.Y);
    }

    private void ComposeShowcaseFrame(Image output, Image fluidFrame, int toolIndex)
    {
        int width = output.GetWidth();
        int height = output.GetHeight();

        output.Fill(new Color("071225"));
        output.FillRect(new Rect2I(0, 0, width, Math.Max(5, height / 14)), new Color("182b42"));
        output.FillRect(new Rect2I(0, height * 7 / 10, width, height * 3 / 10), new Color("101d2d"));
        output.FillRect(new Rect2I(0, height * 7 / 10, width, 2), new Color("d09a28"));
        output.FillRect(new Rect2I(width * 7 / 50, height / 7, 3, height * 4 / 7), new Color("283d50"));
        output.FillRect(new Rect2I(width * 43 / 50, height / 7, 3, height * 4 / 7), new Color("283d50"));
        output.BlendRect(fluidFrame, new Rect2I(0, 0, width, height), Vector2I.Zero);

        Image icon = Image.LoadFromFile(ProjectSettings.GlobalizePath(ToolInfos[toolIndex].IconPath));
        if (!icon.IsEmpty())
        {
            icon.Resize(18, 18, Image.Interpolation.Nearest);
            output.FillRect(new Rect2I(4, 4, 24, 24), new Color(0.02f, 0.04f, 0.08f, 0.88f));
            output.BlendRect(icon, new Rect2I(0, 0, icon.GetWidth(), icon.GetHeight()), new Vector2I(7, 7));
        }
    }

    public override void _ExitTree()
    {
        if (!_captureMode && GetViewport() != null)
            GetViewport().SizeChanged -= OnViewportSizeChanged;
    }

    private void LoadFactoryAssets()
    {
        _wallTexture = GD.Load<Texture2D>("res://Assets/Original/TileMap/wall.png");
        _gridTexture = GD.Load<Texture2D>("res://Assets/Original/TileMap/wall2.png");
        _crossTexture = GD.Load<Texture2D>("res://Assets/Original/TileMap/wall3.png");
        TextureRepeat = CanvasItem.TextureRepeatEnum.Enabled;
        TextureFilter = CanvasItem.TextureFilterEnum.Nearest;
    }

    /// <summary>创建完全基于锚点的 HUD，窗口放大、缩小或最大化时不会裁掉工具栏。</summary>
    private void CreateOverlay()
    {
        var layer = new CanvasLayer { Name = "DemoUI", Layer = 10 };
        AddChild(layer);

        var root = new Control { Name = "ResponsiveRoot", MouseFilter = Control.MouseFilterEnum.Ignore };
        root.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        layer.AddChild(root);

        var statusPanel = new PanelContainer
        {
            OffsetLeft = 20,
            OffsetTop = 18,
            OffsetRight = 455,
            OffsetBottom = 142,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Modulate = new Color(0.04f, 0.08f, 0.15f, 0.94f)
        };
        root.AddChild(statusPanel);

        var statusBox = new VBoxContainer();
        statusBox.AddThemeConstantOverride("separation", 4);
        statusPanel.AddChild(statusBox);
        _status = new Label();
        _status.AddThemeColorOverride("font_color", new Color("d7efff"));
        _status.AddThemeFontSizeOverride("font_size", 18);
        statusBox.AddChild(_status);

        var help = new Label
        {
            Text = "左键使用工具 · 右键画障碍 · Shift+右键擦除\n滚轮 / Q E / 数字键切换 · R 清空 · Space 暂停"
        };
        help.AddThemeColorOverride("font_color", new Color("9cb3c9"));
        help.AddThemeFontSizeOverride("font_size", 15);
        statusBox.AddChild(help);

        // 直接展示原 Unity demo 自带的双语滚轮提示图片。
        var note = new TextureRect
        {
            Texture = GD.Load<Texture2D>("res://Assets/Original/Inventory/note.png"),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            AnchorLeft = 1f,
            AnchorRight = 1f,
            OffsetLeft = -540,
            OffsetTop = 18,
            OffsetRight = -20,
            OffsetBottom = 84
        };
        root.AddChild(note);

        var toolbar = new PanelContainer
        {
            AnchorLeft = 0.5f,
            AnchorRight = 0.5f,
            AnchorTop = 1f,
            AnchorBottom = 1f,
            OffsetLeft = -500,
            OffsetTop = -142,
            OffsetRight = 500,
            OffsetBottom = -18,
            Modulate = new Color(0.035f, 0.065f, 0.12f, 0.96f)
        };
        root.AddChild(toolbar);

        var toolbarColumn = new VBoxContainer();
        toolbarColumn.AddThemeConstantOverride("separation", 5);
        toolbar.AddChild(toolbarColumn);

        _selectedToolLabel = new Label { HorizontalAlignment = HorizontalAlignment.Center };
        _selectedToolLabel.AddThemeColorOverride("font_color", new Color("f4d58d"));
        _selectedToolLabel.AddThemeFontSizeOverride("font_size", 16);
        toolbarColumn.AddChild(_selectedToolLabel);

        var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        row.AddThemeConstantOverride("separation", 8);
        toolbarColumn.AddChild(row);

        var buttonGroup = new ButtonGroup();
        for (int i = 0; i < ToolInfos.Length; i++)
        {
            int capturedIndex = i;
            ToolInfo info = ToolInfos[i];
            var button = new Button
            {
                Text = $"{i + 1}  {info.Name}",
                Icon = GD.Load<Texture2D>(info.IconPath),
                ExpandIcon = false,
                ToggleMode = true,
                ButtonGroup = buttonGroup,
                CustomMinimumSize = new Vector2(150, 70),
                TooltipText = info.Hint
            };
            button.AddThemeFontSizeOverride("font_size", 15);
            button.Pressed += () => SelectTool(capturedIndex);
            row.AddChild(button);
            _toolButtons.Add(button);
        }
    }

    /// <summary>给首次打开场景的用户一个可立即观察的静态流场，不依赖自动重力。</summary>
    private void SeedDemo()
    {
        // 启动时直接展示独立灰烟，让用户无需操作就能确认“浅色中心 + 深色边缘”已经生效。
        _fluid.AddSmoke(new Vector2(0.50f, 0.24f), 0.10f, new Vector2(0.05f, -0.10f), 3.2f, 0.90f);
        _fluid.AddFluid(new Vector2(0.31f, 0.54f), new Color("ef476f"), 0.13f, new Vector2(0.85f, -0.15f), 2.04f);
        _fluid.AddFluid(new Vector2(0.69f, 0.54f), new Color("1fb7d4"), 0.13f, new Vector2(-0.85f, 0.15f), 2.04f);
        _fluid.AddFluid(new Vector2(0.50f, 0.34f), new Color("ffd166"), 0.075f, new Vector2(0.05f, 0.7f), 1.65f);
        _fluid.SetObstacle(new Vector2(0.5f, 0.56f), 0.065f);
        _fluid.SetObstacle(new Vector2(0.38f, 0.42f), 0.034f);
        _fluid.SetObstacle(new Vector2(0.62f, 0.42f), 0.034f);
    }

    public override void _Process(double delta)
    {
        if (_captureMode)
            return;

        float dt = (float)delta;
        // 鼠标在 UI 上方释放时事件可能被按钮消费；轮询真实按键状态避免连续工具卡住。
        if (_leftButtonHeld && !Input.IsMouseButtonPressed(MouseButton.Left))
        {
            _leftButtonHeld = false;
            _lastPaintPosition = null;
        }
        if (_paintingObstacle && !Input.IsMouseButtonPressed(MouseButton.Right))
            _paintingObstacle = false;

        // AK 与灭火器属于“按住连续使用”的工具；即使鼠标不移动也要持续发射。
        _continuousToolCooldown -= dt;
        if (_leftButtonHeld && IsContinuousTool(_selectedTool) && _continuousToolCooldown <= 0f)
        {
            _continuousToolCooldown = _selectedTool == DemoTool.Rifle ? 0.065f : 0.035f;
            UseSelectedTool(GetViewport().GetMousePosition(), Vector2.Zero);
        }

        ToolInfo selected = ToolInfos[(int)_selectedTool];
        _status.Text = $"液体场 {_fluid.Resolution.X} × {_fluid.Resolution.Y}  ·  活跃单元 {_fluid.ActiveCellCount:N0}\n" +
                       $"状态：{(_fluid.IsRunning ? "运行中" : "已暂停")}  ·  当前工具：{selected.Name}\n" +
                       $"压力 {_fluid.PressureStrength:F2}  ·  平流 {_fluid.AdvectionScale:F2}  ·  默认半衰期 {_fluid.DefaultFluidHalfLifeSeconds:F2}s";
    }

    /// <summary>工厂背景使用原砖墙/网格素材，液体以透明纹理覆盖在它上方。</summary>
    public override void _Draw()
    {
        Vector2 size = GetViewportRect().Size;
        DrawRect(new Rect2(Vector2.Zero, size), new Color("071225"));

        // 顶部金属网格与底部砖墙提供空间参照，便于看清液体移动和透明度。
        if (_gridTexture != null)
            DrawTextureRect(_gridTexture, new Rect2(0, 0, size.X, 70), true, new Color(0.42f, 0.52f, 0.66f, 0.42f));
        if (_wallTexture != null)
            DrawTextureRect(_wallTexture, new Rect2(0, size.Y * 0.72f, size.X, size.Y * 0.28f), true, new Color(0.32f, 0.39f, 0.48f, 0.58f));

        // 低成本的管线/警示条用于补足场景构图，不引入 TileMap 与碰撞资源。
        Color pipe = new Color(0.18f, 0.25f, 0.34f, 0.85f);
        DrawLine(new Vector2(0, 112), new Vector2(size.X, 112), pipe, 18f);
        DrawLine(new Vector2(size.X * 0.14f, 112), new Vector2(size.X * 0.14f, size.Y * 0.70f), pipe, 14f);
        DrawLine(new Vector2(size.X * 0.86f, 112), new Vector2(size.X * 0.86f, size.Y * 0.70f), pipe, 14f);
        DrawLine(new Vector2(0, size.Y * 0.70f), new Vector2(size.X, size.Y * 0.70f), new Color("d09a28"), 5f);

        if (_crossTexture != null)
        {
            DrawTextureRect(_crossTexture, new Rect2(18, size.Y * 0.70f - 32, 64, 64), true, new Color(0.74f, 0.79f, 0.86f, 0.65f));
            DrawTextureRect(_crossTexture, new Rect2(size.X - 82, size.Y * 0.70f - 32, 64, 64), true, new Color(0.74f, 0.79f, 0.86f, 0.65f));
        }
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventKey key && key.Pressed && !key.Echo)
        {
            if (key.Keycode == Key.R)
            {
                _fluid.ClearAll();
            }
            else if (key.Keycode == Key.Space)
            {
                _fluid.TogglePaused();
            }
            else if (key.Keycode == Key.Q)
            {
                SelectRelativeTool(-1);
            }
            else if (key.Keycode == Key.E)
            {
                SelectRelativeTool(1);
            }
            else if (key.Keycode >= Key.Key1 && key.Keycode <= Key.Key6)
            {
                SelectTool((int)(key.Keycode - Key.Key1));
            }
        }

        if (@event is not InputEventMouseButton button)
        {
            if (@event is InputEventMouseMotion motion)
                HandleMouseMotion(motion);
            return;
        }

        if (button.ButtonIndex == MouseButton.WheelUp && button.Pressed)
        {
            SelectRelativeTool(1);
            return;
        }
        if (button.ButtonIndex == MouseButton.WheelDown && button.Pressed)
        {
            SelectRelativeTool(-1);
            return;
        }

        if (button.ButtonIndex == MouseButton.Left)
        {
            _leftButtonHeld = button.Pressed;
            if (button.Pressed)
            {
                _lastPaintPosition = button.Position;
                UseSelectedTool(button.Position, Vector2.Zero);
            }
            else
            {
                _lastPaintPosition = null;
            }
        }
        else if (button.ButtonIndex == MouseButton.Right)
        {
            _paintingObstacle = button.Pressed;
            if (button.Pressed)
                PaintObstacle(button.Position, Input.IsKeyPressed(Key.Shift));
        }
        else if (button.ButtonIndex == MouseButton.Middle && button.Pressed)
        {
            Vector2 normalized = _fluid.ViewportToFluid(button.Position);
            _fluid.AddFluid(normalized, Colors.White, 0.045f, Vector2.Zero, 1.07f);
            _fluid.AddImpulse(normalized, 0.075f, 1.8f);
        }
    }

    private void HandleMouseMotion(InputEventMouseMotion motion)
    {
        if (_paintingObstacle)
            PaintObstacle(motion.Position, Input.IsKeyPressed(Key.Shift));

        if (!_leftButtonHeld || _selectedTool != DemoTool.Brush)
            return;

        Vector2 dragVelocity = Vector2.Zero;
        if (_lastPaintPosition.HasValue)
        {
            Vector2 viewport = GetViewportRect().Size;
            Vector2 movement = motion.Position - _lastPaintPosition.Value;
            dragVelocity = new Vector2(movement.X / viewport.X, movement.Y / viewport.Y) * 34f;
            dragVelocity = dragVelocity.LimitLength(3.2f);
        }
        _lastPaintPosition = motion.Position;
        UseSelectedTool(motion.Position, dragVelocity);
    }

    private void UseSelectedTool(Vector2 screenPosition, Vector2 dragVelocity)
    {
        Vector2 normalized = _fluid.ViewportToFluid(screenPosition);
        Vector2 aimDirection = normalized - new Vector2(0.5f, 0.86f);
        aimDirection = aimDirection.LengthSquared() > 0.0001f ? aimDirection.Normalized() : Vector2.Up;

        switch (_selectedTool)
        {
            case DemoTool.Brush:
                float hue = Mathf.PosMod(normalized.X * 0.65f + Time.GetTicksMsec() * 0.00008f, 1f);
                // 颜料半衰期约 3.15 秒：比雾与爆炸持久，但不会永久留在画面上。
                _fluid.AddFluid(normalized, Color.FromHsv(hue, 0.88f, 1f), 0.032f, dragVelocity, BrushHalfLifeSeconds);
                break;

            case DemoTool.Rifle:
                // 枪口/命中闪光只短暂停留，半衰期约 0.43 秒。
                _fluid.AddFluid(normalized, new Color("ffd166"), 0.012f, aimDirection * 1.5f, RifleHalfLifeSeconds);
                _fluid.AddImpulse(normalized, 0.025f, 0.85f);
                break;

            case DemoTool.Extinguisher:
                float wobble = Mathf.Sin(Time.GetTicksMsec() * 0.018f) * 0.008f;
                Vector2 sprayPosition = normalized + new Vector2(-aimDirection.Y, aimDirection.X) * wobble;
                // 灭火剂是快速消散的悬浮雾，半衰期约 0.73 秒。
                _fluid.AddFluid(sprayPosition, new Color(0.72f, 0.92f, 1f, 1f), 0.027f, aimDirection * 1.0f, ExtinguisherHalfLifeSeconds);
                break;

            case DemoTool.SmokeGrenade:
                // 独立烟雾通道不会与彩色染料串色；中心浅灰、边缘深灰由 FluidManager 实时控制。
                Vector2 smokeDrift = new Vector2(aimDirection.X * 0.08f, -0.18f);
                _fluid.AddSmoke(normalized, 0.095f, smokeDrift, SmokeHalfLifeSeconds, 1f);
                _fluid.AddImpulse(normalized, 0.13f, 0.45f);
                break;

            case DemoTool.FlareLauncher:
                _fluid.AddFluid(normalized, new Color("ff5d2e"), 0.058f, aimDirection * 1.15f, FlareHalfLifeSeconds);
                _fluid.AddFluid(normalized, new Color("ffd166"), 0.022f, aimDirection * 1.35f, FlareHalfLifeSeconds * 0.78f);
                _fluid.AddImpulse(normalized, 0.075f, 0.7f);
                break;

            case DemoTool.RocketLauncher:
                // 爆炸扩张明显但亮色衰减很快，避免整屏长期残留高浓度颜色。
                _fluid.AddFluid(normalized, new Color("ef476f"), 0.115f, Vector2.Zero, ExplosionHalfLifeSeconds);
                _fluid.AddFluid(normalized, new Color("ffb703"), 0.060f, Vector2.Zero, ExplosionHalfLifeSeconds * 0.82f);
                _fluid.AddImpulse(normalized, 0.145f, 2.7f);
                break;
        }
    }

    private void PaintObstacle(Vector2 screenPosition, bool erase)
    {
        _fluid.SetObstacle(_fluid.ViewportToFluid(screenPosition), 0.026f, !erase);
    }

    private static bool IsContinuousTool(DemoTool tool)
        => tool == DemoTool.Rifle || tool == DemoTool.Extinguisher;

    private void SelectRelativeTool(int direction)
    {
        int count = ToolInfos.Length;
        SelectTool(((int)_selectedTool + direction + count) % count);
    }

    private void SelectTool(int index)
    {
        index = Mathf.Clamp(index, 0, ToolInfos.Length - 1);
        _selectedTool = (DemoTool)index;
        for (int i = 0; i < _toolButtons.Count; i++)
            _toolButtons[i].ButtonPressed = i == index;

        ToolInfo info = ToolInfos[index];
        if (_selectedToolLabel != null)
            _selectedToolLabel.Text = $"当前：{info.Name}  —  {info.Hint}";
    }

    private void OnViewportSizeChanged()
    {
        QueueRedraw();
        _fluid?.RequestRedraw();
    }
}
