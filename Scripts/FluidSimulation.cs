using Godot;
using System;

namespace GodotFluid;

/// <summary>
/// 低分辨率二维液体场。
///
/// Unity 原 demo 使用多个 RenderTexture 和 shader 做同样的事情。这里把核心
/// 算法改成固定大小的数组，优点是没有 GPU 临时纹理、CommandBuffer、ComputeBuffer
/// 等生命周期负担，逻辑也可以从 Node 中独立使用。渲染分辨率与窗口分离，默认
/// 160x90，足以产生连续流动的观感，同时每帧只处理约 1.4 万个网格单元。
/// </summary>
public sealed class FluidSimulation
{
    /// <summary>模拟网格宽度。它与窗口像素无关，构造完成后不可修改。</summary>
    public readonly int Width;

    /// <summary>模拟网格高度。它与窗口像素无关，构造完成后不可修改。</summary>
    public readonly int Height;

    /// <summary>可调参数集中在一个结构中，方便宿主项目序列化或运行时替换。</summary>
    public sealed class Settings
    {
        /// <summary>速度场向邻域平均的速率，单位约为 1/秒；越大越粘稠、细小旋涡越少。</summary>
        public float Viscosity = 1.4f;

        /// <summary>默认浓度指数衰减率，单位 1/秒；半衰期约等于 0.693 / 此值。</summary>
        public float DensityDecayPerSecond = 0.38f;

        /// <summary>速度指数衰减率，单位 1/秒；越大越快停下来。</summary>
        public float VelocityDecayPerSecond = 0.75f;
        /// <summary>
        /// 密度梯度产生的简化压力。Unity 原 shader 使用 densDiff 和 K 做类似计算，
        /// 它会让高密度区域自然向外扩散，而不是依靠全局重力移动。
        /// </summary>
        public float Pressure = 0.45f;

        /// <summary>速度换算为网格回溯距离时的比例；它直接影响画面中的整体流动速度。</summary>
        public float AdvectionScale = 0.22f;

        /// <summary>
        /// 可选的外力，默认均为零，以匹配原 demo 没有全局重力的行为。
        /// 宿主若要模拟烟雾上浮或液体下坠，可以按场景需要单独设置。
        /// </summary>
        /// <summary>Y 方向恒定外力；Godot 坐标中正数向下，默认 0。</summary>
        public float Gravity = 0f;

        /// <summary>按局部浓度施加的向上浮力；正数使高浓度区域上浮，默认 0。</summary>
        public float Buoyancy = 0f;

        /// <summary>速度绝对值上限，用于防止极端冲击令数值失稳。</summary>
        public float MaxVelocity = 3.0f;
    }

    /// <summary>当前使用的可变模拟参数。高级用户可以在运行时修改其中的标量。</summary>
    public Settings Parameters { get; }

    /// <summary>上一轮更新中浓度大于显示阈值的网格数量，可用于调试或性能面板。</summary>
    public int ActiveCellCount { get; private set; }

    // 染料的 RGB 三个通道拆开存储，避免每个网格单元都创建 Color 对象。
    private float[] _inkR, _inkG, _inkB;
    private float[] _nextInkR, _nextInkG, _nextInkB;
    // 存储“浓度 × 衰减率”而不是裸衰减率，避免与空白区域插值时把衰减率错误稀释。
    private float[] _decayMass, _nextDecayMass;
    // 烟雾使用独立的单通道浓度场，避免灰烟与普通 RGB 染料混合后变成随机彩色。
    // 单通道也比为烟雾再开三张 RGB 数组节省约 2/3 的烟雾存储空间。
    private float[] _smokeDensity, _nextSmokeDensity;
    private float[] _smokeDecayMass, _nextSmokeDecayMass;
    // 速度场同样拆成两个标量数组，便于缓存访问和独立调节阻尼。
    private float[] _velocityX, _velocityY;
    private float[] _nextVelocityX, _nextVelocityY;
    // 障碍场是只读于 Step 的布尔网格；绘制时直接修改，不生成 Godot PhysicsBody。
    private readonly bool[] _obstacle;

