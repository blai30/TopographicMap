using System.Linq;
using System.Runtime.InteropServices;
using Godot;

namespace TopographicCameraShader;

[Tool]
[GlobalClass]
public partial class TopographicEffect : CompositorEffect
{
    // Width of the baked 1-D gradient palette. 256 samples is plenty for smooth
    // ramps and is sampled with linear filtering, so stepping comes from Levels.
    private const int PaletteWidth = 256;

    [ExportGroup("Ramp")] [Export] public float MinElevation { get; set; } = 0f;
    [Export] public float MaxElevation { get; set; } = 100f;
    [Export] public int Levels { get; set; } = 14;
    [Export] public bool SmoothRamp { get; set; } = false;
    [Export] public bool InvertRamp { get; set; } = false;

    // The gradient is the single source of elevation color: its left end colors the
    // lowest band, its right end the highest. A 2-stop gradient gives the classic
    // monochrome ink-on-paper look; multi-stop gradients give hypsometric tints
    // (heatmap, nautical, etc.). Edited inline with Godot's gradient editor.
    [Export]
    public Gradient Gradient
    {
        get;
        set
        {
            // Track the live gradient's "changed" signal so inspector edits re-bake the
            // palette. Guard connect/disconnect with IsConnected so re-assignment (and the
            // field initializer running before the rest of construction) never raises
            // "disconnect a nonexistent connection".
            var onChanged = Callable.From(OnGradientChanged);

            if (field != null && field.IsConnected(Resource.SignalName.Changed, onChanged))
            {
                field.Disconnect(Resource.SignalName.Changed, onChanged);
            }

            field = value;

            if (field != null && !field.IsConnected(Resource.SignalName.Changed, onChanged))
            {
                field.Connect(Resource.SignalName.Changed, onChanged);
            }

            _gradientDirty = true;
        }
    } = MakeDefaultGradient();

    [ExportGroup("Contours")] [Export] public bool ContoursEnabled { get; set; } = true;
    [Export] public bool MajorContoursEnabled { get; set; } = true;
    [Export] public int MajorEvery { get; set; } = 4;
    [Export] public float MinorWidthPx { get; set; } = 1.4f;
    [Export] public float MajorWidthPx { get; set; } = 3.0f;
    [Export(PropertyHint.Range, "0,1")] public float MinorOpacity { get; set; } = 1.0f;
    [Export(PropertyHint.Range, "0,1")] public float MajorOpacity { get; set; } = 1.0f;
    [Export(PropertyHint.Range, "0,4")] public float MinorFade { get; set; } = 0.3f;

    // The contour-line color. Keep it darker (or otherwise distinct) from the
    // gradient bands it overlays, or the lines wash out.
    [Export] public Color ContourColor { get; set; } = new(0.12f, 0.07f, 0.03f);

    [ExportGroup("Background")] [Export] public Color BackgroundColor { get; set; } = new(0.85f, 0.78f, 0.63f);

    private const string ShaderPath = "res://addons/topographic/topographic.glsl";

    private RenderingDevice _rd;
    private Rid _shader;
    private Rid _pipeline;
    private Rid _depthSampler;
    private Rid _gradientSampler;
    private Rid _gradientTexture;
    private bool _gradientDirty = true;
    private bool _freed;

    public TopographicEffect()
    {
        EffectCallbackType = EffectCallbackTypeEnum.PostTransparent;
        AccessResolvedDepth = true;
        RenderingServer.CallOnRenderThread(Callable.From(InitializeCompute));
    }

    // Locates the TopographicEffect inside a compositor, or null if absent. The
    // addon owns the knowledge of how it is stored so callers never repeat the
    // CompositorEffects lookup.
    public static TopographicEffect FindIn(Compositor compositor) =>
        compositor?.CompositorEffects.OfType<TopographicEffect>().FirstOrDefault();

    // Convenience overload: the effect attached to a camera's compositor, if any.
    public static TopographicEffect FindIn(Camera3D camera) =>
        FindIn(camera?.Compositor);

    // A fresh effect should look good with no setup, so the default gradient is the
    // classic stepped ink-on-paper ramp (low = dark espresso, high = light paper).
    private static Gradient MakeDefaultGradient()
    {
        var gradient = new Gradient();
        gradient.SetOffset(0, 0f);
        gradient.SetColor(0, new(0.298f, 0.248f, 0.195f));
        gradient.SetOffset(1, 1f);
        gradient.SetColor(1, new(0.93f, 0.88f, 0.78f));
        return gradient;
    }

    private void OnGradientChanged() => _gradientDirty = true;

