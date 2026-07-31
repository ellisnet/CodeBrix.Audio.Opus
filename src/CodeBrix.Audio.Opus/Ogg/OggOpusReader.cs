using System;
using System.Collections.Generic;
using System.IO;
using CodeBrix.Audio.Opus.Codec.Structs;

namespace CodeBrix.Audio.Opus.Ogg;

/// <summary>
/// Decodes an Ogg Opus stream to 48 kHz interleaved float samples, handling the two things about
/// Opus that no other format in CodeBrix.Audio requires: the pre-skip and the granule clock.
/// </summary>
/// <remarks>
/// <para>
/// An Opus stream ALWAYS decodes at 48 kHz. The rate in the identification header is the rate the
/// encoder was fed and is informational only (RFC 7845 section 5.1), so it never reaches the
/// decoder here.
/// </para>
/// <para>
/// Granule positions run on that same 48 kHz clock and INCLUDE the pre-skip - the encoder's
/// priming samples, which a decoder must discard. So the audible length of a stream is
/// (final granule - pre-skip), and the first samples decoded are thrown away.
/// </para>
/// </remarks>
internal sealed class OggOpusReader : IDisposable
{
    /// <summary>Opus decodes at this rate, always.</summary>
    public const int DecodeSampleRate = 48000;

    /// <summary>Largest frame Opus can produce: 120 ms at 48 kHz.</summary>
    private const int MaxFrameSamples = 5760;

    /// <summary>
    /// Samples to decode and throw away before a seek target, so the decoder has run long enough
    /// to be accurate. RFC 7845 section 4.2 asks for at least 80 ms of pre-roll after seeking.
    /// </summary>
    private const int PreRollSamples = 3840;

    private readonly Stream stream;
    private readonly bool leaveOpen;
    private readonly OggPageReader pages;
    private readonly OpusDecoder decoder;

    private float[] frameBuffer;
    private int frameOffset;
    private int frameCount;

    private long firstAudioPageOffset;
    private long samplesDelivered;
    private long granuleOfCurrentPosition;
    private bool endOfData;
    private bool disposed;

    /// <summary>Opens an Ogg Opus stream and reads its headers.</summary>
    /// <param name="stream">A readable stream positioned at the start of the file.</param>
    /// <param name="leaveOpen">When false the stream is disposed along with this reader.</param>
    /// <exception cref="InvalidDataException">The stream is not a usable Ogg Opus stream.</exception>
    public OggOpusReader(Stream stream, bool leaveOpen = true)
    {
        this.stream = stream ?? throw new ArgumentNullException(nameof(stream));
        this.leaveOpen = leaveOpen;

        pages = new OggPageReader(stream, leaveOpen: true);

        ReadHeaders();

        decoder = new OpusDecoder(DecodeSampleRate, Head.ChannelCount);
        frameBuffer = new float[MaxFrameSamples * Head.ChannelCount];

        TotalGranule = pages.ReadLastGranulePosition();
        granuleOfCurrentPosition = 0;
    }

    /// <summary>The identification header.</summary>
    public OpusHead Head { get; private set; }

    /// <summary>The comment header, or an empty one when the stream carries no tags.</summary>
    public OpusTags Tags { get; private set; } = new OpusTags();

    /// <summary>Channels in the decoded output.</summary>
    public int Channels => Head.ChannelCount;

    /// <summary>The final granule position, or -1 when the stream is not seekable.</summary>
    public long TotalGranule { get; private set; }

    /// <summary>
    /// Total audible samples per channel: the final granule less the pre-skip. -1 when unknown.
    /// </summary>
    public long TotalSamples =>
        TotalGranule < 0 ? -1 : Math.Max(0, TotalGranule - Head.PreSkip);

    /// <summary>Current position, in samples per channel from the start of the audible audio.</summary>
    public long Position => samplesDelivered;

    /// <summary>Reads decoded samples, interleaved, at 48 kHz.</summary>
    /// <param name="destination">The buffer to fill.</param>
    /// <returns>The number of samples written; 0 at the end of the stream.</returns>
    public int Read(Span<float> destination)
    {
        var written = 0;

        while (written < destination.Length)
        {
            if (frameOffset >= frameCount && !DecodeNextFrame()) break;

            var available = frameCount - frameOffset;
            var wanted = destination.Length - written;
            var take = Math.Min(available, wanted);

            // Never hand back more than the stream says exists: the final packet is padded out to
            // a whole frame by the encoder, and the last page's granule is what trims it.
            if (TotalSamples >= 0)
            {
                var remaining = (TotalSamples - samplesDelivered) * Channels;
                if (remaining <= 0) { endOfData = true; break; }
                if (take > remaining) take = (int)remaining;
            }

            frameBuffer.AsSpan(frameOffset, take).CopyTo(destination.Slice(written, take));

            frameOffset += take;
            written += take;
            samplesDelivered += take / Channels;
        }

        return written;
    }

