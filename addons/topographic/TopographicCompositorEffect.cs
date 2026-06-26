using System;
using Godot;

namespace TopographicMap;

// Compositor effect that turns the top-down camera depth buffer into topographic data via
// compute shaders. It writes R = normalized world height and G = coverage mask into the
// RGBA16F color image, and, in a separate persistent texture, the per-cell contour SEGMENT
// (both endpoints, in UV) crossing each grid cell. The unified canvas shader samples that
// segment texture and computes exact point-to-segment distance per display pixel, so it
// draws crisp constant-width vector lines with no distance field, no CPU work, and no bake.
// Attach via a Compositor on the map camera only.
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

    // Persistent per-cell contour segment texture, wrapped as a Texture2D so the canvas
    // shader can sample it directly and compute exact (vector) line distance per display
    // pixel. The compositor owns the underlying RD texture and sets this wrapper's RID.
    public Texture2Drd SegmentTexture { get; } = new();

    // True once the first render callback has produced the real segment texture. Consumers
    // should wait for this before drawing, so they never sample before the producer runs
    // (drawing the segment sampler before its RID is live trips a "set (1)" draw error). Set
    // on the rendering thread, read on the main thread, hence volatile.
    private volatile bool _hasProduced;
    public bool HasProduced => _hasProduced;

    private RenderingDevice _rd;
    private Rid _heightShader, _heightPipeline;
    private Rid _seedShader, _seedPipeline;
    private Rid _depthSampler;
    private Rid _segments;
    private Vector2I _segSize;
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
            && LoadShader("res://addons/topographic/contour_seed.glsl", out _seedShader, out _seedPipeline);
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

        // Give SegmentTexture a valid RID up front. A consumer view binds this wrapper at
        // _Ready, before the first render populates the real segment texture; an empty RID
        // would make the canvas shader fail to supply its sampler uniform on that first draw
        // ("Uniforms were never supplied for set (1)"). A 1x1 placeholder avoids that window
        // and is replaced at the real buffer size on the first render callback.
        CreateSegmentTexture(new(1, 1));

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
        SegmentTexture.TextureRdRid = new();
        FreeRid(_depthSampler);
        FreeRid(_segments);
        FreeRid(_heightPipeline);
        FreeRid(_heightShader);
        FreeRid(_seedPipeline);
        FreeRid(_seedShader);
    }

    private void FreeRid(Rid rid)
    {
        if (rid.IsValid)
        {
            _rd.FreeRid(rid);
        }
    }

    private void EnsureSegmentTexture(Vector2I size)
    {
        if (_segments.IsValid && _segSize == size)
        {
            return;
        }

        CreateSegmentTexture(size);
    }

    private void CreateSegmentTexture(Vector2I size)
    {
        // StorageBit so the seed compute pass can write it, SamplingBit so the canvas shader
        // can sample it. Wrap it in the exposed Texture2Drd for consumers to bind.
        var fmt = new RDTextureFormat
        {
            Format = RenderingDevice.DataFormat.R32G32B32A32Sfloat,
            Width = (uint)size.X,
            Height = (uint)size.Y,
            Depth = 1,
            ArrayLayers = 1,
            Mipmaps = 1,
            TextureType = RenderingDevice.TextureType.Type2D,
            UsageBits = RenderingDevice.TextureUsageBits.StorageBit | RenderingDevice.TextureUsageBits.SamplingBit
                                                                    | RenderingDevice.TextureUsageBits.CanUpdateBit
        };

        // Repoint the Texture2Drd to the new texture BEFORE freeing the previous rd_texture.
        // The wrapper still references the old RID, so freeing it first would leave the
        // setter operating on a freed RID ("Attempted to free invalid ID" / double free).
        var previous = _segments;
        _segments = _rd.TextureCreate(fmt, new(), new());
        SegmentTexture.TextureRdRid = _segments;
        FreeRid(previous);
        _segSize = size;
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

        EnsureSegmentTexture(size);

        uint xGroups = ((uint)size.X - 1) / 8 + 1;
        uint yGroups = ((uint)size.Y - 1) / 8 + 1;

        byte[] heightPush = Floats(size.X, size.Y, CameraY, NearPlane, FarPlane, HeightMin, HeightMax,
            DepthReversed ? 1.0f : 0.0f);
        byte[] seedPush = Floats(size.X, size.Y, HeightMin, HeightMax, ContourInterval, 0f, 0f, 0f);

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
            var seedSet = UniformSetCacheRD.GetCache(_seedShader, 0,
                [ImageUniform(0, colorImage), ImageUniform(1, _segments)]);

            long list = _rd.ComputeListBegin();

            // Height + mask into the color image.
            _rd.ComputeListBindComputePipeline(list, _heightPipeline);
            _rd.ComputeListBindUniformSet(list, heightSet, 0);
            _rd.ComputeListSetPushConstant(list, heightPush, (uint)heightPush.Length);
            _rd.ComputeListDispatch(list, xGroups, yGroups, 1);
            _rd.ComputeListAddBarrier(list);

            // Seed pass: per-cell contour segment into the persistent segment texture.
            _rd.ComputeListBindComputePipeline(list, _seedPipeline);
            _rd.ComputeListBindUniformSet(list, seedSet, 0);
            _rd.ComputeListSetPushConstant(list, seedPush, (uint)seedPush.Length);
            _rd.ComputeListDispatch(list, xGroups, yGroups, 1);

            _rd.ComputeListEnd();
        }

        _hasProduced = true;
    }
}
