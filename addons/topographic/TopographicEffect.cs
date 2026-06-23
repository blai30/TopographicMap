using System.Linq;
using System.Runtime.InteropServices;
using Godot;

namespace TopographicCameraShader;

[Tool]
[GlobalClass]
public partial class TopographicEffect : CompositorEffect
{
    [ExportGroup("Ramp")] [Export] public float MinElevation { get; set; } = 0f;
    [Export] public float MaxElevation { get; set; } = 100f;
    [Export] public int Levels { get; set; } = 14;
    [Export] public bool SmoothRamp { get; set; } = false;
    [Export] public bool InvertRamp { get; set; } = false;
    [Export(PropertyHint.Range, "0,1")] public float FillLow { get; set; } = 0.22f;
    [Export(PropertyHint.Range, "0,1")] public float FillHigh { get; set; } = 1.0f;
    [Export] public Color PaperColor { get; set; } = new(0.93f, 0.88f, 0.78f);
    [Export] public Color InkColor { get; set; } = new(0.12f, 0.07f, 0.03f);

    [ExportGroup("Contours")] [Export] public bool ContoursEnabled { get; set; } = true;
    [Export] public bool MajorContoursEnabled { get; set; } = true;
    [Export] public int MajorEvery { get; set; } = 4;
    [Export] public float MinorWidthPx { get; set; } = 1.4f;
    [Export] public float MajorWidthPx { get; set; } = 3.0f;
    [Export(PropertyHint.Range, "0,1")] public float MinorOpacity { get; set; } = 1.0f;
    [Export(PropertyHint.Range, "0,1")] public float MajorOpacity { get; set; } = 1.0f;
    [Export(PropertyHint.Range, "0,4")] public float MinorFade { get; set; } = 0.3f;

    [ExportGroup("Background")] [Export] public Color BackgroundColor { get; set; } = new(0.85f, 0.78f, 0.63f);

    private const string ShaderPath = "res://addons/topographic/topographic.glsl";

    private RenderingDevice _rd;
    private Rid _shader;
    private Rid _pipeline;
    private Rid _sampler;
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
        _sampler = _rd.SamplerCreate(new()
        {
            MinFilter = RenderingDevice.SamplerFilter.Nearest,
            MagFilter = RenderingDevice.SamplerFilter.Nearest,
            RepeatU = RenderingDevice.SamplerRepeatMode.ClampToEdge,
            RepeatV = RenderingDevice.SamplerRepeatMode.ClampToEdge
        });
    }

    // Self-frees GPU resources at runtime. FreeRid is only legal on the render thread,
    // so the frees are dispatched there (mirroring InitializeCompute). At app shutdown the
    // device may be torn down before that runs, so RIDs leak then -- harmless, process is
    // exiting. This keeps the addon drop-in with no host code required.
    public override void _Notification(int what)
    {
        if (what != NotificationPredelete || _freed || _rd == null)
        {
            return;
        }

        _freed = true;

        // Capture by value -- Rids are structs -- so the deferred callable does not touch
        // this object after it has been destroyed.
        var rd = _rd;
        var pipeline = _pipeline;
        var shader = _shader;
        var sampler = _sampler;
        RenderingServer.CallOnRenderThread(Callable.From(() =>
        {
            if (pipeline.IsValid)
            {
                rd.FreeRid(pipeline);
            }

            if (shader.IsValid)
            {
                rd.FreeRid(shader);
            }

            if (sampler.IsValid)
            {
                rd.FreeRid(sampler);
            }
        }));
    }

    public override void _RenderCallback(int effectCallbackType, RenderData renderData)
    {
        if (_rd == null || effectCallbackType != (int)EffectCallbackTypeEnum.PostTransparent)
        {
            return;
        }

        var sceneBuffers = renderData.GetRenderSceneBuffers() as RenderSceneBuffersRD;
        var sceneData = renderData.GetRenderSceneData() as RenderSceneDataRD;
        if (sceneBuffers == null || sceneData == null)
        {
            return;
        }

        var size = sceneBuffers.GetInternalSize();
        if (size.X == 0 || size.Y == 0)
        {
            return;
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
            depthUniform.AddId(_sampler);
            depthUniform.AddId(sceneBuffers.GetDepthLayer(view));

            var paramsUniform = new RDUniform
            {
                UniformType = RenderingDevice.UniformType.UniformBuffer,
                Binding = 2
            };
            paramsUniform.AddId(paramsBuffer);

            var uniformSet =
                UniformSetCacheRD.GetCache(_shader, 0, [colorUniform, depthUniform, paramsUniform]);

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
    [StructLayout(LayoutKind.Sequential)]
    private struct TopoParams
    {
        public Projection Proj; // forward camera projection (view -> clip)
        public Projection InvView; // camera transform (view -> world)
        public Vector4 RasterAndRange; // raster_size.xy, min_elevation, max_elevation
        public Vector4 RampParams; // levels, fill_low, fill_high, major_every
        public Vector4 ContourWeights; // minor_width_px, major_width_px, minor_opacity, major_opacity
        public Vector4 ContourFlags; // contours_enabled, smooth_ramp, minor_fade, major_contours_enabled
        public Vector4 ModeFlags; // invert_ramp, unused, unused, unused
        public Vector4 InkColor;
        public Vector4 PaperColor;
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
            RampParams = new(Levels, FillLow, FillHigh, MajorEvery),
            ContourWeights = new(MinorWidthPx, MajorWidthPx, MinorOpacity, MajorOpacity),
            ContourFlags = new(
                ContoursEnabled ? 1f : 0f, SmoothRamp ? 1f : 0f, MinorFade, MajorContoursEnabled ? 1f : 0f),
            ModeFlags = new(InvertRamp ? 1f : 0f, 0f, 0f, 0f),
            InkColor = new(InkColor.R, InkColor.G, InkColor.B, InkColor.A),
            PaperColor = new(PaperColor.R, PaperColor.G, PaperColor.B, PaperColor.A),
            BackgroundColor = new(BackgroundColor.R, BackgroundColor.G, BackgroundColor.B, BackgroundColor.A)
        };

        return MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref p, 1)).ToArray();
    }
}
