using System;
using Godot;

namespace TopographicMap;

// Compositor effect that turns the top-down camera depth buffer into a topographic
// data buffer via compute shaders. It writes, into the RGBA16F color image:
//   R = normalized world height, G = coverage mask,
//   B = distance to the nearest contour line, A = that line's level index.
// The contour fields (B, A) are built on the GPU with a jump-flood signed-distance
// pass, so the unified canvas shader can draw crisp constant-width lines with no CPU
// work and no bake. Attach via a Compositor on the map camera only.
[Tool]
[GlobalClass]
public partial class TopographicCompositorEffect : CompositorEffect
{
    [Export] public float HeightMin = -40.0f;
    [Export] public float HeightMax = 110.0f;
    [Export] public float ContourInterval = 10.0f;
    [Export] public float CameraY = 200.0f;
    [Export] public float NearPlane = 80.0f;
    [Export] public float FarPlane = 245.0f;
    [Export] public bool DepthReversed = true;

    private RenderingDevice _rd;
    private Rid _heightShader, _heightPipeline;
    private Rid _seedShader, _seedPipeline;
    private Rid _jfaShader, _jfaPipeline;
    private Rid _compositeShader, _compositePipeline;
    private Rid _depthSampler;
    private Rid _seedA, _seedB;
    private Vector2I _seedSize;
    private bool _ready;

    public TopographicCompositorEffect()
    {
        EffectCallbackType = EffectCallbackTypeEnum.PreTransparent;
        _rd = RenderingServer.GetRenderingDevice();
        if (_rd == null)
        {
            return;
        }

        bool ok =
            LoadShader("res://addons/topographic/topographic.glsl", out _heightShader, out _heightPipeline)
            && LoadShader("res://addons/topographic/contour_seed.glsl", out _seedShader, out _seedPipeline)
            && LoadShader("res://addons/topographic/contour_jfa.glsl", out _jfaShader, out _jfaPipeline)
            && LoadShader("res://addons/topographic/contour_composite.glsl", out _compositeShader,
                out _compositePipeline);
        if (!ok)
        {
            return;
        }

        var nearest = new RDSamplerState
        {
            MinFilter = RenderingDevice.SamplerFilter.Nearest,
            MagFilter = RenderingDevice.SamplerFilter.Nearest,
            RepeatU = RenderingDevice.SamplerRepeatMode.ClampToEdge,
            RepeatV = RenderingDevice.SamplerRepeatMode.ClampToEdge
        };
        _depthSampler = _rd.SamplerCreate(nearest);

        _ready = _depthSampler.IsValid;
    }

    private bool LoadShader(string path, out Rid shader, out Rid pipeline)
    {
        shader = new();
        pipeline = new();
        var shaderFile = GD.Load<RDShaderFile>(path);
        if (shaderFile == null)
        {
            return false;
        }

        shader = _rd.ShaderCreateFromSpirV(shaderFile.GetSpirV());
        if (!shader.IsValid)
        {
            return false;
        }

        pipeline = _rd.ComputePipelineCreate(shader);
        return pipeline.IsValid;
    }

    public override void _Notification(int what)
    {
        if (what != NotificationPredelete || _rd == null) return;
        FreeRid(_depthSampler);
        FreeRid(_seedA);
        FreeRid(_seedB);
        FreeRid(_heightPipeline);
        FreeRid(_heightShader);
        FreeRid(_seedPipeline);
        FreeRid(_seedShader);
        FreeRid(_jfaPipeline);
        FreeRid(_jfaShader);
        FreeRid(_compositePipeline);
        FreeRid(_compositeShader);
    }

    private void FreeRid(Rid rid)
    {
        if (rid.IsValid)
        {
            _rd.FreeRid(rid);
        }
    }

    private void EnsureSeedTextures(Vector2I size)
    {
        if (_seedA.IsValid && _seedSize == size)
        {
            return;
        }

        FreeRid(_seedA);
        FreeRid(_seedB);

        var fmt = new RDTextureFormat
        {
            Format = RenderingDevice.DataFormat.R32G32B32A32Sfloat,
            Width = (uint)size.X,
            Height = (uint)size.Y,
            Depth = 1,
            ArrayLayers = 1,
            Mipmaps = 1,
            TextureType = RenderingDevice.TextureType.Type2D,
            UsageBits = RenderingDevice.TextureUsageBits.StorageBit | RenderingDevice.TextureUsageBits.CanUpdateBit
        };
        _seedA = _rd.TextureCreate(fmt, new(), new());
        _seedB = _rd.TextureCreate(fmt, new(), new());
        _seedSize = size;
    }

    private static RDUniform ImageUniform(int binding, Rid image)
    {
        var u = new RDUniform { UniformType = RenderingDevice.UniformType.Image, Binding = binding };
        u.AddId(image);
        return u;
    }