    /// <summary>
    /// 创建纯 C# 液体模拟器。通常建议新手通过 FluidManager 使用；只有需要自定义渲染、
    /// 多模拟域或手动更新时间步时，才需要直接构造本类。
    /// </summary>
    /// <param name="width">模拟网格宽度，最小会被限制为 16。</param>
    /// <param name="height">模拟网格高度，最小会被限制为 16。</param>
    /// <param name="settings">可选参数对象；传 null 时使用安全默认值。</param>
    public FluidSimulation(int width = 160, int height = 90, Settings settings = null)
    {
        Width = Math.Max(16, width);
        Height = Math.Max(16, height);
        Parameters = settings ?? new Settings();
        int length = Width * Height;
        _inkR = new float[length]; _inkG = new float[length]; _inkB = new float[length];
        _nextInkR = new float[length]; _nextInkG = new float[length]; _nextInkB = new float[length];
        _decayMass = new float[length]; _nextDecayMass = new float[length];
        _smokeDensity = new float[length]; _nextSmokeDensity = new float[length];
        _smokeDecayMass = new float[length]; _nextSmokeDecayMass = new float[length];
        _velocityX = new float[length]; _velocityY = new float[length];
        _nextVelocityX = new float[length]; _nextVelocityY = new float[length];
        _obstacle = new bool[length];
    }

    /// <summary>将归一化坐标（左上为 0,0）转换为数组坐标。</summary>
    private Vector2I ToCell(Vector2 normalized)
    {
        // 外部脚本可能传入 NaN/Infinity。坐标无效时回退到中心，绝不让非法值进入数组索引。
        float normalizedX = float.IsFinite(normalized.X) ? Mathf.Clamp(normalized.X, 0f, 1f) : 0.5f;
        float normalizedY = float.IsFinite(normalized.Y) ? Mathf.Clamp(normalized.Y, 0f, 1f) : 0.5f;
        int x = Mathf.Clamp(Mathf.FloorToInt(normalizedX * Width), 0, Width - 1);
        int y = Mathf.Clamp(Mathf.FloorToInt(normalizedY * Height), 0, Height - 1);
        return new Vector2I(x, y);
    }

    private int Index(int x, int y) => y * Width + x;

    /// <summary>
    /// 添加染料和初速度。颜色采用加法混合，多个刷子可以叠加而不需要队列或临时 RT。
    /// </summary>
    /// <param name="normalized">左上为 (0,0)、右下为 (1,1) 的归一化位置。</param>
    /// <param name="color">注入颜色；RGB 会写入浓度场，Alpha 当前不参与计算。</param>
    /// <param name="radius">相对于模拟域宽高的半径，负值会按绝对值处理。</param>
    /// <param name="velocity">随染料注入的初速度；非法或过大值会被安全限制。</param>
    /// <param name="decayPerSecond">浓度指数衰减率；负数表示使用 Settings 默认值。</param>
    public void AddInk(
        Vector2 normalized,
        Color color,
        float radius = 0.08f,
        Vector2 velocity = default,
        float decayPerSecond = -1f)
    {
        float defaultDecay = FiniteOr(Parameters.DensityDecayPerSecond, 0.38f);
        float requestedDecay = decayPerSecond >= 0f ? decayPerSecond : defaultDecay;
        requestedDecay = Mathf.Clamp(FiniteOr(requestedDecay, defaultDecay), 0f, 100f);
        float maxVelocity = Mathf.Max(0.01f, FiniteOr(Parameters.MaxVelocity, 3f));
        float inputVelocityX = FiniteOr(velocity.X, 0f);
        float inputVelocityY = FiniteOr(velocity.Y, 0f);
        PaintCircle(normalized, radius, (index, falloff) =>
        {
            float previousDensity = _inkR[index] + _inkG[index] + _inkB[index];
            float colorR = Mathf.Clamp(FiniteOr(color.R, 0f), 0f, 1f);
            float colorG = Mathf.Clamp(FiniteOr(color.G, 0f), 0f, 1f);
            float colorB = Mathf.Clamp(FiniteOr(color.B, 0f), 0f, 1f);
            float addedDensity = (colorR + colorG + colorB) * falloff;
            _inkR[index] = Mathf.Clamp(_inkR[index] + colorR * falloff, 0f, 1f);
            _inkG[index] = Mathf.Clamp(_inkG[index] + colorG * falloff, 0f, 1f);
            _inkB[index] = Mathf.Clamp(_inkB[index] + colorB * falloff, 0f, 1f);

            // 累加“衰减率 × 注入浓度”；Step 中再除以总浓度得到加权平均衰减率。
            // 因此不同物质混合时不会突然跳到某一方的寿命。
            if (previousDensity + addedDensity > 0.0001f)
                _decayMass[index] += requestedDecay * addedDensity;

            _velocityX[index] = Mathf.Clamp(FiniteOr(_velocityX[index], 0f) + inputVelocityX * falloff, -maxVelocity, maxVelocity);
            _velocityY[index] = Mathf.Clamp(FiniteOr(_velocityY[index], 0f) + inputVelocityY * falloff, -maxVelocity, maxVelocity);
        });
    }

