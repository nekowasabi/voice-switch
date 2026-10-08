using System.Collections.Immutable;
using VoiceSwitch.Windows.Core;

namespace VoiceSwitch.Windows;

public sealed record AnalysisFrame(long Start, ImmutableArray<short> Original, ImmutableArray<short> Analysis);

public sealed record NoiseProcessorStatus(
    NoiseReductionMode Mode,
    bool Calibrated,
    long ProcessedSamples,
    int Saturations,
    int AvailabilityDelaySamples,
    int PublicationLagSamples,
    int RetainedInputSamples,
    int RetainedOlaSamples,
    int InternalStateHighWaterSamples,
    int InternalStateSampleBudget);

public sealed class NoiseProcessor
{
    public const int WindowLength = 320;
    public const int HopLength = 160;
    public const int FftLength = 512;
    public const int InternalStateSampleBudget = 4096;
    private const int SpeechHangoverHops = 30;
    private const int CalibrationQuietHops = 50;
    private const double NoiseUpdateBeta = 0.9900498337491681;
    private const double PriorAlpha = 0.96;
    private const double Epsilon = 1e-24;
    private static readonly double[] SqrtHann = BuildSqrtHann();

    private readonly NoiseReductionOptions options;
    private readonly Action<AnalysisFrame> emit;
    private readonly double vadMinRms;
    private readonly double minGain;
    private readonly List<short> input = new();
    private readonly List<short> originalBlock = new(Segmenter.FrameLength);
    private readonly List<short> analysisBlock = new(Segmenter.FrameLength);
    private readonly double[] real = new double[FftLength];
    private readonly double[] imag = new double[FftLength];
    private readonly double[] power = new double[FftLength / 2 + 1];
    private readonly double[] noisePsd = new double[FftLength / 2 + 1];
    private readonly double[] candidatePsd = new double[FftLength / 2 + 1];
    private readonly double[] previousEnhancedPower = new double[FftLength / 2 + 1];
    private readonly double[] previousGain = new double[FftLength / 2 + 1];
    private readonly double[] gainByBin = new double[FftLength / 2 + 1];
    private readonly double[] ola = new double[InternalStateSampleBudget];
    private readonly double[] denominator = new double[InternalStateSampleBudget];
    private long absoluteStart;
    private long inputBase;
    private long olaBase;
    private long olaWrittenExclusive;
    private long nextInputStart;
    private long nextWindowStart;
    private long nextEmitIndex;
    private long outputBlockStart;
    private int retainedInputHighWater;
    private int retainedOlaHighWater;
    private int internalStateHighWater;
    private bool completed;
    private bool calibrated;
    private int candidateCount;
    private int quietRunAfterHangover;
    private int speechHangover;
    private int saturations;

    public NoiseProcessor(NoiseReductionOptions? options, Action<AnalysisFrame> emit, double vadMinRms)
    {
        this.options = options ?? new NoiseReductionOptions();
        this.emit = emit;
        this.vadMinRms = vadMinRms > 0 ? vadMinRms : 0.005;
        minGain = Math.Pow(10.0, -Math.Clamp(this.options.MaxAttenuationDb, 0, 6) / 20.0);
        Reset(0);
    }

    public NoiseProcessorStatus Status => new(
        options.Mode,
        calibrated,
        Math.Max(0, nextEmitIndex - absoluteStart),
        saturations,
        options.Mode == NoiseReductionMode.Off ? 0 : HopLength,
        options.Mode == NoiseReductionMode.Off ? 0 : Segmenter.FrameLength,
        input.Count,
        RetainedOlaSamples,
        internalStateHighWater,
        InternalStateSampleBudget);

    public void Reset(long start)
    {
        input.Clear();
        Array.Clear(ola);
        Array.Clear(denominator);
        Array.Clear(noisePsd);
        Array.Clear(candidatePsd);
        Array.Clear(previousEnhancedPower);
        Array.Fill(previousGain, 1.0);
        originalBlock.Clear();
        analysisBlock.Clear();
        absoluteStart = start;
        inputBase = start;
        olaBase = start;
        olaWrittenExclusive = start;
        nextInputStart = start;
        nextWindowStart = start - HopLength;
        nextEmitIndex = start;
        outputBlockStart = start;
        retainedInputHighWater = 0;
        retainedOlaHighWater = 0;
        internalStateHighWater = 0;
        completed = false;
        calibrated = false;
        candidateCount = 0;
        quietRunAfterHangover = 0;
        speechHangover = 0;
        saturations = 0;
    }

