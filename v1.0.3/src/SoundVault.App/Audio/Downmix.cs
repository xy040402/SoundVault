using System;

namespace SoundVault.App.Audio;

/// <summary>
/// 多声道 → 立体声降混。
/// 以前只取前两轨（FL/FR），中置和环绕直接丢掉；这里按 WAVE 通道掩码做加权求和，并做峰值限幅。
/// </summary>
public static class Downmix
{
    // WAVE 通道掩码位（按位从低到高就是文件里通道的顺序）
    private const int FL = 0x1, FR = 0x2, FC = 0x4, LFE = 0x8;
    private const int BL = 0x10, BR = 0x20, FLC = 0x40, FRC = 0x80, BC = 0x100;
    private const int SL = 0x200, SR = 0x400;

    /// <summary>中置/环绕的混合系数（-3 dB），LFE 取一半避免轰头。</summary>
    private const float SurroundGain = 0.7071f;
    private const float CenterGain = 0.7071f;
    private const float LfeGain = 0.5f;

    /// <summary>把多声道样本降成 L/R 交错数组；mask 为 0 时按标准顺序猜。</summary>
    public static float[] ToStereo(float[] src, int channels, int mask)
    {
        if (channels <= 2) return src;

        int frames = src.Length / channels;
        var output = new float[frames * 2];
        var (toL, toR) = Coefficients(channels, mask);

        float peak = 0;
        for (int f = 0; f < frames; f++)
        {
            int b = f * channels;
            float l = 0, r = 0;
            for (int c = 0; c < channels; c++)
            {
                float s = src[b + c];
                l += s * toL[c];
                r += s * toR[c];
            }
            output[f * 2] = l;
            output[f * 2 + 1] = r;

            float a = Math.Max(Math.Abs(l), Math.Abs(r));
            if (a > peak) peak = a;
        }

        // 叠加后可能超过 0 dBFS：整体缩一次，别让它削波
        if (peak > 0.999f)
        {
            float k = 0.999f / peak;
            for (int i = 0; i < output.Length; i++) output[i] *= k;
            LastGain = k;
        }
        else
        {
            LastGain = 1f;
        }

        LastPeakBeforeLimit = peak;
        LastLayout = Describe(channels, mask);
        return output;
    }

    /// <summary>上次降混的峰值（限幅前）与施加的增益，供日志/自检用。</summary>
    public static float LastPeakBeforeLimit { get; private set; } = 1f;
    public static float LastGain { get; private set; } = 1f;
    public static string LastLayout { get; private set; } = "";

    private static (float[] L, float[] R) Coefficients(int channels, int mask)
    {
        var l = new float[channels];
        var r = new float[channels];

        if (mask == 0)
        {
            // 没有掩码：按最常见顺序猜 FL, FR, FC, LFE, BL, BR, SL, SR
            int[] guess = { FL, FR, FC, LFE, BL, BR, SL, SR, BC, FLC, FRC };
            for (int c = 0; c < channels; c++) Apply(guess[Math.Min(c, guess.Length - 1)], ref l[c], ref r[c]);
            return (l, r);
        }

        int bit = 0;
        for (int c = 0; c < channels && bit < 32; c++)
        {
            while (bit < 32 && (mask & (1 << bit)) == 0) bit++;
            if (bit >= 32) break;
            Apply(1 << bit, ref l[c], ref r[c]);
            bit++;
        }
        return (l, r);
    }

    private static void Apply(int flag, ref float l, ref float r)
    {
        switch (flag)
        {
            case FL: l = 1f; break;
            case FR: r = 1f; break;
            case FC: l = CenterGain; r = CenterGain; break;
            case LFE: l = LfeGain; r = LfeGain; break;
            case BL: case SL: case FLC: l = SurroundGain; break;
            case BR: case SR: case FRC: r = SurroundGain; break;
            case BC: l = SurroundGain; r = SurroundGain; break;
            default:
                // 不认识的通道：左右各给一点，总比丢掉好
                l = 0.5f; r = 0.5f; break;
        }
    }

    public static string Describe(int channels, int mask)
    {
        if (channels <= 2) return $"{channels}ch";

        var names = new System.Collections.Generic.List<string>();
        for (int bit = 0; bit < 32; bit++)
        {
            if ((mask & (1 << bit)) == 0) continue;
            names.Add((1 << bit) switch
            {
                FL => "FL", FR => "FR", FC => "FC", LFE => "LFE",
                BL => "BL", BR => "BR", FLC => "FLC", FRC => "FRC",
                BC => "BC", SL => "SL", SR => "SR",
                _ => "ch" + bit
            });
        }

        if (names.Count == 0) return $"{channels}ch";
        return $"{channels}ch ({string.Join("|", names)})";
    }
}
