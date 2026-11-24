using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Channels;

namespace VrcTranslator;

public sealed class IncomingAudio(bool strongerNoiseFilter = false)
{
    public const int SampleRate = 16000;
    public const int FrameMilliseconds = 20;
    public const int FrameBytes = SampleRate * FrameMilliseconds / 1000 * sizeof(short);
    private const int OnsetFrames = 3;
    private const int LeadFrames = 300 / FrameMilliseconds + OnsetFrames - 1;
    private const int TailMilliseconds = 160;
    private const int TailFrames = TailMilliseconds / FrameMilliseconds;
    private const int TaskFrames = 10000 / FrameMilliseconds;
    private static readonly TimeSpan PacketGrace = TimeSpan.FromMilliseconds(100);
    private static readonly long PacketGraceTicks = Stopwatch.Frequency * PacketGrace.Ticks / TimeSpan.TicksPerSecond;
    private static readonly long FrameTicks = Stopwatch.Frequency * FrameMilliseconds / 1000;
    private static readonly byte[] Silence = new byte[FrameBytes];
    private readonly int gateLevel = strongerNoiseFilter ? 260 : 130;
    private readonly Channel<Frame> frames = Channel.CreateBounded<Frame>(new BoundedChannelOptions(500)
    {
        SingleWriter = true, SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest
    });
    private long sequence;

    public void Push(byte[] pcm, long capturedAt)
    {
        if (pcm.Length != FrameBytes) throw new ArgumentException("Expected 20 ms of mono PCM.", nameof(pcm));
        frames.Writer.TryWrite(new(++sequence, pcm, capturedAt));
    }

    internal async Task<Utterance> WaitForSpeechAsync(long capturedSince, CancellationToken cancellation)
    {
        var lead = new Queue<Frame>(LeadFrames);
        Frame previous = default;
        int audible = 0;
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            var frame = await frames.Reader.ReadAsync(cancellation).ConfigureAwait(false);
            if (frame.CapturedAt < capturedSince) continue;
            if (previous.Number + 1 != frame.Number ||
                Stopwatch.GetElapsedTime(previous.CapturedAt, frame.CapturedAt) > PacketGrace)
            {
                lead.Clear();
                audible = 0;
            }
            previous = frame;
            audible = IsAudible(frame.Pcm) ? audible + 1 : 0;
            if (audible == OnsetFrames) return new(lead.ToArray(), frame);
            if (lead.Count == LeadFrames) lead.Dequeue();
            lead.Enqueue(frame);
        }
    }

    internal async IAsyncEnumerable<byte[]> ReadSpeechAsync(Utterance utterance, int sentenceSilenceMs,
        [EnumeratorCancellation] CancellationToken cancellation)
    {
        // Give the server enough silence to finish the sentence before ending an idle session.
        int idleMilliseconds = Math.Max(5000, sentenceSilenceMs + TailMilliseconds + FrameMilliseconds);
        int idleFrames = (idleMilliseconds + FrameMilliseconds - 1) / FrameMilliseconds;
        foreach (var frame in utterance.Lead) yield return frame.Pcm;
        yield return utterance.FirstAudible.Pcm;
        int count = utterance.Lead.Length + 1;
        int silent = 0;
        await foreach (var pcm in ReadTimedFramesAsync(utterance.FirstAudible, cancellation).ConfigureAwait(false))
        {
            silent = IsAudible(pcm) ? 0 : silent + 1;
            // Keep quiet syllable tails, then pass true silence so the server can recognize the pause.
            yield return silent <= TailFrames ? pcm : Silence;
            if (++count >= TaskFrames || silent >= idleFrames) yield break;
        }
    }

    private async IAsyncEnumerable<byte[]> ReadTimedFramesAsync(Frame previous, [EnumeratorCancellation] CancellationToken cancellation)
    {
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        Task<bool>? readable = null;
        Task? clockTick = null;
        int filled = 0;
        try
        {
            while (true)
            {
                cancellation.ThrowIfCancellationRequested();
                bool hasNext = frames.Reader.TryPeek(out var next);
                if (hasNext && next.Number != previous.Number + 1) throw Backlog();

                // Real frames carry their end time. While waiting, leave room for normal WASAPI batching.
                long gapTicks = hasNext ? next.CapturedAt - previous.CapturedAt - FrameTicks
                    : Stopwatch.GetTimestamp() - previous.CapturedAt - PacketGraceTicks;
                long missing = (gapTicks + FrameTicks / 2) / FrameTicks;
                while (filled < missing)
                {
                    cancellation.ThrowIfCancellationRequested();
                    filled++;
                    yield return Silence;
                }
                if (hasNext)
                {
                    // Peek keeps the first frame after a long pause in the channel if the task ends during padding.
                    if (!frames.Reader.TryRead(out next) || next.Number != previous.Number + 1) throw Backlog();
                    previous = next;
                    filled = 0;
                    yield return next.Pcm;
                    continue;
                }
                readable ??= frames.Reader.WaitToReadAsync(waiting.Token).AsTask();
                clockTick ??= Task.Delay(PacketGrace, waiting.Token);
                await Task.WhenAny(readable, clockTick).ConfigureAwait(false);
                if (readable.IsCompleted)
                {
                    if (!await readable.ConfigureAwait(false)) yield break;
                    readable = null;
                }
                if (clockTick.IsCompleted) clockTick = null;
            }
        }
        finally { waiting.Cancel(); }

        static ServiceException Backlog() => new("来话音频积压，已跳过当前片段。");
    }

    private bool IsAudible(byte[] pcm)
    {
        // A level gate at about -48 dBFS, or -42 dBFS with stronger filtering.
        var samples = MemoryMarshal.Cast<byte, short>(pcm);
        long energy = 0;
        foreach (short sample in samples) energy += sample * sample;
        return energy >= samples.Length * gateLevel * gateLevel;
    }

    internal readonly record struct Frame(long Number, byte[] Pcm, long CapturedAt);

    internal sealed record Utterance(Frame[] Lead, Frame FirstAudible)
    {
        public long StartTimestamp => (Lead.Length == 0 ? FirstAudible.CapturedAt : Lead[0].CapturedAt) -
            Stopwatch.Frequency * FrameMilliseconds / 1000;
    }
}
