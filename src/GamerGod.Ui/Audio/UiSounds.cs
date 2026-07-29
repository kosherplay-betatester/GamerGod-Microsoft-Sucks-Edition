using System;
using System.IO;
using System.Media;

namespace GamerGod.Ui.Audio;

public enum UiSound
{
    /// <summary>Game Mode turned on. A rising interval — an instrument arming.</summary>
    Arm,

    /// <summary>Game Mode turned off. The same interval falling, so the pair reads as a cycle.</summary>
    Disarm,

    /// <summary>Navigation and hover. Barely there by design.</summary>
    Tick,

    /// <summary>A button did something.</summary>
    Confirm,

    /// <summary>Something needs attention. Low, not shrill.</summary>
    Alert,
}

/// <summary>
/// GamerGod's sound design, synthesised at runtime.
///
/// <para>
/// No audio files ship with the product. Every sound is a few sine partials with an envelope,
/// generated into a WAV in memory the first time it is needed. That keeps the download
/// smaller, keeps the repository free of binary assets nobody can review, and — the reason
/// that actually matters — means the sounds are <em>designed in code</em> and can be tuned by
/// changing a frequency rather than by opening an audio editor.
/// </para>
///
/// <para>
/// The palette is deliberately restrained. This is an instrument, not a game menu: short,
/// soft, low-mid frequencies, no reverb, nothing that would grate on the hundredth hearing.
/// Every sound is off unless the user turned audio on.
/// </para>
/// </summary>
public sealed class UiSounds
{
    private const int SampleRate = 44100;

    private readonly Dictionary<(UiSound Sound, int Volume), byte[]> _cache = [];
    private readonly Lock _gate = new();

    /// <summary>Master switch. When false, nothing is synthesised and nothing plays.</summary>
    public bool Enabled { get; set; }

    /// <summary>0 to 100. Applied at synthesis time, so it costs nothing at playback.</summary>
    public int Volume { get; set; } = 45;

    public void Play(UiSound sound)
    {
        if (!Enabled || Volume <= 0)
        {
            return;
        }

        try
        {
            var wav = Render(sound, Volume);

            // Fire and forget. A UI sound that blocks the thread that drew the frame is worse
            // than no UI sound, and this is a performance tool.
            using var stream = new MemoryStream(wav, writable: false);
            using var player = new SoundPlayer(stream);
            player.Play();
        }
        catch (Exception)
        {
            // No audio device, an exclusive-mode application holding the endpoint, a muted
            // session. None of these are worth surfacing: the user asked for a sound, not for
            // a dialog about why there wasn't one.
        }
    }

    private byte[] Render(UiSound sound, int volume)
    {
        lock (_gate)
        {
            if (_cache.TryGetValue((sound, volume), out var cached))
            {
                return cached;
            }

            var gain = volume / 100.0 * 0.5;   // headroom: never render at full scale
            var samples = sound switch
            {
                // A perfect fifth, rising. Reads as "ready" rather than "success", which is
                // the right claim — GamerGod applied changes, it did not win you frames.
                UiSound.Arm => Sequence(
                    Tone(392.00, 0.055, gain, 0.004, 0.030),
                    Tone(587.33, 0.085, gain, 0.004, 0.055)),

                // The same interval inverted, so on and off are audibly a pair.
                UiSound.Disarm => Sequence(
                    Tone(587.33, 0.055, gain, 0.004, 0.030),
                    Tone(392.00, 0.090, gain * 0.85, 0.004, 0.060)),

                // Deliberately almost inaudible. It should register as texture, not as a beep.
                UiSound.Tick => Tone(1_320.00, 0.014, gain * 0.30, 0.001, 0.010),

                UiSound.Confirm => Tone(783.99, 0.045, gain * 0.65, 0.003, 0.030),

                // Low and doubled. Attention without alarm — nothing here is an emergency.
                UiSound.Alert => Sequence(
                    Tone(261.63, 0.070, gain * 0.9, 0.004, 0.045),
                    Silence(0.045),
                    Tone(261.63, 0.070, gain * 0.9, 0.004, 0.045)),

                _ => Silence(0.01),
            };

            var wav = ToWav(samples);
            _cache[(sound, volume)] = wav;
            return wav;
        }
    }

    /// <summary>
    /// A single tone with a soft attack and decay. The envelope is the whole trick: a raw sine
    /// that starts and stops abruptly produces an audible click at each edge, which is exactly
    /// the cheap-sounding artefact that makes synthesised UI audio feel amateurish.
    /// </summary>
    private static double[] Tone(
        double frequency, double seconds, double gain, double attackSeconds, double decaySeconds)
    {
        var count = (int)(SampleRate * seconds);
        var buffer = new double[count];

        var attack = Math.Max(1, (int)(SampleRate * attackSeconds));
        var decay = Math.Max(1, (int)(SampleRate * decaySeconds));

        for (var i = 0; i < count; i++)
        {
            var t = i / (double)SampleRate;

            // A quiet second harmonic keeps it from sounding like a test tone without making
            // it musical enough to notice.
            var wave = Math.Sin(2 * Math.PI * frequency * t)
                       + (0.18 * Math.Sin(4 * Math.PI * frequency * t));

            var envelope = 1.0;
            if (i < attack)
            {
                envelope = i / (double)attack;
            }
            else if (i > count - decay)
            {
                envelope = Math.Max(0, (count - i) / (double)decay);
            }

            // Cosine-shaped edges rather than linear: linear ramps still leave a faint edge.
            envelope = 0.5 - (0.5 * Math.Cos(Math.PI * Math.Clamp(envelope, 0, 1)));

            buffer[i] = wave * envelope * gain / 1.18;
        }

        return buffer;
    }

    private static double[] Silence(double seconds) => new double[(int)(SampleRate * seconds)];

    private static double[] Sequence(params double[][] parts)
    {
        var total = 0;
        foreach (var part in parts)
        {
            total += part.Length;
        }

        var buffer = new double[total];
        var offset = 0;

        foreach (var part in parts)
        {
            Array.Copy(part, 0, buffer, offset, part.Length);
            offset += part.Length;
        }

        return buffer;
    }

    /// <summary>16-bit mono PCM in a minimal RIFF container — what SoundPlayer wants.</summary>
    private static byte[] ToWav(double[] samples)
    {
        var dataBytes = samples.Length * 2;

        using var memory = new MemoryStream(44 + dataBytes);
        using var writer = new BinaryWriter(memory);

        writer.Write("RIFF"u8);
        writer.Write(36 + dataBytes);
        writer.Write("WAVE"u8);

        writer.Write("fmt "u8);
        writer.Write(16);                        // PCM header size
        writer.Write((short)1);                  // PCM
        writer.Write((short)1);                  // mono
        writer.Write(SampleRate);
        writer.Write(SampleRate * 2);            // byte rate
        writer.Write((short)2);                  // block align
        writer.Write((short)16);                 // bits per sample

        writer.Write("data"u8);
        writer.Write(dataBytes);

        foreach (var sample in samples)
        {
            // Clamped before scaling. Without this, a summed harmonic can exceed unity and
            // wrap to the opposite extreme, which is heard as a hard crackle.
            var clamped = Math.Clamp(sample, -1.0, 1.0);
            writer.Write((short)(clamped * short.MaxValue));
        }

        writer.Flush();
        return memory.ToArray();
    }
}
