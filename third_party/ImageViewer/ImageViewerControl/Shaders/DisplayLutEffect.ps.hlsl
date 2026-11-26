// 显示调节着色器（窗宽/窗位、亮度、对比度、gamma、反色 + 伪彩色）。
// Chinese: 传递曲线与调色板都只在 C# 侧定义一次（ImageViewerDisplaySourceService.GetDisplayLut 合成的 256 项表），
// 本着色器只做三件事：
//   ① 按 LutMode 决定取样方式（直通 / 单强度索引 / 每通道索引）；
//   ② 强度权重与 CPU 路径的 ImageViewerPixelAccess.ReadIntensity 完全一致；
//   ③ 采样 texel 中心，落在与 CPU 整数索引同一格上，并保留输入 alpha。
// 每通道模式用于"未启用伪彩色"的场合：灰度图下它与单强度模式数值相同，彩色图下它保住色相不被去饱和。
// English: The transfer curve and the palette exist once, in the C# LUT; this shader only picks a sampling mode, uses the
// same luminance weights as the CPU path, samples texel centers and preserves the input alpha.
sampler2D Input : register(s0);
sampler2D DisplayLut : register(s1);
float LutMode : register(c0);

// 采样 texel 中心（LUT 宽 256），避免线性过滤在相邻色阶之间插值。
float lutU(float value)
{
    return (value * 255.0 + 0.5) / 256.0;
}

float4 main(float2 uv : TEXCOORD) : COLOR
{
    float4 inputColor = tex2D(Input, uv);

    if (LutMode < 0.5)
    {
        return inputColor;
    }

    // WPF 的 ShaderEffect 输入是预乘 alpha；先恢复直通颜色再计算强度，
    // 输出也必须重新预乘，否则半透明边缘会变亮或发灰。
    float alpha = inputColor.a;
    if (alpha <= 0.0001)
    {
        return float4(0.0, 0.0, 0.0, 0.0);
    }

    float3 straightInput = inputColor.rgb / alpha;
    float3 color;
    if (LutMode < 1.5)
    {
        // 与 ImageViewerPixelAccess.ReadIntensity 同一组权重。
        float intensity = saturate(dot(straightInput, float3(0.299, 0.587, 0.114)));
        color = tex2D(DisplayLut, float2(lutU(intensity), 0.5)).rgb;
    }
    else
    {
        float2 lutRow = float2(0.0, 0.5);
        lutRow.x = lutU(straightInput.r);
        float4 red = tex2D(DisplayLut, lutRow);
        lutRow.x = lutU(straightInput.g);
        float4 green = tex2D(DisplayLut, lutRow);
        lutRow.x = lutU(straightInput.b);
        float4 blue = tex2D(DisplayLut, lutRow);
        color = float3(red.r, green.g, blue.b);
    }

    return float4(color * alpha, alpha);
}
