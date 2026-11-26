namespace Wallfly.Transcription.Deepgram;

/// <summary>
/// Send discipline of one connection: a single send in flight, audio paced to at most 1.25× real time
/// while catching up, and <c>{"type":"KeepAlive"}</c> after 3 s without any send. KeepAlive and
/// CloseStream are control messages; they never advance the audio clock.
/// </summary>
public sealed class DeepgramSender(double keepAliveInterval = DeepgramSender.DefaultKeepAliveInterval)
{
    public const double DefaultKeepAliveInterval = 3;
    public const double CatchUpRate = 1.25;
    public const string KeepAliveMessage = "{\"type\":\"KeepAlive\"}";
    public const string CloseStreamMessage = "{\"type\":\"CloseStream\"}";

    public double KeepAliveInterval { get; } = keepAliveInterval;
    public bool InFlight { get; private set; }
    public double NextSend { get; private set; }
    public double LastSend { get; private set; }

    /// <summary>The connection is open: audio may go out at once, the KeepAlive clock starts now.</summary>
    public void Opened(double now) { LastSend = now; NextSend = now; }

    /// <summary>Audio pacing: the next packet may go out at <paramref name="now"/>.</summary>
    public bool AudioDue(double now) => now >= NextSend;

    /// <summary>Reserves the pacing slot of a packet of <paramref name="duration"/> seconds.</summary>
    public void AudioScheduled(double duration, double now) => NextSend = now + duration / CatchUpRate;

    public bool KeepAliveDue(double now) => now - LastSend >= KeepAliveInterval;

    /// <summary>Any message (audio or control) leaves now; nothing else may be sent until <see cref="Completed"/>.</summary>
    public void Began(double now) { InFlight = true; LastSend = now; }

    public void Completed() => InFlight = false;

    /// <summary>The connection is gone; an ambiguous in-flight send no longer blocks the next connection.</summary>
    public void Reset() => InFlight = false;
}