    private void InitializeCompute()
    {
        _rd = RenderingServer.GetRenderingDevice();
        if (_rd == null)
        {
            return;
        }

        var spirV = GD.Load<RDShaderFile>(ShaderPath).GetSpirV();
        _shader = _rd.ShaderCreateFromSpirV(spirV);
        _pipeline = _rd.ComputePipelineCreate(_shader);
        _depthSampler = _rd.SamplerCreate(new()
        {
            MinFilter = RenderingDevice.SamplerFilter.Nearest,
            MagFilter = RenderingDevice.SamplerFilter.Nearest,
            RepeatU = RenderingDevice.SamplerRepeatMode.ClampToEdge,
            RepeatV = RenderingDevice.SamplerRepeatMode.ClampToEdge
        });
        _gradientSampler = _rd.SamplerCreate(new()
        {
            MinFilter = RenderingDevice.SamplerFilter.Linear,
            MagFilter = RenderingDevice.SamplerFilter.Linear,
            RepeatU = RenderingDevice.SamplerRepeatMode.ClampToEdge,
            RepeatV = RenderingDevice.SamplerRepeatMode.ClampToEdge
        });

        var format = new RDTextureFormat
        {
            Width = PaletteWidth,
            Height = 1,
            Depth = 1,
            ArrayLayers = 1,
            Mipmaps = 1,
            TextureType = RenderingDevice.TextureType.Type2D,
            Format = RenderingDevice.DataFormat.R8G8B8A8Unorm,
            UsageBits = RenderingDevice.TextureUsageBits.SamplingBit |
                        RenderingDevice.TextureUsageBits.CanUpdateBit
        };
        _gradientTexture = _rd.TextureCreate(format, new(), [BakeGradient()]);
        _gradientDirty = false;
    }

    // Samples the gradient into a flat RGBA8 row. Runs on the render thread (called
    // from setup and from the render callback when the gradient changes).
    private byte[] BakeGradient()
    {
        var gradient = Gradient ?? MakeDefaultGradient();
        byte[] data = new byte[PaletteWidth * 4];
        for (int i = 0; i < PaletteWidth; i++)
        {
            float offset = (float)i / (PaletteWidth - 1);
            var color = gradient.Sample(offset);
            int b = i * 4;
            data[b + 0] = (byte)Mathf.Clamp(Mathf.RoundToInt(color.R * 255f), 0, 255);
            data[b + 1] = (byte)Mathf.Clamp(Mathf.RoundToInt(color.G * 255f), 0, 255);
            data[b + 2] = (byte)Mathf.Clamp(Mathf.RoundToInt(color.B * 255f), 0, 255);
            data[b + 3] = (byte)Mathf.Clamp(Mathf.RoundToInt(color.A * 255f), 0, 255);
        }

        return data;
    }

    // Self-frees GPU resources at runtime. FreeRid is only legal on the render thread,
    // so the frees are dispatched there (mirroring InitializeCompute). At app shutdown the
    // device may be torn down before that runs, so RIDs leak then -- harmless, process is
    // exiting (see the "RID leaked" note in the gotchas; it is a known Godot limitation for
    // C# CompositorEffects, not a bug here). This keeps the addon drop-in with no host code.
    public override void _Notification(int what)
    {
        if (what != NotificationPredelete || _freed || _rd == null)
        {
            return;
        }

        _freed = true;
        DisconnectGradientSignal();

        // Capture by value -- Rids are structs and the array holds copies -- so the deferred
        // callable never touches this object after it has been destroyed. Order matches the
        // original frees (pipeline before its shader) so nothing is freed via a stale handle.
        var rd = _rd;
        Rid[] rids = [_pipeline, _shader, _depthSampler, _gradientSampler, _gradientTexture];
        RenderingServer.CallOnRenderThread(Callable.From(() =>
        {
            foreach (var rid in rids)
            {
                if (rid.IsValid)
                {
                    rd.FreeRid(rid);
                }
            }
        }));
    }

    private void DisconnectGradientSignal()
    {
        var onChanged = Callable.From(OnGradientChanged);
        if (Gradient != null && Gradient.IsConnected(Resource.SignalName.Changed, onChanged))
        {
            Gradient.Disconnect(Resource.SignalName.Changed, onChanged);
        }
    }