    public void Push(PcmFrame frame)
    {
        if (completed)
        {
            throw new InvalidOperationException("cannot push PCM after noise processor completion.");
        }

        if (frame.Start != nextInputStart)
        {
            throw new InvalidOperationException($"PCM discontinuity at {frame.Start}; expected {nextInputStart}.");
        }

        nextInputStart += frame.Samples.Length;
        if (options.Mode == NoiseReductionMode.Off)
        {
            emit(new AnalysisFrame(frame.Start, frame.Samples, frame.Samples));
            nextEmitIndex = frame.Start + frame.Samples.Length;
            return;
        }

        nextInputStart = frame.Start;
        var offset = 0;
        while (offset < frame.Samples.Length)
        {
            var count = Math.Min(Segmenter.FrameLength, frame.Samples.Length - offset);
            AppendInput(frame.Samples, offset, count);
            nextInputStart += count;
            ProcessAvailable(eof: false);
            EvictFinalized();
            offset += count;
        }
    }

    public void Complete()
    {
        if (completed)
        {
            return;
        }

        completed = true;
        if (options.Mode != NoiseReductionMode.Off)
        {
            ProcessAvailable(eof: true);
            EmitFinalized(nextInputStart);
            EvictFinalized();
            EmitPendingBlock();
        }
    }

    internal static void FftForTest(double[] real, double[] imag, bool inverse) => Transform(real, imag, inverse);

    private void ProcessAvailable(bool eof)
    {
        while (nextWindowStart < nextInputStart
            && (eof || nextWindowStart + WindowLength <= nextInputStart))
        {
            ProcessWindow(nextWindowStart, eof);
            nextWindowStart += HopLength;
            EmitFinalized(Math.Min(nextInputStart, Math.Max(absoluteStart, nextWindowStart)));
            EvictFinalized();
        }
    }

    private void ProcessWindow(long start, bool eof)
    {
        Array.Clear(real);
        Array.Clear(imag);
        var fullWindow = start >= absoluteStart && start + WindowLength <= nextInputStart;
        var energy = 0.0;
        var count = 0;
        for (var i = 0; i < WindowLength; i++)
        {
            var index = start + i;
            var sample = TryGetInputSample(index, out var pcm) ? pcm / 32768.0 : 0.0;
            real[i] = sample * SqrtHann[i];
            if (index >= absoluteStart && index < nextInputStart)
            {
                energy += sample * sample;
                count++;
            }
        }

        var rms = count == 0 ? 0.0 : Math.Sqrt(energy / count);
        Transform(real, imag, inverse: false);
        for (var k = 0; k <= FftLength / 2; k++)
        {
            power[k] = real[k] * real[k] + imag[k] * imag[k];
        }

        var trainable = fullWindow && !eof;
        UpdateNoiseState(rms, trainable);
        ApplyGain(rms >= vadMinRms);
        Transform(real, imag, inverse: true);
        for (var i = 0; i < WindowLength; i++)
        {
            var index = start + i;
            if (index < absoluteStart)
            {
                continue;
            }

            AddOla(index, real[i] * SqrtHann[i], SqrtHann[i] * SqrtHann[i]);
        }
    }

    private void UpdateNoiseState(double rms, bool trainable)
    {
        if (!trainable)
        {
            return;
        }

        var speech = rms >= vadMinRms;
        if (speech)
        {
            speechHangover = SpeechHangoverHops;
            quietRunAfterHangover = 0;
            candidateCount = 0;
            Array.Clear(candidatePsd);
            return;
        }

        if (speechHangover > 0)
        {
            speechHangover--;
            quietRunAfterHangover = 0;
            return;
        }

        quietRunAfterHangover++;
        if (!calibrated)
        {
            for (var k = 0; k < candidatePsd.Length; k++)
            {
                candidatePsd[k] += power[k];
            }

            candidateCount++;
            if (quietRunAfterHangover >= CalibrationQuietHops && candidateCount > 0)
            {
                for (var k = 0; k < noisePsd.Length; k++)
                {
                    noisePsd[k] = candidatePsd[k] / candidateCount;
                }

                calibrated = true;
            }

            return;
        }

        for (var k = 0; k < noisePsd.Length; k++)
        {
            noisePsd[k] = NoiseUpdateBeta * noisePsd[k] + (1.0 - NoiseUpdateBeta) * power[k];
        }
    }