    /// <summary>
    /// 添加一团独立烟雾。烟雾只保存浓度，最终颜色由 FluidManager/FluidCanvas 的实时显示参数决定，
    /// 因此可以在 Remote Inspector 或业务代码中换色，而不需要重新注入烟雾。
    /// </summary>
    /// <param name="normalized">左上为 (0,0)、右下为 (1,1) 的归一化位置。</param>
    /// <param name="radius">相对于模拟域宽高的半径。</param>
    /// <param name="velocity">注入时写入共享速度场的初速度。</param>
    /// <param name="decayPerSecond">烟雾指数衰减率；负数表示使用 Settings 默认浓度衰减率。</param>
    /// <param name="amount">注入浓度倍率；1 为标准浓度，常用范围 0.1～2。</param>
    public void AddSmoke(
        Vector2 normalized,
        float radius = 0.08f,
        Vector2 velocity = default,
        float decayPerSecond = -1f,
        float amount = 1f)
    {
        float defaultDecay = FiniteOr(Parameters.DensityDecayPerSecond, 0.38f);
        float requestedDecay = decayPerSecond >= 0f ? decayPerSecond : defaultDecay;
        requestedDecay = Mathf.Clamp(FiniteOr(requestedDecay, defaultDecay), 0f, 100f);
        float safeAmount = Mathf.Clamp(FiniteOr(amount, 1f), 0f, 10f);
        float maxVelocity = Mathf.Max(0.01f, FiniteOr(Parameters.MaxVelocity, 3f));
        float inputVelocityX = FiniteOr(velocity.X, 0f);
        float inputVelocityY = FiniteOr(velocity.Y, 0f);

        PaintCircle(normalized, radius, (index, falloff) =>
        {
            float previousDensity = _smokeDensity[index];
            float nextDensity = Mathf.Clamp(previousDensity + safeAmount * falloff, 0f, 1f);
            float actuallyAddedDensity = nextDensity - previousDensity;
            _smokeDensity[index] = nextDensity;
            // 只按真正写入数组的浓度累计寿命权重；已饱和单元不会凭空累加衰减率。
            _smokeDecayMass[index] += requestedDecay * actuallyAddedDensity;
            _velocityX[index] = Mathf.Clamp(FiniteOr(_velocityX[index], 0f) + inputVelocityX * falloff, -maxVelocity, maxVelocity);
            _velocityY[index] = Mathf.Clamp(FiniteOr(_velocityY[index], 0f) + inputVelocityY * falloff, -maxVelocity, maxVelocity);
        });
    }

