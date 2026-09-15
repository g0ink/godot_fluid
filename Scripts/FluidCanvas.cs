using Godot;

namespace GodotFluid;

/// <summary>
/// 液体显示组件。它不参与模拟，只把 ImageTexture 拉伸到当前视口，并绘制障碍物提示。
/// 将该节点放进其他场景即可复用，宿主只需调用 SetSimulation/Refresh。
/// </summary>
[GlobalClass]
public sealed partial class FluidCanvas : Node2D
{
    /// <summary>
    /// 液体画布自身的底色。默认完全透明，使宿主场景的 TileMap、Sprite 和背景
    /// 可以透过无液体区域显示；如果组件需要独立使用，可以将 Alpha 调高。
    /// </summary>
    [Export]
    public Color BackgroundColor { get; set; } = new Color(0f, 0f, 0f, 0f);

    /// <summary>
    /// 障碍网格在调试画布中的显示颜色。Alpha 会在绘制时统一调整为 0.62，
    /// 因此此处主要控制障碍物的 RGB 色调，不影响实际碰撞判定。
    /// </summary>
    [Export]
    public Color ObstacleColor { get; set; } = new Color("d9e3f0");

    /// <summary>
    /// 独立烟雾场高浓度中心的颜色。运行时修改后，已经存在的烟雾也会立即换色。
    /// 默认使用接近原 Unity Demo 的中性浅灰，而不是随机彩色。
    /// </summary>
    [ExportGroup("烟雾显示")]
    [Export]
    public Color SmokeColor { get; set; } = new Color(0.68f, 0.70f, 0.72f, 1f);

    /// <summary>
    /// 烟雾轮廓/稀薄区域的颜色。默认深于中心色，用来还原原 Demo 的深色烟雾边缘。
    /// </summary>
    [Export]
    public Color SmokeEdgeColor { get; set; } = new Color(0.20f, 0.22f, 0.24f, 1f);

    /// <summary>
    /// 深色边缘的混合强度。0 完全关闭边缘效果，1 使用完整边缘色；推荐 0.5～0.85。
    /// </summary>
    [Export(PropertyHint.Range, "0,1,0.01")]
    public float SmokeEdgeStrength { get; set; } = 0.72f;

    /// <summary>
    /// 烟雾整体不透明度倍率。1 为标准效果；减小会更轻薄，增大则更浓厚。
    /// </summary>
    [Export(PropertyHint.Range, "0,4,0.01,or_greater")]
    public float SmokeOpacity { get; set; } = 1.15f;

    private FluidSimulation _simulation;
    private Image _image;
    private ImageTexture _texture;

    /// <summary>
    /// 绑定需要显示的模拟器，并按模拟分辨率创建 Image 与 ImageTexture。
    /// 通常由 FluidManager 自动调用，普通业务脚本无需手动执行。
    /// </summary>
    /// <param name="simulation">非空的 FluidSimulation 实例。</param>
    public void SetSimulation(FluidSimulation simulation)
    {
        _simulation = simulation;
        // Godot 4.6 起 CreateEmpty 是创建可写空图像的明确 API，避免旧 Create 的弃用警告。
        _image = Image.CreateEmpty(simulation.Width, simulation.Height, false, Image.Format.Rgba8);
        _texture = ImageTexture.CreateFromImage(_image);
        TextureFilter = CanvasItem.TextureFilterEnum.Linear;
        Refresh();
    }

    /// <summary>
    /// 立即把当前模拟数组上传到显示纹理。FluidManager 运行时会每帧自动调用；
    /// 只有自主管理底层模拟或暂停状态下手动改数组时才需要直接调用。
    /// </summary>
    public void Refresh()
    {
        if (_simulation == null) return;
        _simulation.WriteImage(_image, SmokeColor, SmokeEdgeColor, SmokeEdgeStrength, SmokeOpacity);
        _texture.Update(_image);
        QueueRedraw();
    }

    public override void _Draw()
    {
        Vector2 size = GetViewportRect().Size;
        if (BackgroundColor.A > 0f)
            DrawRect(new Rect2(Vector2.Zero, size), BackgroundColor);
        if (_texture != null)
            DrawTextureRect(_texture, new Rect2(Vector2.Zero, size), false, Colors.White);

        // 障碍物以半透明圆点显示，帮助检查交互；实际碰撞仍由网格负责。
        if (_simulation != null)
        {
            float cellW = size.X / _simulation.Width;
            float cellH = size.Y / _simulation.Height;
            for (int y = 0; y < _simulation.Height; y++)
            {
                for (int x = 0; x < _simulation.Width; x++)
                {
                    if (_simulation.IsObstacle(x, y))
                    {
                        Color obstacleTint = new Color(ObstacleColor.R, ObstacleColor.G, ObstacleColor.B, 0.62f);
                        DrawRect(new Rect2(x * cellW, y * cellH, cellW + 0.5f, cellH + 0.5f), obstacleTint);
                    }
                }
            }
        }
    }
}
