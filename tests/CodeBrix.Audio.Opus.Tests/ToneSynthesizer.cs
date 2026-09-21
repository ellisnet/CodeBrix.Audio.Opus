using System;
using CodeBrix.Audio.Synth;

namespace CodeBrix.Audio.Opus.Tests;

/// <summary>
/// A MIDI synthesizer that needs no asset: one sine voice per sounding note, with a short fade in
/// and out so nothing clicks.
/// </summary>
/// <remarks>
/// The end-to-end tests here render MIDI and abc to .opus, and they have to do it with nothing but
/// CodeBrix.Audio and this package - no SoundFont, no sample library, no file fetched from
/// anywhere. A sine bank is enough: what those tests measure is that the render reaches the
/// encoder and comes back as audio of the right length, not how good it sounds.
/// </remarks>
internal sealed class ToneSynthesizer : IMidiSynthesizer
{
    private const int MaxVoices = 24;
    private const double TwoPi = Math.PI * 2.0;

    private readonly Voice[] voices = new Voice[MaxVoices];
    private readonly int sampleRate;
    private readonly float attackPerFrame;
    private readonly float releasePerFrame;

    private float masterVolume = 0.5F;

    /// <summary>Creates the synthesizer at a sample rate.</summary>
    /// <param name="sampleRate">The rate to render at, in Hz.</param>
    public ToneSynthesizer(int sampleRate)
    {
        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sampleRate), sampleRate, "The sample rate must be positive.");
        }

        this.sampleRate = sampleRate;

        // 5 ms in, 60 ms out. A tone that starts or stops at full amplitude clicks, and a click is
        // exactly the sort of broadband content that would make an encoding test measure the
        // click rather than the note.
        attackPerFrame = 1F / Math.Max(1F, sampleRate * 0.005F);
        releasePerFrame = 1F / Math.Max(1F, sampleRate * 0.060F);
    }

    /// <inheritdoc />
    public int SampleRate => sampleRate;

    /// <inheritdoc />
    public int BlockSize => 64;

    /// <inheritdoc />
    public int ActiveVoiceCount
    {
        get
        {
            var count = 0;

            foreach (var voice in voices)
            {
                if (voice.Active) count++;
            }

            return count;
        }
    }

    /// <inheritdoc />
    public float MasterVolume
    {
        get => masterVolume;
        set => masterVolume = value;
    }

    /// <inheritdoc />
    public void ProcessMidiMessage(int channel, int command, int data1, int data2)
    {
        // Every channel sounds the same way: this is a test instrument, and the sequences it plays
        // put their notes wherever the music put them.
        var status = command & 0xF0;

        if (status == 0x90 && data2 > 0)
        {
            NoteOn(data1, data2);
        }
        else if (status == 0x90 || status == 0x80)
        {
            // A note-on at velocity 0 is the other spelling of a note-off.
            NoteOff(data1);
        }
    }

    /// <inheritdoc />
    public void NoteOffAll(bool immediate)
    {
        for (var i = 0; i < voices.Length; i++)
        {
            ref var voice = ref voices[i];

            if (!voice.Active) continue;

            if (immediate)
            {
                voice.Active = false;
            }
            else
            {
                voice.Releasing = true;
            }
        }
    }

    /// <inheritdoc />
    public void Reset() => Array.Clear(voices);

    /// <inheritdoc />
    public void Render(Span<float> left, Span<float> right)
    {
        if (left.Length != right.Length)
        {
            throw new ArgumentException("The output buffers for the left and right must be the same length.");
        }

        for (var frame = 0; frame < left.Length; frame++)
        {
            var mixed = 0F;

            for (var i = 0; i < voices.Length; i++)
            {
                ref var voice = ref voices[i];

                if (!voice.Active) continue;

                if (voice.Releasing)
                {
                    voice.Level -= releasePerFrame * voice.Target;

                    if (voice.Level <= 0F)
                    {
                        voice.Active = false;
                        continue;
                    }
                }
                else if (voice.Level < voice.Target)
                {
                    voice.Level = Math.Min(voice.Target, voice.Level + (attackPerFrame * voice.Target));
                }

                mixed += voice.Level * (float)Math.Sin(voice.Phase);

                voice.Phase += voice.PhaseIncrement;
                if (voice.Phase >= TwoPi) voice.Phase -= TwoPi;
            }

            var sample = mixed * masterVolume;

            left[frame] = sample;
            right[frame] = sample;
        }
    }

    private void NoteOn(int note, int velocity)
    {
        var slot = FindSlot(note);
        if (slot < 0) return;

        ref var voice = ref voices[slot];

        voice.Active = true;
        voice.Releasing = false;
        voice.Note = note;
        voice.Phase = 0;
        voice.PhaseIncrement = TwoPi * Frequency(note) / sampleRate;
        voice.Level = 0F;
        voice.Target = 0.25F * (velocity / 127F);
    }

    private void NoteOff(int note)
    {
        for (var i = 0; i < voices.Length; i++)
        {
            ref var voice = ref voices[i];

            if (voice.Active && !voice.Releasing && voice.Note == note)
            {
                voice.Releasing = true;
            }
        }
    }

    private int FindSlot(int note)
    {
        // The same note again takes its own voice back, so a repeated note does not stack up.
        for (var i = 0; i < voices.Length; i++)
        {
            if (voices[i].Active && voices[i].Note == note) return i;
        }

        for (var i = 0; i < voices.Length; i++)
        {
            if (!voices[i].Active) return i;
        }

        return -1;
    }

    private static double Frequency(int note) => 440.0 * Math.Pow(2.0, (note - 69) / 12.0);

    private struct Voice
    {
        public bool Active;
        public bool Releasing;
        public int Note;
        public double Phase;
        public double PhaseIncrement;
        public float Level;
        public float Target;
    }
}