    private void ApplyGain(bool speech)
    {
        for (var k = 0; k <= FftLength / 2; k++)
        {
            gainByBin[k] = GainForBin(k, speech);
        }

        for (var k = 0; k < FftLength; k++)
        {
            var bin = k <= FftLength / 2 ? k : FftLength - k;
            var gain = gainByBin[bin];
            real[k] *= gain;
            imag[k] *= gain;
        }
    }

    private double GainForBin(int bin, bool speech)
    {
        if (!calibrated || noisePsd[bin] <= Epsilon)
        {
            previousGain[bin] = 1.0;
            previousEnhancedPower[bin] = power[bin];
            return 1.0;
        }

        var noise = Math.Max(noisePsd[bin], Epsilon);
        var gamma = power[bin] / noise;
        var instantPrior = Math.Max(gamma - 1.0, 0.0);
        var prior = PriorAlpha * (previousEnhancedPower[bin] / noise) + (1.0 - PriorAlpha) * instantPrior;
        if (speech)
        {
            prior = Math.Max(prior, instantPrior);
        }

        var gain = prior <= 0 ? 0.0 : prior / (1.0 + prior);
        gain = Math.Clamp(gain, minGain, 1.0);
        previousGain[bin] = gain;
        previousEnhancedPower[bin] = gain * gain * power[bin];
        return gain;
    }

    private void EmitFinalized(long finalizedExclusive)
    {
        var limit = Math.Min(finalizedExclusive, nextInputStart);
        while (nextEmitIndex < limit)
        {
            if (originalBlock.Count == 0)
            {
                outputBlockStart = nextEmitIndex;
            }

            var value = ReadOlaValue(nextEmitIndex);
            originalBlock.Add(ReadInputSample(nextEmitIndex));
            analysisBlock.Add(ToPcm16(value));
            nextEmitIndex++;
            if (analysisBlock.Count == Segmenter.FrameLength)
            {
                EmitPendingBlock();
            }
        }
    }

    private void EmitPendingBlock()
    {
        if (analysisBlock.Count == 0)
        {
            return;
        }

        emit(new AnalysisFrame(
            outputBlockStart,
            originalBlock.ToImmutableArray(),
            analysisBlock.ToImmutableArray()));
        originalBlock.Clear();
        analysisBlock.Clear();
    }

    private void AppendInput(ImmutableArray<short> samples, int offset, int count)
    {
        for (var i = 0; i < count; i++)
        {
            input.Add(samples[offset + i]);
        }

        UpdateInternalStateHighWater();
        if (input.Count > InternalStateSampleBudget)
        {
            throw new InvalidOperationException($"noise processor retained {input.Count} input samples; budget is {InternalStateSampleBudget}.");
        }
    }

    private bool TryGetInputSample(long index, out short sample)
    {
        var relative = index - inputBase;
        if (relative >= 0 && relative < input.Count)
        {
            sample = input[(int)relative];
            return true;
        }

        sample = 0;
        return false;
    }

    private short ReadInputSample(long index)
    {
        if (!TryGetInputSample(index, out var sample))
        {
            throw new InvalidOperationException($"noise processor input sample {index} was evicted before publication.");
        }

        return sample;
    }

    private void EvictFinalized()
    {
        var inputSafeExclusive = Math.Min(nextEmitIndex, nextWindowStart);
        if (inputSafeExclusive > inputBase)
        {
            var remove = (int)Math.Min(input.Count, inputSafeExclusive - inputBase);
            input.RemoveRange(0, remove);
            inputBase += remove;
        }

        AdvanceOlaBase(nextEmitIndex);
    }

    private void AddOla(long index, double value, double weight)
    {
        EnsureOlaContains(index);
        var slot = OlaSlot(index);
        ola[slot] += value;
        denominator[slot] += weight;
        olaWrittenExclusive = Math.Max(olaWrittenExclusive, index + 1);
        UpdateInternalStateHighWater();
    }

    private double ReadOlaValue(long index)
    {
        if (index < olaBase || index >= olaBase + ola.Length)
        {
            throw new InvalidOperationException($"noise processor OLA sample {index} is outside retained window.");
        }

        var slot = OlaSlot(index);
        return denominator[slot] > Epsilon ? ola[slot] / denominator[slot] : 0.0;
    }

