namespace VoiceSwitch.Windows.Core;

public sealed class Segmenter
{
    public const double Rate = 16000.0;
    public const int FrameLength = 480;

    private VoiceSwitchConfig config;
    private float floor = 0.001f;
    private readonly Queue<float[]> ring = new();
    private readonly List<float[]> utterance = new();
    private int ringSamples;
    private int utteranceSamples;
    private int silentSamples;
    private int silent;
    private bool skipping;

    public Segmenter(VoiceSwitchConfig config)
    {
        this.config = config;
    }

    public bool LastWasSpeech { get; private set; }
    public bool AdaptFloor { get; set; } = true;
    public bool HasOpenUtterance => utterance.Count > 0;

    public void Reset()
    {
        ring.Clear();
        utterance.Clear();
        ringSamples = 0;
        utteranceSamples = 0;
        silentSamples = 0;
        silent = 0;
        skipping = false;
        LastWasSpeech = false;
    }

    public SegmenterEvent? Push(float[] frame)
    {
        var preroll = Frames(config.PrerollMs ?? 300);
        var hangover = Frames(config.HangoverMs ?? 300);
        var hangoverSamples = Frames(config.HangoverMs ?? 300) * FrameLength;
        var minSamples = MsToSamples(config.MinSpeechMs ?? 300);
        var maxSamples = (int)((config.MaxSeconds ?? 2.5) * Rate);
        var speech = IsSpeech(frame);
        LastWasSpeech = speech;

        if (utterance.Count == 0)
        {
            ring.Enqueue(frame);
            ringSamples += frame.Length;
            while (ring.Count > preroll)
            {
                ringSamples -= ring.Dequeue().Length;
            }

            if (speech)
            {
                utterance.AddRange(ring);
                utteranceSamples = ringSamples;
                silent = 0;
                silentSamples = 0;
            }

            return null;
        }

        silent = speech ? 0 : silent + 1;
        silentSamples = speech ? 0 : silentSamples + frame.Length;
        SegmenterEvent? head = null;
        if (utteranceSamples <= maxSamples + hangoverSamples)
        {
            utterance.Add(frame);
            utteranceSamples += frame.Length;
        }
        else if (!skipping)
        {
            skipping = true;
            head = SegmenterEvent.Head(Flatten(utterance.Append(frame)));
        }

        if (silentSamples < hangoverSamples)
        {
            return head;
        }

        var done = !skipping && utteranceSamples - hangoverSamples >= minSamples
            ? SegmenterEvent.Utterance(Flatten(utterance))
            : null;
        utterance.Clear();
        ring.Clear();
        ringSamples = 0;
        utteranceSamples = 0;
        silentSamples = 0;
        skipping = false;
        return done ?? head;
    }

    public SegmenterEvent? Flush()
    {
        if (utterance.Count == 0)
        {
            return null;
        }

        var minSamples = MsToSamples(config.MinSpeechMs ?? 300);
        var done = !skipping && utteranceSamples >= minSamples
            ? SegmenterEvent.Utterance(Flatten(utterance))
            : null;
        utterance.Clear();
        ring.Clear();
        ringSamples = 0;
        utteranceSamples = 0;
        silentSamples = 0;
        silent = 0;
        skipping = false;
        LastWasSpeech = false;
        return done;
    }

    public static int Frames(int ms) =>
        Math.Max(1, ms * (int)Rate / 1000 / FrameLength);

    private static int MsToSamples(int ms) =>
        Math.Max(1, (int)Math.Round(ms * Rate / 1000.0, MidpointRounding.AwayFromZero));

    private bool IsSpeech(float[] frame)
    {
        var rms = MathF.Sqrt(frame.Aggregate(0f, (sum, sample) => sum + sample * sample) / frame.Length);
        var speech = rms > Math.Max(floor * (config.VadRatio ?? 3f), config.VadMinRMS ?? 0.005f);
        if (!speech && AdaptFloor)
        {
            floor = floor * 0.95f + rms * 0.05f;
        }

        return speech;
    }

    private static float[] Flatten(IEnumerable<float[]> frames) =>
        frames.SelectMany(frame => frame).ToArray();
}

public sealed record SegmenterEvent(string Kind, float[] Samples)
{
    public static SegmenterEvent Utterance(float[] samples) => new("utterance", samples);
    public static SegmenterEvent Head(float[] samples) => new("head", samples);
}