    /// <summary>绘制/擦除障碍物。障碍物只存一个布尔场，比生成碰撞体更轻量。</summary>
    /// <param name="normalized">0～1 的归一化位置。</param>
    /// <param name="radius">障碍物归一化半径。</param>
    /// <param name="solid">true 绘制障碍，false 擦除障碍。</param>
    public void SetObstacle(Vector2 normalized, float radius = 0.06f, bool solid = true)
    {
        PaintCircle(normalized, radius, (index, _) => _obstacle[index] = solid);
    }

    /// <summary>
    /// 在指定位置添加径向速度。爆炸、子弹冲击和其他排斥效果都复用该接口，
    /// 因而演示层不需要为每种武器维护独立的粒子或刚体实现。
    /// </summary>
    /// <param name="normalized">冲击中心的 0～1 归一化位置。</param>
    /// <param name="radius">冲击范围的归一化半径。</param>
    /// <param name="strength">正数向外推、负数向内吸；最终速度受 MaxVelocity 限制。</param>
    public void AddRadialImpulse(Vector2 normalized, float radius, float strength)
    {
        Vector2I center = ToCell(normalized);
        float safeRadius = Mathf.Clamp(Mathf.Abs(FiniteOr(radius, 0f)), 0f, 2f);
        float safeStrength = Mathf.Clamp(FiniteOr(strength, 0f), -100f, 100f);
        float maxVelocity = Mathf.Max(0.01f, FiniteOr(Parameters.MaxVelocity, 3f));
        int rx = Math.Max(1, Mathf.CeilToInt(safeRadius * Width));
        int ry = Math.Max(1, Mathf.CeilToInt(safeRadius * Height));

        for (int dy = -ry; dy <= ry; dy++)
        {
            int y = center.Y + dy;
            if (y < 0 || y >= Height) continue;
            for (int dx = -rx; dx <= rx; dx++)
            {
                int x = center.X + dx;
                if (x < 0 || x >= Width) continue;
                float nx = dx / (float)rx;
                float ny = dy / (float)ry;
                float distance = Mathf.Sqrt(nx * nx + ny * ny);
                if (distance <= 0.001f || distance > 1f) continue;

                int index = Index(x, y);
                if (_obstacle[index]) continue;
                float force = (1f - distance) * safeStrength;
                _velocityX[index] = Mathf.Clamp(FiniteOr(_velocityX[index], 0f) + nx / distance * force, -maxVelocity, maxVelocity);
                _velocityY[index] = Mathf.Clamp(FiniteOr(_velocityY[index], 0f) + ny / distance * force, -maxVelocity, maxVelocity);
            }
        }
    }

    /// <summary>查询指定网格单元是否为障碍；越界坐标会自动夹取到最近边界。</summary>
    /// <param name="x">网格 X 坐标，不是屏幕像素。</param>
    /// <param name="y">网格 Y 坐标，不是屏幕像素。</param>
    /// <returns>对应单元为障碍时返回 true。</returns>
    public bool IsObstacle(int x, int y) => _obstacle[Index(Mathf.Clamp(x, 0, Width - 1), Mathf.Clamp(y, 0, Height - 1))];

