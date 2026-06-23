using System;
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

    private void InitializeCompute()
    {
        _rd = RenderingServer.GetRenderingDevice();
        if (_rd == null)
            return;

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

    // Self-frees GPU resources at runtime. At app shutdown the device may be torn
    // down before PREDELETE fires, so RIDs leak then -- harmless, process is exiting.
    // This keeps the addon drop-in with no host code required.
    public override void _Notification(int what)
    {
        if (what != NotificationPredelete || _freed || _rd == null)
            return;
        _freed = true;
        if (_pipeline.IsValid) _rd.FreeRid(_pipeline);
        if (_shader.IsValid) _rd.FreeRid(_shader);
        if (_sampler.IsValid) _rd.FreeRid(_sampler);
    }

    public override void _RenderCallback(int effectCallbackType, RenderData renderData)
    {
        if (_rd == null || effectCallbackType != (int)EffectCallbackTypeEnum.PostTransparent)
            return;

        var sceneBuffers = renderData.GetRenderSceneBuffers() as RenderSceneBuffersRD;
        var sceneData = renderData.GetRenderSceneData() as RenderSceneDataRD;
        if (sceneBuffers == null || sceneData == null)
            return;

        var size = sceneBuffers.GetInternalSize();
        if (size.X == 0 || size.Y == 0)
            return;

        uint xGroups = ((uint)size.X - 1) / 8 + 1;
        uint yGroups = ((uint)size.Y - 1) / 8 + 1;

        // Shader inverts proj itself -- C# Projection.Inverse() returns zero for this ortho projection.
        byte[] paramsBytes = BuildParams(sceneData.GetCamProjection(), sceneData.GetCamTransform(), size);
        var paramsBuffer = _rd.UniformBufferCreate((uint)paramsBytes.Length, paramsBytes);

        uint viewCount = sceneBuffers.GetViewCount();
        for (uint view = 0; view < viewCount; view++)
        {
            var colorUniform = new RDUniform { UniformType = RenderingDevice.UniformType.Image, Binding = 0 };
            colorUniform.AddId(sceneBuffers.GetColorLayer(view));

            var depthUniform = new RDUniform
                { UniformType = RenderingDevice.UniformType.SamplerWithTexture, Binding = 1 };
            depthUniform.AddId(_sampler);
            depthUniform.AddId(sceneBuffers.GetDepthLayer(view));

            var paramsUniform = new RDUniform { UniformType = RenderingDevice.UniformType.UniformBuffer, Binding = 2 };
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

    // std140 UBO: two mat4 (proj, inv_view) then misc/color vec4s.
    // Must stay in lockstep with the Params block in topographic.glsl.
    private byte[] BuildParams(Projection proj, Transform3D cam, Vector2I size)
    {
        float[] data = new float[64];
        int i = 0;

        void Column(float x, float y, float z, float w)
        {
            data[i++] = x;
            data[i++] = y;
            data[i++] = z;
            data[i++] = w;
        }

        Column(proj.X.X, proj.X.Y, proj.X.Z, proj.X.W);
        Column(proj.Y.X, proj.Y.Y, proj.Y.Z, proj.Y.W);
        Column(proj.Z.X, proj.Z.Y, proj.Z.Z, proj.Z.W);
        Column(proj.W.X, proj.W.Y, proj.W.Z, proj.W.W);

        Column(cam.Basis.X.X, cam.Basis.X.Y, cam.Basis.X.Z, 0f);
        Column(cam.Basis.Y.X, cam.Basis.Y.Y, cam.Basis.Y.Z, 0f);
        Column(cam.Basis.Z.X, cam.Basis.Z.Y, cam.Basis.Z.Z, 0f);
        Column(cam.Origin.X, cam.Origin.Y, cam.Origin.Z, 1f);

        Column(size.X, size.Y, MinElevation, MaxElevation);
        Column(Levels, FillLow, FillHigh, MajorEvery);
        Column(MinorWidthPx, MajorWidthPx, MinorOpacity, MajorOpacity);
        Column(ContoursEnabled ? 1f : 0f, SmoothRamp ? 1f : 0f, MinorFade, MajorContoursEnabled ? 1f : 0f);
        Column(InvertRamp ? 1f : 0f, 0f, 0f, 0f);
        Column(InkColor.R, InkColor.G, InkColor.B, InkColor.A);
        Column(PaperColor.R, PaperColor.G, PaperColor.B, PaperColor.A);
        Column(BackgroundColor.R, BackgroundColor.G, BackgroundColor.B, BackgroundColor.A);

        return MemoryMarshal.AsBytes(data.AsSpan()).ToArray();
    }
}