    /// <summary>Seeks to a sample position, counted per channel from the start of the audio.</summary>
    /// <param name="sampleIndex">The target position.</param>
    /// <returns>True when the seek succeeded.</returns>
    public bool Seek(long sampleIndex)
    {
        if (!stream.CanSeek) return false;

        if (sampleIndex < 0) sampleIndex = 0;
        if (TotalSamples >= 0 && sampleIndex > TotalSamples) sampleIndex = TotalSamples;

        // Granule positions count the pre-skip, so the target on the file's clock sits that much
        // further along than the caller's sample index.
        var targetGranule = sampleIndex + Head.PreSkip;
        var searchGranule = Math.Max(0, targetGranule - PreRollSamples);

        if (!pages.SeekToGranule(searchGranule, firstAudioPageOffset, out var startGranule))
        {
            return false;
        }

        decoder.ResetState();
        frameOffset = frameCount = 0;
        endOfData = false;
        granuleOfCurrentPosition = startGranule;
        samplesDelivered = Math.Max(0, startGranule - Head.PreSkip);

        // Decode forward and throw away everything before the target.
        //
        // Count in AUDIBLE samples, not granule units. The two differ by the pre-skip, and
        // DecodeNextFrame has already dropped that - so subtracting it again here would land the
        // reader a pre-skip's worth too far into the stream on every seek, including a seek back
        // to the start.
        var toDiscard = sampleIndex - samplesDelivered;
        while (toDiscard > 0)
        {
            if (frameOffset >= frameCount && !DecodeNextFrame()) break;

            var availableFrames = (frameCount - frameOffset) / Channels;
            var take = (int)Math.Min(availableFrames, toDiscard);

            frameOffset += take * Channels;
            toDiscard -= take;
            samplesDelivered += take;
        }

        samplesDelivered = sampleIndex;
        return true;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;

        pages.Dispose();
        if (!leaveOpen) stream.Dispose();
    }

    private void ReadHeaders()
    {
        if (!pages.TryReadPacket(out var identification) ||
            !OpusHead.TryParse(identification.Data, out var head))
        {
            throw new InvalidDataException(
                "This stream does not start with an Opus identification header, so it is not an " +
                "Ogg Opus stream.");
        }

        if (!head.IsSupportedMapping)
        {
            throw new InvalidDataException(
                $"This Ogg Opus stream uses channel mapping family {head.ChannelMappingFamily} " +
                $"with {head.ChannelCount} channel(s). CodeBrix.Audio.Opus decodes mapping " +
                "family 0 - mono and stereo - which covers every ordinary Opus file; " +
                "multichannel Opus is not supported.");
        }

        Head = head;

        // The comment header follows on its own page. A stream without one is malformed, but the
        // audio is still perfectly decodable, so a missing or unreadable OpusTags is not fatal.
        if (pages.TryReadPacket(out var comment) && OpusTags.TryParse(comment.Data, out var tags))
        {
            Tags = tags;
        }

        firstAudioPageOffset = stream.CanSeek ? stream.Position : 0;
    }

    private bool DecodeNextFrame()
    {
        if (endOfData) return false;

        while (true)
        {
            if (!pages.TryReadPacket(out var packet))
            {
                endOfData = true;
                return false;
            }

            if (packet.Data.Length == 0) continue;

            int samplesPerChannel;

            try
            {
                samplesPerChannel = decoder.Decode(
                    packet.Data, frameBuffer, MaxFrameSamples, decode_fec: false);
            }
            catch (Exception ex)
            {
                throw new InvalidDataException(
                    "An Opus packet in this stream could not be decoded; the file is corrupt or " +
                    "truncated.", ex);
            }

            if (samplesPerChannel <= 0) continue;

            frameOffset = 0;
            frameCount = samplesPerChannel * Channels;
            granuleOfCurrentPosition += samplesPerChannel;

            // Drop the encoder's priming samples. They are counted by the granule positions but
            // are not audio anybody should hear - without this every file starts a few
            // milliseconds early, with a click.
            var priming = Head.PreSkip - (granuleOfCurrentPosition - samplesPerChannel);
            if (priming > 0)
            {
                var skip = (int)Math.Min(samplesPerChannel, priming);
                frameOffset = skip * Channels;

                if (frameOffset >= frameCount) continue;   // whole frame was priming
            }

            return true;
        }
    }
}