    private void EnsureOlaContains(long index)
    {
        if (index < olaBase)
        {
            throw new InvalidOperationException($"noise processor OLA sample {index} was already finalized.");
        }

        if (index < olaBase + ola.Length)
        {
            return;
        }

        var targetBase = index - ola.Length + 1;
        if (targetBase > nextEmitIndex)
        {
            throw new InvalidOperationException($"noise processor OLA backlog exceeded {ola.Length} samples.");
        }

        AdvanceOlaBase(targetBase);
    }

    private void AdvanceOlaBase(long targetBase)
    {
        if (targetBase <= olaBase)
        {
            return;
        }

        var delta = targetBase - olaBase;
        if (delta >= ola.Length)
        {
            Array.Clear(ola);
            Array.Clear(denominator);
        }
        else
        {
            for (var index = olaBase; index < targetBase; index++)
            {
                var slot = OlaSlot(index);
                ola[slot] = 0.0;
                denominator[slot] = 0.0;
            }
        }

        olaBase = targetBase;
        if (olaWrittenExclusive < olaBase)
        {
            olaWrittenExclusive = olaBase;
        }
    }

    private int OlaSlot(long index)
    {
        var slot = index % ola.Length;
        return (int)(slot < 0 ? slot + ola.Length : slot);
    }

    private int RetainedOlaSamples => (int)Math.Min(int.MaxValue, Math.Max(0, olaWrittenExclusive - olaBase));

    private void UpdateInternalStateHighWater()
    {
        retainedInputHighWater = Math.Max(retainedInputHighWater, input.Count);
        retainedOlaHighWater = Math.Max(retainedOlaHighWater, RetainedOlaSamples);
        internalStateHighWater = Math.Max(internalStateHighWater, Math.Max(retainedInputHighWater, retainedOlaHighWater));
    }

    private short ToPcm16(double value)
    {
        if (!double.IsFinite(value))
        {
            value = 0;
        }

        var scaled = value * 32768.0;
        if (scaled > short.MaxValue)
        {
            saturations++;
            return short.MaxValue;
        }

        if (scaled < short.MinValue)
        {
            saturations++;
            return short.MinValue;
        }

        return (short)Math.Round(scaled, MidpointRounding.AwayFromZero);
    }

    private static double[] BuildSqrtHann()
    {
        var window = new double[WindowLength];
        for (var i = 0; i < window.Length; i++)
        {
            window[i] = Math.Sqrt(0.5 - 0.5 * Math.Cos(2.0 * Math.PI * i / WindowLength));
        }

        return window;
    }

    private static void Transform(double[] real, double[] imag, bool inverse)
    {
        if (real.Length != FftLength || imag.Length != FftLength)
        {
            throw new ArgumentException("FFT buffers must be 512 samples.");
        }

        var j = 0;
        for (var i = 1; i < FftLength; i++)
        {
            var bit = FftLength >> 1;
            for (; (j & bit) != 0; bit >>= 1)
            {
                j ^= bit;
            }

            j ^= bit;
            if (i < j)
            {
                (real[i], real[j]) = (real[j], real[i]);
                (imag[i], imag[j]) = (imag[j], imag[i]);
            }
        }

        for (var length = 2; length <= FftLength; length <<= 1)
        {
            var angle = 2.0 * Math.PI / length * (inverse ? 1.0 : -1.0);
            var wlenReal = Math.Cos(angle);
            var wlenImag = Math.Sin(angle);
            for (var i = 0; i < FftLength; i += length)
            {
                var wReal = 1.0;
                var wImag = 0.0;
                for (var k = 0; k < length / 2; k++)
                {
                    var uReal = real[i + k];
                    var uImag = imag[i + k];
                    var vReal = real[i + k + length / 2] * wReal - imag[i + k + length / 2] * wImag;
                    var vImag = real[i + k + length / 2] * wImag + imag[i + k + length / 2] * wReal;
                    real[i + k] = uReal + vReal;
                    imag[i + k] = uImag + vImag;
                    real[i + k + length / 2] = uReal - vReal;
                    imag[i + k + length / 2] = uImag - vImag;
                    var nextReal = wReal * wlenReal - wImag * wlenImag;
                    wImag = wReal * wlenImag + wImag * wlenReal;
                    wReal = nextReal;
                }
            }
        }

        if (inverse)
        {
            for (var i = 0; i < FftLength; i++)
            {
                real[i] /= FftLength;
                imag[i] /= FftLength;
            }
        }
    }
}
