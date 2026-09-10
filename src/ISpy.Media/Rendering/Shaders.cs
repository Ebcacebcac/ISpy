namespace ISpy.Media.Rendering;

/// <summary>
/// HLSL for drawing a decoded NV12 frame.
/// </summary>
/// <remarks>
/// Compiled at runtime rather than shipped as bytecode: it is a few hundred microseconds once per
/// process, and it keeps the build free of a shader-compilation step and the .cso files that go
/// stale without anyone noticing.
/// </remarks>
public static class Shaders
{
    /// <summary>
    /// Emits a quad from the vertex id alone - no vertex or index buffer. The destination rectangle
    /// arrives in clip space so one shader draws every tile.
    /// </summary>
    public const string VertexShader = """
        cbuffer TileConstants : register(b0)
        {
            float4 Rect;      // x, y, width, height in normalised device coordinates
            float4 Reserved;
        };

        struct VSOut
        {
            float4 Position : SV_POSITION;
            float2 Texture  : TEXCOORD0;
        };

        VSOut main(uint id : SV_VertexID)
        {
            // 0 -> (0,0)  1 -> (1,0)  2 -> (0,1)  3 -> (1,1), drawn as a triangle strip.
            float2 corner = float2(id & 1, (id >> 1) & 1);

            VSOut output;
            output.Position = float4(
                Rect.x + corner.x * Rect.z,
                Rect.y - corner.y * Rect.w,
                0.0f,
                1.0f);
            output.Texture = corner;
            return output;
        }
        """;

    /// <summary>
    /// NV12 to RGB. Cameras in this family emit BT.709 limited range; treating it as full range is
    /// what makes a picture look washed out or crushed compared with the vendor's own client.
    /// </summary>
    public const string PixelShader = """
        Texture2D<float>  LumaPlane   : register(t0);
        Texture2D<float2> ChromaPlane : register(t1);
        SamplerState      Bilinear    : register(s0);

        struct PSIn
        {
            float4 Position : SV_POSITION;
            float2 Texture  : TEXCOORD0;
        };

        float4 main(PSIn input) : SV_TARGET
        {
            float  y  = LumaPlane.Sample(Bilinear, input.Texture);
            float2 uv = ChromaPlane.Sample(Bilinear, input.Texture);

            // Limited range: Y is 16..235, chroma 16..240, both scaled to 0..1 by the sampler.
            y  = (y - 0.0627451f) * 1.164383f;
            uv -= float2(0.5f, 0.5f);

            float3 rgb;
            rgb.r = y + 1.792741f * uv.y;
            rgb.g = y - 0.213249f * uv.x - 0.532909f * uv.y;
            rgb.b = y + 2.112402f * uv.x;

            return float4(saturate(rgb), 1.0f);
        }
        """;
}
