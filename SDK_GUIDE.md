# FluidManager 新手快速入门

`FluidManager` 是本项目推荐的唯一日常入口。它自动创建模拟网格、推进时间、更新纹理并处理暂停状态。刚接触 Godot/C# 时，不需要直接操作 `FluidSimulation` 或 `FluidCanvas`。

## 1. 放进自己的场景

复制以下三个文件到目标 Godot 4.6+ .NET 项目：

- `Scripts/FluidManager.cs`
- `Scripts/FluidSimulation.cs`
- `Scripts/FluidCanvas.cs`

最简单的方式是直接把项目根目录的 `FluidManager.tscn` 拖入自己的场景。也可以手动添加一个 `Node2D`，命名为 `FluidManager`，然后挂载 `FluidManager.cs`。液体默认覆盖整个 Viewport，空白区域完全透明，可以放在 TileMap 或其他游戏画面上方。

## 2. 获取管理器

如果业务节点与管理器是同级节点，可以这样获取：

```csharp
private FluidManager _fluid;

public override void _Ready()
{
    _fluid = GetNode<FluidManager>("../FluidManager");
}
```

也可以将 `FluidManager` 作为导出节点引用，直接在 Inspector 中拖入：

```csharp
/// <summary>场景中的液体管理器，请在 Inspector 中拖入。</summary>
[Export]
public FluidManager Fluid { get; set; }
```

## 3. 坐标规则

所有常用效果使用归一化坐标：

- 左上角：`new Vector2(0, 0)`
- 画面中心：`new Vector2(0.5f, 0.5f)`
- 右下角：`new Vector2(1, 1)`

鼠标和触摸事件提供的是像素坐标，请先转换：

```csharp
Vector2 fluidPosition = _fluid.ViewportToFluid(GetViewport().GetMousePosition());
```

## 4. 常见操作

### 添加一团液体

```csharp
// 半径约占画面宽高的 4%，没有初速度，浓度半衰期为 2 秒。
_fluid.AddFluid(
    new Vector2(0.5f, 0.5f),
    Colors.DodgerBlue,
    radius: 0.04f,
    velocity: Vector2.Zero,
    halfLifeSeconds: 2f);
```

`halfLifeSeconds` 的规则：

- 负数：使用 Inspector 中的默认半衰期。
- `0`：不会自动衰减。
- 正数：经过该秒数后浓度剩余一半。

### 添加灰色烟雾

烟雾使用独立单通道浓度场，不会被普通 RGB 液体染成随机彩色：

```csharp
_fluid.AddSmoke(
    new Vector2(0.5f, 0.5f),
    radius: 0.09f,
    velocity: new Vector2(0f, -0.18f), // Godot 中负 Y 表示向上
    halfLifeSeconds: 2.5f,
    amount: 1f);
```

以下属性可以在 Inspector 或运行时代码中修改，并会立即作用于已经存在的烟雾：

```csharp
_fluid.SmokeColor = new Color("b7b9bb");
_fluid.SmokeEdgeColor = new Color("34383c");
_fluid.SmokeEdgeStrength = 0.72f;
_fluid.SmokeOpacity = 1.15f;

// 也可以一次设置；省略边缘色时 SDK 会自动生成更深的同色系。
_fluid.SetSmokeAppearance(
    centerColor: new Color("9ecca4"),
    edgeColor: new Color("264f30"),
    edgeStrength: 0.8f,
    opacity: 1.25f);
```

深色边缘来自烟雾浓度和四邻域梯度，不需要额外 Shader 或模糊纹理；关闭该效果时将
`Smoke Edge Strength` 设为 `0`。

### 鼠标拖动画液体

```csharp
public override void _UnhandledInput(InputEvent inputEvent)
{
    if (inputEvent is not InputEventMouseMotion motion) return;
    if (!Input.IsMouseButtonPressed(MouseButton.Left)) return;

    Vector2 position = _fluid.ViewportToFluid(motion.Position);
    Vector2 velocity = motion.Relative.LimitLength(3f) * 0.08f;
    _fluid.AddFluid(position, Colors.Cyan, 0.03f, velocity, 3f);
}
```

### 爆炸或冲击

```csharp
Vector2 position = new Vector2(0.5f, 0.5f);
_fluid.AddFluid(position, Colors.OrangeRed, 0.10f, halfLifeSeconds: 0.7f);
_fluid.AddImpulse(position, radius: 0.14f, strength: 2.5f);
```

`strength` 为正时向外推，为负时向中心吸。普通命中建议 `0.3～1.0`，爆炸建议 `1～4`。

### 绘制和擦除障碍

```csharp
Vector2 position = _fluid.ViewportToFluid(GetViewport().GetMousePosition());

_fluid.SetObstacle(position, radius: 0.03f, solid: true);  // 绘制
_fluid.SetObstacle(position, radius: 0.03f, solid: false); // 擦除
```

### 暂停、恢复和清空

```csharp
_fluid.Pause();          // 暂停流动，但仍可添加内容
_fluid.Resume();         // 恢复
_fluid.TogglePaused();   // 切换状态

_fluid.ClearFluid();     // 清液体，保留障碍
_fluid.ClearObstacles(); // 清障碍，保留液体
_fluid.ClearAll();       // 全部清空
```

## 5. Inspector 参数如何选择

建议先只调整下面四项：

| 想要的结果 | 调整方式 |
| --- | --- |
| 整体移动太快 | 降低 `Advection Scale` |
| 向四周铺开太快 | 降低 `Pressure Strength` |
| 流动过于碎乱 | 提高 `Viscosity` |
| 停止施力后动太久 | 降低 `Velocity Half Life Seconds` |
| 颜色消失太快 | 提高 `Default Fluid Half Life Seconds` |
| 烟雾边缘太深 | 降低 `Smoke Edge Strength` |
| 烟雾太厚/太薄 | 调整 `Smoke Opacity` 或 `AddSmoke` 的 `amount` |

分辨率推荐：

- 轻量移动端：`128 × 72`
- 默认桌面 Demo：`192 × 108`
- 更高细节：`320 × 180`

网格数量增加一倍，CPU 和纹理上传成本也大致增加一倍。修改分辨率后需要重新进入场景。

## 6. 什么时候访问底层

以下需求才建议使用 `AdvancedSimulation` 或 `AdvancedCanvas`：

- 自己控制固定时间步或暂停策略。
- 使用 SubViewport、世界坐标域或多个液体区域。
- 自定义颜色映射、Shader 或渲染目标。
- 直接读取网格、实现新的边界条件或特殊力场。

如果只是画液体、制造爆炸、添加障碍、暂停或清空，请始终优先使用 `FluidManager`。这样后续替换底层算法时，业务代码不需要一起修改。

## 7. 安全约定

SDK 会防御 NaN、Infinity、越界坐标、负半径和过大速度，避免坏输入传播成数组越界。但仍建议业务层遵守：

- 坐标尽量保持在 `0～1`。
- 普通速度保持在 `-3～3`。
- 普通半径保持在 `0.01～0.20`。
- 不要在 `FluidManager` 自动运行时再次手动调用底层 `Step()`。
