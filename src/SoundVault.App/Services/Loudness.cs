namespace SoundVault.App.Services;

/// <summary>
/// ITU-R BS.1770-4 综合响度（LUFS）+ 归一化增益。
/// 比原来的 RMS 近似靠谱：先做 K 加权（高架 + 高通），再按 400ms 块带门限统计。
/// </summary>
public static class Loudness
{
    private const double TargetLufs = -18.0;   // 试听归一目标

    /// <summary>算综合响度（LUFS）。静音返回 -70。</summary>
    public static double IntegratedLufs(float[] samples, int rate, int channels, int maxSamples = 6_000_000)
    {
        if (samples.Length < 1000 || rate <= 0) return -70;

        int ch = Math.Max(1, channels);
        int take = Math.Min(samples.Length, maxSamples);
        take -= take % ch;

        var (b1, a1, b2, a2) = Coefficients(rate);

        // 逐声道 K 加权，累计每帧的平方和
        var filtered = new double[take];
        for (int c = 0; c < ch; c++)
        {
            double z1 = 0, z2 = 0, z3 = 0, z4 = 0;
            for (int i = c; i < take; i += ch)
            {
                double x = samples[i];

                double y1 = b1.B0 * x + z1;
                z1 = b1.B1 * x - a1.A1 * y1 + z2;
                z2 = b1.B2 * x - a1.A2 * y1;

                double y2 = b2.B0 * y1 + z3;
                z3 = b2.B1 * y1 - a2.A1 * y2 + z4;
                z4 = b2.B2 * y1 - a2.A2 * y2;

                filtered[i] = y2;
            }
        }

        // 400ms 块、75% 重叠
        int block = Math.Max(1, (int)(rate * 0.4));
        int hop = Math.Max(1, block / 4);

        var energies = new List<double>();
        for (int start = 0; start + block <= take; start += hop)
        {
            double sum = 0;
            for (int i = start; i < start + block; i++) sum += filtered[i] * filtered[i];
            double meanSquare = sum / block;
            if (meanSquare <= 0) continue;
            energies.Add(meanSquare);
        }

        if (energies.Count == 0) return -70;

        double Lufs(double meanSquare) => -0.691 + 10 * Math.Log10(meanSquare);

        // 绝对门限 -70
        var kept = energies.Where(e => Lufs(e) > -70).ToList();
        if (kept.Count == 0) return -70;

        // 相对门限：先求均值，再减 10 LU
        double relativeThreshold = Lufs(kept.Average()) - 10;
        var gated = kept.Where(e => Lufs(e) > relativeThreshold).ToList();
        if (gated.Count == 0) gated = kept;

        return Lufs(gated.Average());
    }

    /// <summary>归一化增益：把这段音频拉到目标响度，夹在 0.2–5 倍之间。</summary>
    public static float GainFor(float[] samples, int rate, int channels)
    {
        double lufs = IntegratedLufs(samples, rate, channels);
        if (lufs <= -69) return 1f;                     // 几乎静音，别放大底噪

        double db = TargetLufs - lufs;
        double gain = Math.Pow(10, db / 20.0);
        return (float)Math.Clamp(gain, 0.2, 5.0);
    }

    /// <summary>K 加权两级双二阶的系数（按采样率现算）。</summary>
    private static ((double B0, double B1, double B2) b1, (double A1, double A2) a1, (double B0, double B1, double B2) b2, (double A1, double A2) a2)
        Coefficients(int fs)
    {
        // 高架（pre-filter）
        const double shelfF0 = 1681.974450955533;
        const double shelfG = 3.999843853973347;
        const double shelfQ = 0.7071752369554196;

        double k1 = Math.Tan(Math.PI * shelfF0 / fs);
        double vh = Math.Pow(10, shelfG / 20);
        double vb = Math.Pow(vh, 0.4996667741545416);
        double a0 = 1 + k1 / shelfQ + k1 * k1;

        double b0 = (vh + vb * k1 / shelfQ + k1 * k1) / a0;
        double b1 = 2 * (k1 * k1 - vh) / a0;
        double b2 = (vh - vb * k1 / shelfQ + k1 * k1) / a0;
        double a1 = 2 * (k1 * k1 - 1) / a0;
        double a2 = (1 - k1 / shelfQ + k1 * k1) / a0;

        // 高通（RLB）
        const double hpF0 = 38.13547087613982;
        const double hpQ = 0.5003270373253953;

        double k2 = Math.Tan(Math.PI * hpF0 / fs);
        double d0 = 1 + k2 / hpQ + k2 * k2;

        double c0 = 1 / d0;
        double c1 = -2 / d0;
        double c2 = 1 / d0;
        double e1 = 2 * (k2 * k2 - 1) / d0;
        double e2 = (1 - k2 / hpQ + k2 * k2) / d0;

        return ((b0, b1, b2), (a1, a2), (c0, c1, c2), (e1, e2));
    }
}