    private static byte[] Floats(params float[] values)
    {
        byte[] bytes = new byte[values.Length * sizeof(float)];
        Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    public override void _RenderCallback(int effectCallbackType, RenderData renderData)
    {
        if (!_ready || effectCallbackType != (int)EffectCallbackTypeEnum.PreTransparent)
        {
            return;
        }

        if (renderData.GetRenderSceneBuffers() is not RenderSceneBuffersRD sceneBuffers)
        {
            return;
        }

        var size = sceneBuffers.GetInternalSize();
        if (size.X == 0 || size.Y == 0)
        {
            return;
        }

        EnsureSeedTextures(size);

        uint xGroups = ((uint)size.X - 1) / 8 + 1;
        uint yGroups = ((uint)size.Y - 1) / 8 + 1;

        byte[] heightPush = Floats(size.X, size.Y, CameraY, NearPlane, FarPlane, HeightMin, HeightMax,
            DepthReversed ? 1.0f : 0.0f);
        byte[] seedPush = Floats(size.X, size.Y, HeightMin, HeightMax, ContourInterval, 0f, 0f, 0f);
        byte[] compositePush = Floats(size.X, size.Y, 0f, 0f);

        int startStep = 1;
        while (startStep * 2 < Math.Max(size.X, size.Y))
        {
            startStep *= 2;
        }

        uint viewCount = sceneBuffers.GetViewCount();
        for (uint view = 0; view < viewCount; view++)
        {
            var colorImage = sceneBuffers.GetColorLayer(view);
            var depthImage = sceneBuffers.GetDepthLayer(view);

            var depthUniform = new RDUniform
            {
                UniformType = RenderingDevice.UniformType.SamplerWithTexture,
                Binding = 1
            };
            depthUniform.AddId(_depthSampler);
            depthUniform.AddId(depthImage);

            var heightSet = UniformSetCacheRD.GetCache(_heightShader, 0, [ImageUniform(0, colorImage), depthUniform]);
            var seedSet =
                UniformSetCacheRD.GetCache(_seedShader, 0, [ImageUniform(0, colorImage), ImageUniform(1, _seedA)]);
            var jfaSetAB =
                UniformSetCacheRD.GetCache(_jfaShader, 0, [ImageUniform(0, _seedA), ImageUniform(1, _seedB)]);
            var jfaSetBA =
                UniformSetCacheRD.GetCache(_jfaShader, 0, [ImageUniform(0, _seedB), ImageUniform(1, _seedA)]);

            long list = _rd.ComputeListBegin();

            // Height + mask into the color image.
            _rd.ComputeListBindComputePipeline(list, _heightPipeline);
            _rd.ComputeListBindUniformSet(list, heightSet, 0);
            _rd.ComputeListSetPushConstant(list, heightPush, (uint)heightPush.Length);
            _rd.ComputeListDispatch(list, xGroups, yGroups, 1);
            _rd.ComputeListAddBarrier(list);

            // Seed pass: band edges into seedA.
            _rd.ComputeListBindComputePipeline(list, _seedPipeline);
            _rd.ComputeListBindUniformSet(list, seedSet, 0);
            _rd.ComputeListSetPushConstant(list, seedPush, (uint)seedPush.Length);
            _rd.ComputeListDispatch(list, xGroups, yGroups, 1);
            _rd.ComputeListAddBarrier(list);

            // Jump flood: ping-pong seedA/seedB with halving step. After the loop the
            // latest result is in `finalIsA` ? seedA : seedB.
            bool srcIsA = true;
            for (int step = startStep; step >= 1; step /= 2)
            {
                _rd.ComputeListBindComputePipeline(list, _jfaPipeline);
                _rd.ComputeListBindUniformSet(list, srcIsA ? jfaSetAB : jfaSetBA, 0);
                _rd.ComputeListSetPushConstant(list, Floats(size.X, size.Y, step, 0f), 16);
                _rd.ComputeListDispatch(list, xGroups, yGroups, 1);
                _rd.ComputeListAddBarrier(list);
                srcIsA = !srcIsA;
            }

            var finalSeed = srcIsA ? _seedA : _seedB;
            var compositeSet = UniformSetCacheRD.GetCache(_compositeShader, 0,
                [ImageUniform(0, colorImage), ImageUniform(1, finalSeed)]);

            // Composite distance + level into color image B, A.
            _rd.ComputeListBindComputePipeline(list, _compositePipeline);
            _rd.ComputeListBindUniformSet(list, compositeSet, 0);
            _rd.ComputeListSetPushConstant(list, compositePush, (uint)compositePush.Length);
            _rd.ComputeListDispatch(list, xGroups, yGroups, 1);

            _rd.ComputeListEnd();
        }
    }
}