    public override void _RenderCallback(int effectCallbackType, RenderData renderData)
    {
        if (_rd == null || effectCallbackType != (int)EffectCallbackTypeEnum.PostTransparent)
        {
            return;
        }

        // These C# wrappers must be disposed deterministically on the render thread.
        // Left to the GC, they are finalized on a background thread and their Dispose
        // calls free_rid off the render thread, spamming "free_rid can only be called
        // from the render thread" at random (Godot issue #104263). `using` frees them
        // here, on the render thread, before the callback returns.
        using var sceneBuffers = renderData.GetRenderSceneBuffers() as RenderSceneBuffersRD;
        using var sceneData = renderData.GetRenderSceneData() as RenderSceneDataRD;
        if (sceneBuffers == null || sceneData == null)
        {
            return;
        }

        var size = sceneBuffers.GetInternalSize();
        if (size.X == 0 || size.Y == 0)
        {
            return;
        }

        // Re-bake the palette only when the gradient actually changed (on edit or
        // resource swap), then reuse the same texture every frame.
        if (_gradientDirty && _gradientTexture.IsValid)
        {
            _rd.TextureUpdate(_gradientTexture, 0, BakeGradient());
            _gradientDirty = false;
        }

        uint xGroups = ((uint)size.X - 1) / 8 + 1;
        uint yGroups = ((uint)size.Y - 1) / 8 + 1;

        // Shader inverts proj itself -- C# Projection.Inverse() returns zero for this ortho projection.
        byte[] paramsBytes = BuildParams(sceneData.GetCamProjection(), sceneData.GetCamTransform(), size);
        var paramsBuffer = _rd.UniformBufferCreate((uint)paramsBytes.Length, paramsBytes);

        uint viewCount = sceneBuffers.GetViewCount();
        for (uint view = 0; view < viewCount; view++)
        {
            var colorUniform = new RDUniform
            {
                UniformType = RenderingDevice.UniformType.Image,
                Binding = 0
            };
            colorUniform.AddId(sceneBuffers.GetColorLayer(view));

            var depthUniform = new RDUniform
            {
                UniformType = RenderingDevice.UniformType.SamplerWithTexture,
                Binding = 1
            };
            depthUniform.AddId(_depthSampler);
            depthUniform.AddId(sceneBuffers.GetDepthLayer(view));

            var paramsUniform = new RDUniform
            {
                UniformType = RenderingDevice.UniformType.UniformBuffer,
                Binding = 2
            };
            paramsUniform.AddId(paramsBuffer);

            var gradientUniform = new RDUniform
            {
                UniformType = RenderingDevice.UniformType.SamplerWithTexture,
                Binding = 3
            };
            gradientUniform.AddId(_gradientSampler);
            gradientUniform.AddId(_gradientTexture);

            var uniformSet = UniformSetCacheRD.GetCache(
                _shader, 0, [colorUniform, depthUniform, paramsUniform, gradientUniform]);

            long computeList = _rd.ComputeListBegin();
            _rd.ComputeListBindComputePipeline(computeList, _pipeline);
            _rd.ComputeListBindUniformSet(computeList, uniformSet, 0);
            _rd.ComputeListDispatch(computeList, xGroups, yGroups, 1);
            _rd.ComputeListEnd();
        }

        _rd.FreeRid(paramsBuffer);
    }

    // Mirrors the std140 Params block in topographic.glsl one-to-one. Every member
    // is padded to a full vec4, so std140 alignment is automatic (each row lands on
    // a 16-byte boundary) and LayoutKind.Sequential reproduces the shader layout
    // byte-for-byte. Field order and per-lane meaning must match the shader block.
    // Band color comes from the gradient texture, not this block.
    [StructLayout(LayoutKind.Sequential)]
    private struct TopoParams
    {
        public Projection Proj; // forward camera projection (view -> clip)
        public Projection InvView; // camera transform (view -> world)
        public Vector4 RasterAndRange; // raster_size.xy, min_elevation, max_elevation
        public Vector4 RampParams; // levels, major_every, unused, unused
        public Vector4 ContourWeights; // minor_width_px, major_width_px, minor_opacity, major_opacity
        public Vector4 ContourFlags; // contours_enabled, smooth_ramp, minor_fade, major_contours_enabled
        public Vector4 ModeFlags; // invert_ramp, unused, unused, unused
        public Vector4 ContourColor;
        public Vector4 BackgroundColor;
    }

    private byte[] BuildParams(Projection proj, Transform3D cam, Vector2I size)
    {
        // Pack inv_view from the camera's basis columns and origin directly, so the
        // columns match the shader's expected view -> world matrix exactly.
        var invView = new Projection(
            new(cam.Basis.X.X, cam.Basis.X.Y, cam.Basis.X.Z, 0f),
            new(cam.Basis.Y.X, cam.Basis.Y.Y, cam.Basis.Y.Z, 0f),
            new(cam.Basis.Z.X, cam.Basis.Z.Y, cam.Basis.Z.Z, 0f),
            new(cam.Origin.X, cam.Origin.Y, cam.Origin.Z, 1f));

        var p = new TopoParams
        {
            Proj = proj,
            InvView = invView,
            RasterAndRange = new(size.X, size.Y, MinElevation, MaxElevation),
            RampParams = new(Levels, MajorEvery, 0f, 0f),
            ContourWeights = new(MinorWidthPx, MajorWidthPx, MinorOpacity, MajorOpacity),
            ContourFlags = new(
                ContoursEnabled ? 1f : 0f, SmoothRamp ? 1f : 0f, MinorFade, MajorContoursEnabled ? 1f : 0f),
            ModeFlags = new(InvertRamp ? 1f : 0f, 0f, 0f, 0f),
            ContourColor = new(ContourColor.R, ContourColor.G, ContourColor.B, ContourColor.A),
            BackgroundColor = new(BackgroundColor.R, BackgroundColor.G, BackgroundColor.B, BackgroundColor.A)
        };

        return MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref p, 1)).ToArray();
    }
}