    /// <summary>
    /// 半拉格朗日回溯：从当前网格沿速度反向取样。它比 Unity 原版的多次 Blit 更容易
    /// 调试；双线性采样仍能保持平滑的色带和旋涡。水平方向环绕，模拟“无限世界”的感觉。
    /// </summary>
    /// <param name="delta">本次模拟的秒数；内部会限制在 0.001～0.05 秒以维持稳定。</param>
    public void Step(float delta)
    {
        float dt = Mathf.Clamp(delta, 0.001f, 0.05f);
        float velocityDecay = Mathf.Clamp(FiniteOr(Parameters.VelocityDecayPerSecond, 0.75f), 0f, 100f);
        float pressure = Mathf.Clamp(FiniteOr(Parameters.Pressure, 0.45f), 0f, 100f);
        float viscosity = Mathf.Clamp(FiniteOr(Parameters.Viscosity, 1.4f), 0f, 100f);
        float advectionScale = Mathf.Clamp(FiniteOr(Parameters.AdvectionScale, 0.22f), 0f, 10f);
        float gravity = Mathf.Clamp(FiniteOr(Parameters.Gravity, 0f), -100f, 100f);
        float buoyancy = Mathf.Clamp(FiniteOr(Parameters.Buoyancy, 0f), -100f, 100f);
        float maxVelocity = Mathf.Max(0.01f, FiniteOr(Parameters.MaxVelocity, 3f));
        float defaultDensityDecay = Mathf.Clamp(FiniteOr(Parameters.DensityDecayPerSecond, 0.38f), 0f, 100f);
        float velocityDamping = Mathf.Exp(-velocityDecay * dt);
        ActiveCellCount = 0;

        // 双缓冲保证本轮采样的都是“上一帧”数据，不需要复制整张纹理。
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                int i = Index(x, y);
                if (_obstacle[i])
                {
                    _nextInkR[i] = _nextInkG[i] = _nextInkB[i] = 0f;
                    _nextDecayMass[i] = 0f;
                    _nextSmokeDensity[i] = 0f;
                    _nextSmokeDecayMass[i] = 0f;
                    _nextVelocityX[i] = _nextVelocityY[i] = 0f;
                    continue;
                }

                float ux = FiniteOr(_velocityX[i], 0f);
                float uy = FiniteOr(_velocityY[i], 0f);

                // 邻域平均提供少量粘性，避免昂贵的压力投影迭代。
                float averageX = NeighbourAverage(_velocityX, x, y);
                float averageY = NeighbourAverage(_velocityY, x, y);
                float viscosityBlend = Mathf.Clamp(viscosity * dt, 0f, 1f);
                ux = Mathf.Lerp(ux, averageX, viscosityBlend);
                uy = Mathf.Lerp(uy, averageY, viscosityBlend);

                // 使用中心差分近似 Unity shader 中的密度压力项。
                // X/Y 两个方向完全对称，因此不会像旧版本一样持续向屏幕下方漂移。
                float densityLeft = DensityAt((x - 1 + Width) % Width, y);
                float densityRight = DensityAt((x + 1) % Width, y);
                float densityUp = DensityAt(x, Math.Max(0, y - 1));
                float densityDown = DensityAt(x, Math.Min(Height - 1, y + 1));
                ux -= (densityRight - densityLeft) * pressure * dt;
                uy -= (densityDown - densityUp) * pressure * dt;

                // 烟雾与染料共享速度场，因此冲击、障碍、重力和浮力都能作用于两种物质。
                float localDensity = _inkR[i] + _inkG[i] + _inkB[i] + _smokeDensity[i];
                uy += gravity * dt;
                uy -= localDensity * buoyancy * dt;
                ux = Mathf.Clamp(FiniteOr(ux * velocityDamping, 0f), -maxVelocity, maxVelocity);
                uy = Mathf.Clamp(FiniteOr(uy * velocityDamping, 0f), -maxVelocity, maxVelocity);

                // 回溯距离按网格尺寸缩放，使改变分辨率后速度手感保持一致。
                float backX = x - ux * dt * Width * advectionScale;
                float backY = y - uy * dt * Height * advectionScale;

                float rawR = Sample(_inkR, backX, backY);
                float rawG = Sample(_inkG, backX, backY);
                float rawB = Sample(_inkB, backX, backY);
                float rawDensity = rawR + rawG + rawB;
                float sampledDecayMass = Mathf.Max(0f, Sample(_decayMass, backX, backY));
                float decayRate = rawDensity > 0.0001f
                    ? sampledDecayMass / rawDensity
                    : defaultDensityDecay;
                decayRate = Mathf.Clamp(FiniteOr(decayRate, defaultDensityDecay), 0f, 100f);
                float densityDamping = Mathf.Exp(-decayRate * dt);
                float r = rawR * densityDamping;
                float g = rawG * densityDamping;
                float b = rawB * densityDamping;

                float rawSmoke = Mathf.Max(0f, Sample(_smokeDensity, backX, backY));
                float sampledSmokeDecayMass = Mathf.Max(0f, Sample(_smokeDecayMass, backX, backY));
                float smokeDecayRate = rawSmoke > 0.0001f
                    ? sampledSmokeDecayMass / rawSmoke
                    : defaultDensityDecay;
                smokeDecayRate = Mathf.Clamp(FiniteOr(smokeDecayRate, defaultDensityDecay), 0f, 100f);
                float smoke = rawSmoke * Mathf.Exp(-smokeDecayRate * dt);

                _nextInkR[i] = r; _nextInkG[i] = g; _nextInkB[i] = b;
                _nextDecayMass[i] = r + g + b > 0.002f ? decayRate * (r + g + b) : 0f;
                _nextSmokeDensity[i] = smoke;
                _nextSmokeDecayMass[i] = smoke > 0.002f ? smokeDecayRate * smoke : 0f;
                _nextVelocityX[i] = ux; _nextVelocityY[i] = uy;
                if (r + g + b + smoke > 0.01f) ActiveCellCount++;
            }
        }

        (_inkR, _nextInkR) = (_nextInkR, _inkR);
        (_inkG, _nextInkG) = (_nextInkG, _inkG);
        (_inkB, _nextInkB) = (_nextInkB, _inkB);
        (_decayMass, _nextDecayMass) = (_nextDecayMass, _decayMass);
        (_smokeDensity, _nextSmokeDensity) = (_nextSmokeDensity, _smokeDensity);
        (_smokeDecayMass, _nextSmokeDecayMass) = (_nextSmokeDecayMass, _smokeDecayMass);
        (_velocityX, _nextVelocityX) = (_nextVelocityX, _velocityX);
        (_velocityY, _nextVelocityY) = (_nextVelocityY, _velocityY);
    }

    /// <summary>把当前染料场和独立烟雾场写入 Godot Image，供 FluidCanvas 更新 ImageTexture。</summary>
    /// <param name="image">尺寸应与 Width/Height 一致、格式应为 RGBA8 的可写图像。</param>
    /// <param name="smokeCenterColor">烟雾高浓度中心的颜色。</param>
    /// <param name="smokeEdgeColor">烟雾低浓度边缘的颜色。</param>
    /// <param name="smokeEdgeStrength">边缘深色效果强度，0 表示关闭。</param>
    /// <param name="smokeOpacity">烟雾整体不透明度倍率。</param>
    public void WriteImage(
        Image image,
        Color smokeCenterColor,
        Color smokeEdgeColor,
        float smokeEdgeStrength,
        float smokeOpacity)
    {
        float safeEdgeStrength = Mathf.Clamp(FiniteOr(smokeEdgeStrength, 0.72f), 0f, 1f);
        float safeSmokeOpacity = Mathf.Clamp(FiniteOr(smokeOpacity, 1f), 0f, 4f);
        Color safeSmokeCenterColor = SanitizeColor(smokeCenterColor, new Color(0.68f, 0.70f, 0.72f, 1f));
        Color safeSmokeEdgeColor = SanitizeColor(smokeEdgeColor, new Color(0.20f, 0.22f, 0.24f, 1f));
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                int i = Index(x, y);
                if (_obstacle[i])
                {
                    image.SetPixel(x, y, new Color(0.035f, 0.045f, 0.07f, 1f));
                    continue;
                }
                float r = Mathf.Clamp(_inkR[i], 0f, 1f);
                float g = Mathf.Clamp(_inkG[i], 0f, 1f);
                float b = Mathf.Clamp(_inkB[i], 0f, 1f);
                float inkAlpha = Mathf.Clamp((r + g + b) * 0.72f, 0f, 1f);

                float smoke = Mathf.Clamp(_smokeDensity[i], 0f, 1f);
                float smokeAlpha = Mathf.Clamp(smoke * safeSmokeOpacity, 0f, 1f);
                if (smokeAlpha <= 0.0001f)
                {
                    image.SetPixel(x, y, new Color(r, g, b, inkAlpha));
                    continue;
                }

                // 原 Unity 效果的烟雾轮廓比中心更深。这里不增加额外模糊纹理，
                // 只读取四邻域：低浓度区域与局部梯度较大区域被判定为边缘，复杂度仍为 O(W×H)。
                float left = SmokeAt((x - 1 + Width) % Width, y);
                float right = SmokeAt((x + 1) % Width, y);
                float up = SmokeAt(x, Math.Max(0, y - 1));
                float down = SmokeAt(x, Math.Min(Height - 1, y + 1));
                float gradient = Mathf.Abs(right - left) + Mathf.Abs(down - up);
                float thinEdge = 1f - Mathf.SmoothStep(0.08f, 0.52f, smoke);
                float gradientEdge = Mathf.Clamp(gradient / (smoke + 0.03f) * 0.9f, 0f, 1f);
                float edgeFactor = Mathf.Clamp(Mathf.Max(thinEdge, gradientEdge) * safeEdgeStrength, 0f, 1f);
                Color smokeColor = safeSmokeCenterColor.Lerp(safeSmokeEdgeColor, edgeFactor);
                smokeAlpha *= Mathf.Clamp(smokeColor.A, 0f, 1f);

                // 将烟雾覆盖在普通染料之上。烟雾保持独立灰色，同时下方彩色液体仍可透出。
                float combinedAlpha = smokeAlpha + inkAlpha * (1f - smokeAlpha);
                float outR = smokeColor.R;
                float outG = smokeColor.G;
                float outB = smokeColor.B;
                if (combinedAlpha > 0.0001f)
                {
                    outR = (smokeColor.R * smokeAlpha + r * inkAlpha * (1f - smokeAlpha)) / combinedAlpha;
                    outG = (smokeColor.G * smokeAlpha + g * inkAlpha * (1f - smokeAlpha)) / combinedAlpha;
                    outB = (smokeColor.B * smokeAlpha + b * inkAlpha * (1f - smokeAlpha)) / combinedAlpha;
                }
                image.SetPixel(x, y, new Color(outR, outG, outB, combinedAlpha));
            }
        }
    }

    /// <summary>只清除颜色、速度和物质寿命，保留已经绘制的障碍物。</summary>
    public void ClearFluid()
    {
        Array.Clear(_inkR, 0, _inkR.Length); Array.Clear(_inkG, 0, _inkG.Length); Array.Clear(_inkB, 0, _inkB.Length);
        Array.Clear(_velocityX, 0, _velocityX.Length); Array.Clear(_velocityY, 0, _velocityY.Length);
        Array.Clear(_nextInkR, 0, _nextInkR.Length); Array.Clear(_nextInkG, 0, _nextInkG.Length); Array.Clear(_nextInkB, 0, _nextInkB.Length);
        Array.Clear(_decayMass, 0, _decayMass.Length); Array.Clear(_nextDecayMass, 0, _nextDecayMass.Length);
        Array.Clear(_smokeDensity, 0, _smokeDensity.Length); Array.Clear(_nextSmokeDensity, 0, _nextSmokeDensity.Length);
        Array.Clear(_smokeDecayMass, 0, _smokeDecayMass.Length); Array.Clear(_nextSmokeDecayMass, 0, _nextSmokeDecayMass.Length);
        Array.Clear(_nextVelocityX, 0, _nextVelocityX.Length); Array.Clear(_nextVelocityY, 0, _nextVelocityY.Length);
        ActiveCellCount = 0;
    }

    /// <summary>只清除障碍物，不改变当前颜色场和速度场。</summary>
    public void ClearObstacles()
    {
        Array.Clear(_obstacle, 0, _obstacle.Length);
    }

    /// <summary>清除液体、速度和全部障碍物，恢复为空白模拟域。</summary>
    public void Clear()
    {
        ClearFluid();
        ClearObstacles();
    }

    private void PaintCircle(Vector2 normalized, float radius, Action<int, float> paint)
    {
        Vector2I center = ToCell(normalized);
        // 半径限制为最多两个模拟域，防止错误输入造成超大循环或整数溢出。
        float safeRadius = Mathf.Clamp(Mathf.Abs(FiniteOr(radius, 0f)), 0f, 2f);
        int rx = Math.Max(1, Mathf.CeilToInt(safeRadius * Width));
        int ry = Math.Max(1, Mathf.CeilToInt(safeRadius * Height));
        for (int dy = -ry; dy <= ry; dy++)
        {
            int y = center.Y + dy;
            if (y < 0 || y >= Height) continue;
            for (int dx = -rx; dx <= rx; dx++)
            {
                int x = center.X + dx;
                if (x < 0 || x >= Width) continue;
                float distance = Mathf.Sqrt((dx / (float)rx) * (dx / (float)rx) + (dy / (float)ry) * (dy / (float)ry));
                if (distance > 1f) continue;
                paint(Index(x, y), Mathf.SmoothStep(1f, 0f, distance));
            }
        }
    }

    private float Sample(float[] field, float x, float y)
    {
        // 这是所有平流采样的安全边界。之前 Mathf.PosMod 在非有限值或浮点上界附近
        // 可能让 FloorToInt 产生非法索引，靠近障碍物高速绘制时便会触发数组越界。
        if (field == null || field.Length != Width * Height)
            return 0f;
        if (!float.IsFinite(x) || !float.IsFinite(y))
            return 0f;

        float wrappedX = x % Width;
        if (wrappedX < 0f) wrappedX += Width;
        wrappedX = Mathf.Clamp(wrappedX, 0f, Width - 0.0001f);
        float clampedY = Mathf.Clamp(y, 0f, Height - 1.0001f);

        int x0 = Math.Clamp((int)MathF.Floor(wrappedX), 0, Width - 1);
        int y0 = Math.Clamp((int)MathF.Floor(clampedY), 0, Height - 1);
        int x1 = x0 == Width - 1 ? 0 : x0 + 1;
        int y1 = Math.Min(y0 + 1, Height - 1);
        float tx = Mathf.Clamp(wrappedX - x0, 0f, 1f);
        float ty = Mathf.Clamp(clampedY - y0, 0f, 1f);

        // 此处直接计算经过验证的数组下标，避免未来修改 Index() 时破坏采样安全性。
        int index00 = y0 * Width + x0;
        int index10 = y0 * Width + x1;
        int index01 = y1 * Width + x0;
        int index11 = y1 * Width + x1;
        float a = Mathf.Lerp(field[index00], field[index10], tx);
        float b = Mathf.Lerp(field[index01], field[index11], tx);
        return Mathf.Lerp(a, b, ty);
    }

    private float NeighbourAverage(float[] field, int x, int y)
    {
        int left = (x - 1 + Width) % Width, right = (x + 1) % Width;
        int up = Math.Max(0, y - 1), down = Math.Min(Height - 1, y + 1);
        return (field[Index(left, y)] + field[Index(right, y)] + field[Index(x, up)] + field[Index(x, down)]) * 0.25f;
    }

    private float DensityAt(int x, int y)
    {
        int index = Index(x, y);
        return _inkR[index] + _inkG[index] + _inkB[index] + _smokeDensity[index];
    }

    /// <summary>读取烟雾浓度；调用方已经提供安全网格坐标。</summary>
    private float SmokeAt(int x, int y) => _smokeDensity[Index(x, y)];

    /// <summary>把 NaN 和正负无穷替换为安全默认值，阻止坏数据在多帧迭代中传播。</summary>
    private static float FiniteOr(float value, float fallback)
        => float.IsFinite(value) ? value : fallback;

    /// <summary>限制外部传入的颜色，避免 NaN/Infinity 进入 ImageTexture 上传数据。</summary>
    private static Color SanitizeColor(Color color, Color fallback) => new(
        Mathf.Clamp(FiniteOr(color.R, fallback.R), 0f, 1f),
        Mathf.Clamp(FiniteOr(color.G, fallback.G), 0f, 1f),
        Mathf.Clamp(FiniteOr(color.B, fallback.B), 0f, 1f),
        Mathf.Clamp(FiniteOr(color.A, fallback.A), 0f, 1f));
}
