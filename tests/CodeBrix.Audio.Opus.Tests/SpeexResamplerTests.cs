using System;
using CodeBrix.Audio.Opus.Codec.Common;
using SilverAssertions;
using SilverAssertions.Collections;
using SilverAssertions.Numeric;
using SilverAssertions.Primitives;
using Xunit;

namespace CodeBrix.Audio.Opus.Tests;

/// <summary>
/// Tests for the Speex resampler's kernel selection.
/// </summary>
/// <remarks>
/// The direct-sinc path has a vectorised kernel and a scalar one. The port used to pick between them at
/// random in Debug builds so that both got exercised; the choice is now an explicit, deterministic
/// constructor option, and these tests are what keeps the scalar kernel covered on SIMD hardware.
/// </remarks>
public class SpeexResamplerTests
{
    private const int InputRate = 48000;
    private const int OutputRate = 44100;
    private const int Quality = 5;
    private const int InputFrames = 48000;

    private static float[] Resample(bool allowSimd)
    {
        var input = new float[InputFrames];
        for (int i = 0; i < input.Length; i++)
        {
            input[i] = 0.5f * MathF.Sin(2 * MathF.PI * 440f * i / InputRate);
        }

        using var resampler = new SpeexResampler(1, InputRate, OutputRate, InputRate, OutputRate, Quality, allowSimd);
        var output = new float[InputFrames];
        int inLen = input.Length;
        int outLen = output.Length;
        resampler.Process(0, input, ref inLen, output, ref outLen);
        return output[..outLen];
    }

    [Fact]
    public void scalar_and_simd_kernels_produce_the_same_output()
    {
        //Arrange
        var simd = Resample(allowSimd: true);
        var scalar = Resample(allowSimd: false);

        //Act
        float maxDifference = 0f;
        for (int i = 0; i < Math.Min(simd.Length, scalar.Length); i++)
        {
            maxDifference = MathF.Max(maxDifference, MathF.Abs(simd[i] - scalar[i]));
        }

        //Assert
        scalar.Length.Should().Be(simd.Length);
        maxDifference.Should().BeLessThan(1e-4f);
    }

    [Fact]
    public void scalar_kernel_resamples_a_tone()
    {
        //Arrange + Act
        var output = Resample(allowSimd: false);

        //Assert
        double sumSquares = 0;
        for (int i = 0; i < output.Length; i++) sumSquares += output[i] * output[i];
        double rms = Math.Sqrt(sumSquares / output.Length);
        output.Length.Should().BeInRange(44000, 44100);
        rms.Should().BeInRange(0.33, 0.38);
    }

    [Fact]
    public void public_constructor_is_deterministic_across_instances()
    {
        //Arrange
        var first = Resample(allowSimd: true);
        var second = Resample(allowSimd: true);

        //Act + Assert
        second.Should().Equal(first);
    }
}
